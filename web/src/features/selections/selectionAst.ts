export type ValueKind = 'int' | 'decimal' | 'string' | 'boolean' | 'date' | 'time' | 'dateTime' | 'guid';

export type TypedValue = { kind: ValueKind; value: string | number | boolean };

export type ColumnRef = { alias: string; name: string; valueKind: ValueKind };

export type Operator = 'equal' | 'notEqual' | 'greaterThan' | 'greaterOrEqual' | 'lessThan' | 'lessOrEqual' | 'between' | 'in' | 'isNull' | 'isNotNull' | 'contains' | 'startsWith' | 'endsWith' | 'dateRange' | 'timeRange' | 'dateTimeRange';

export type Predicate =
  | { kind: 'and'; terms: readonly Predicate[] }
  | { kind: 'or'; terms: readonly Predicate[] }
  | { kind: 'not'; term: Predicate }
  | { kind: 'comparison'; column: ColumnRef; operator: 'equal' | 'notEqual' | 'greaterThan' | 'greaterOrEqual' | 'lessThan' | 'lessOrEqual'; value: TypedValue }
  | { kind: 'between'; column: ColumnRef; lower: TypedValue; upper: TypedValue }
  | { kind: 'set'; column: ColumnRef; negated: boolean; values: readonly TypedValue[] }
  | { kind: 'null'; column: ColumnRef; negated: boolean }
  | { kind: 'text'; column: ColumnRef; match: 'contains' | 'startsWith' | 'endsWith'; value: TypedValue }
  | { kind: 'boolean'; column: ColumnRef; value: TypedValue }
  | { kind: 'temporalRange'; column: ColumnRef; temporalKind: 'date' | 'time' | 'dateTime'; lower: TypedValue; upper: TypedValue }
  | { kind: 'exists'; tableId: string; alias: string; correlations: readonly { outer: ColumnRef; innerColumn: string }[]; predicate: Predicate | null; negated: boolean };

export type Join =
  | { kind: 'foreignKey'; fromAlias: string; alias: string; foreignKeyId: string; direction: 'forward' | 'reverse' }
  | { kind: 'manual'; fromAlias: string; tableId: string; alias: string; pairs: readonly { fromColumn: string; toColumn: string }[] };

export type VisualSelection = {
  root: { tableId: string; alias: string; stableKey: readonly string[] };
  joins: readonly Join[];
  predicate: Predicate | null;
};

export type SelectionSchema = {
  tables: readonly { tableId: string; stableKey: readonly string[] | null; columns: readonly { name: string; valueKind: ValueKind }[] }[];
  foreignKeys: readonly { foreignKeyId: string; childTableId: string; parentTableId: string }[];
};

const operators: Record<ValueKind, readonly Operator[]> = {
  int: ['equal', 'notEqual', 'greaterThan', 'greaterOrEqual', 'lessThan', 'lessOrEqual', 'between', 'in', 'isNull', 'isNotNull'],
  decimal: ['equal', 'notEqual', 'greaterThan', 'greaterOrEqual', 'lessThan', 'lessOrEqual', 'between', 'in', 'isNull', 'isNotNull'],
  string: ['equal', 'notEqual', 'in', 'isNull', 'isNotNull', 'contains', 'startsWith', 'endsWith'],
  boolean: ['equal', 'notEqual', 'isNull', 'isNotNull'],
  date: ['equal', 'notEqual', 'between', 'isNull', 'isNotNull', 'dateRange'],
  time: ['equal', 'notEqual', 'between', 'isNull', 'isNotNull', 'timeRange'],
  dateTime: ['equal', 'notEqual', 'between', 'isNull', 'isNotNull', 'dateTimeRange'],
  guid: ['equal', 'notEqual', 'in', 'isNull', 'isNotNull'],
};

export function replacePredicate(selection: VisualSelection, predicate: Predicate | null): VisualSelection {
  return { ...selection, predicate };
}

export function addJoin(selection: VisualSelection, join: Join): VisualSelection {
  return { ...selection, joins: [...selection.joins, join] };
}

export function selectionFingerprint(selection: VisualSelection): string {
  return JSON.stringify(selection);
}

export function operatorsFor(valueKind: ValueKind): readonly Operator[] {
  return operators[valueKind];
}

export function validateVisualSelection(selection: VisualSelection, schema: SelectionSchema): readonly string[] {
  const errors: string[] = [];
  const root = schema.tables.find((table) => table.tableId === selection.root.tableId);

  if (root === undefined) {
    errors.push('Root table is unknown.');
    return Object.freeze(errors);
  }

  if (!sameColumns(root.stableKey, selection.root.stableKey)) {
    errors.push('Root stable key must match the schema order.');
  }

  const aliases = new Map<string, SchemaTable>();
  addAlias(errors, aliases, selection.root.alias, root);
  for (const join of selection.joins) {
    validateJoin(errors, aliases, join, schema);
  }
  if (selection.predicate !== null) {
    validatePredicate(errors, aliases, selection.predicate, schema);
  }

  return Object.freeze(errors);
}

type SchemaTable = SelectionSchema['tables'][number];

function sameColumns(expected: readonly string[] | null, actual: readonly string[]): boolean {
  if (expected === null || expected.length !== actual.length) {
    return false;
  }

  return expected.every((column, index) => column === actual[index]);
}

function addAlias(errors: string[], aliases: Map<string, SchemaTable>, alias: string, table: SchemaTable): void {
  if (!/^[A-Za-z_][A-Za-z0-9_]*$/.test(alias)) {
    errors.push(`Alias "${alias}" is invalid.`);
    return;
  }
  if (aliases.has(alias)) {
    errors.push(`Alias "${alias}" is already in use.`);
    return;
  }

  aliases.set(alias, table);
}

function validateJoin(errors: string[], aliases: Map<string, SchemaTable>, join: Join, schema: SelectionSchema): void {
  const source = aliases.get(join.fromAlias);
  if (source === undefined) {
    errors.push(`Join source alias "${join.fromAlias}" is unknown.`);
    return;
  }

  if (join.kind === 'foreignKey') {
    const foreignKey = schema.foreignKeys.find((item) => item.foreignKeyId === join.foreignKeyId);
    if (foreignKey === undefined) {
      errors.push(`Foreign key "${join.foreignKeyId}" is unknown.`);
      return;
    }

    const sourceTableId = join.direction === 'forward' ? foreignKey.childTableId : foreignKey.parentTableId;
    if (source.tableId !== sourceTableId) {
      errors.push(`Foreign key "${join.foreignKeyId}" does not match its direction.`);
      return;
    }

    const targetTableId = join.direction === 'forward' ? foreignKey.parentTableId : foreignKey.childTableId;
    addAlias(errors, aliases, join.alias, schema.tables.find((table) => table.tableId === targetTableId)!);
    return;
  }

  const target = schema.tables.find((table) => table.tableId === join.tableId);
  if (target === undefined) {
    errors.push(`Manual join table "${join.tableId}" is unknown.`);
    return;
  }
  if (join.pairs.length === 0) {
    errors.push('Manual joins require at least one column pair.');
  }
  for (const pair of join.pairs) {
    const fromKind = namedColumnKind(errors, source, join.fromAlias, pair.fromColumn);
    const toKind = namedColumnKind(errors, target, join.alias, pair.toColumn);
    if (fromKind !== null && toKind !== null && fromKind !== toKind) {
      errors.push('Manual join columns must have matching value kinds.');
    }
  }
  addAlias(errors, aliases, join.alias, target);
}

function validatePredicate(errors: string[], aliases: Map<string, SchemaTable>, predicate: Predicate, schema: SelectionSchema): void {
  switch (predicate.kind) {
    case 'and':
      validateGroup(errors, aliases, predicate.terms, 'AND', schema);
      return;
    case 'or':
      validateGroup(errors, aliases, predicate.terms, 'OR', schema);
      return;
    case 'not':
      validatePredicate(errors, aliases, predicate.term, schema);
      return;
    case 'comparison':
      validateValues(errors, aliases, predicate.column, [predicate.value]);
      return;
    case 'between':
      validateValues(errors, aliases, predicate.column, [predicate.lower, predicate.upper]);
      return;
    case 'set':
      if (predicate.values.length === 0) {
        errors.push('IN lists require at least one value.');
      }
      validateValues(errors, aliases, predicate.column, predicate.values);
      return;
    case 'null':
      columnKind(errors, aliases, predicate.column);
      return;
    case 'text': {
      const kind = columnKind(errors, aliases, predicate.column);
      if (kind !== 'string') {
        errors.push('Text predicates require a string column.');
      }
      validateValues(errors, aliases, predicate.column, [predicate.value]);
      return;
    }
    case 'boolean': {
      const kind = columnKind(errors, aliases, predicate.column);
      if (kind !== 'boolean') {
        errors.push('Boolean predicates require a boolean column.');
      }
      validateValues(errors, aliases, predicate.column, [predicate.value]);
      return;
    }
    case 'temporalRange': {
      const kind = columnKind(errors, aliases, predicate.column);
      if (kind !== predicate.temporalKind) {
        errors.push('Temporal range kind must match its column.');
      }
      validateValues(errors, aliases, predicate.column, [predicate.lower, predicate.upper]);
      return;
    }
    case 'exists':
      validateExists(errors, aliases, predicate, schema);
  }
}

function validateGroup(errors: string[], aliases: Map<string, SchemaTable>, terms: readonly Predicate[], label: 'AND' | 'OR', schema: SelectionSchema): void {
  if (terms.length < 2) {
    errors.push(`${label} groups require at least two terms.`);
  }
  for (const term of terms) {
    validatePredicate(errors, aliases, term, schema);
  }
}

function validateValues(errors: string[], aliases: Map<string, SchemaTable>, column: ColumnRef, values: readonly TypedValue[]): void {
  const kind = columnKind(errors, aliases, column);
  for (const value of values) {
    if (kind !== null && value.kind !== kind) {
      errors.push(`Value kind must match "${column.alias}.${column.name}".`);
    }
  }
}

function columnKind(errors: string[], aliases: Map<string, SchemaTable>, column: ColumnRef): ValueKind | null {
  const table = aliases.get(column.alias);
  if (table === undefined) {
    errors.push(`Column "${column.alias}.${column.name}" is unknown.`);
    return null;
  }
  const kind = namedColumnKind(errors, table, column.alias, column.name);
  if (kind !== null && kind !== column.valueKind) {
    errors.push(`Column "${column.alias}.${column.name}" has the wrong value kind.`);
  }

  return kind;
}

function namedColumnKind(errors: string[], table: SchemaTable, alias: string, name: string): ValueKind | null {
  const column = table.columns.find((item) => item.name === name);
  if (column === undefined) {
    errors.push(`Column "${alias}.${name}" is unknown.`);
    return null;
  }

  return column.valueKind;
}

function validateExists(errors: string[], aliases: Map<string, SchemaTable>, predicate: Extract<Predicate, { kind: 'exists' }>, schema: SelectionSchema): void {
  const table = schema.tables.find((item) => item.tableId === predicate.tableId);
  if (table === undefined) {
    errors.push(`EXISTS table "${predicate.tableId}" is unknown.`);
    return;
  }

  const scope = new Map(aliases);
  addAlias(errors, scope, predicate.alias, table);
  if (predicate.correlations.length === 0) {
    errors.push('EXISTS requires at least one correlation.');
  }
  for (const correlation of predicate.correlations) {
    const outerKind = columnKind(errors, aliases, correlation.outer);
    const innerKind = columnKind(errors, scope, { alias: predicate.alias, name: correlation.innerColumn, valueKind: outerKind ?? 'int' });
    if (outerKind !== null && innerKind !== null && outerKind !== innerKind) {
      errors.push('EXISTS correlation columns must have matching value kinds.');
    }
  }
  if (predicate.predicate !== null) {
    validatePredicate(errors, scope, predicate.predicate, schema);
  }
}
