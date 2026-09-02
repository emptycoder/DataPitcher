import { expect, it } from 'vitest';
import {
  addJoin,
  operatorsFor,
  replacePredicate,
  selectionFingerprint,
  validateVisualSelection,
  type SelectionSchema,
  type VisualSelection,
} from './selectionAst';

const selection: VisualSelection = { root: { tableId: 'sales.orders', alias: 'o', stableKey: ['id'] }, joins: [], predicate: null };

it('edits a typed nested AST without SQL text', () => {
  const nested = { kind: 'not' as const, term: { kind: 'or' as const, terms: [
    { kind: 'between' as const, column: { alias: 'o', name: 'created', valueKind: 'date' as const }, lower: { kind: 'date' as const, value: '2026-09-01' }, upper: { kind: 'date' as const, value: '2026-09-02' } },
    { kind: 'exists' as const, tableId: 'sales.lines', alias: 'l', correlations: [{ outer: { alias: 'o', name: 'id', valueKind: 'int' as const }, innerColumn: 'order_id' }], predicate: { kind: 'text' as const, match: 'contains' as const, column: { alias: 'l', name: 'sku', valueKind: 'string' as const }, value: { kind: 'string' as const, value: 'A' } }, negated: false },
  ] } };

  expect(replacePredicate(selection, nested).predicate).toEqual(nested);
  expect(addJoin(selection, { kind: 'foreignKey', fromAlias: 'o', alias: 'c', foreignKeyId: 'fk_orders_customer', direction: 'forward' }).joins).toHaveLength(1);
  expect(operatorsFor('string')).toEqual(['equal', 'notEqual', 'in', 'isNull', 'isNotNull', 'contains', 'startsWith', 'endsWith']);
  expect(operatorsFor('date')).toEqual(['equal', 'notEqual', 'between', 'isNull', 'isNotNull', 'dateRange']);
});

const schema: SelectionSchema = {
  tables: [
    { tableId: 'sales.orders', stableKey: ['id', 'tenant_id'], columns: [
      { name: 'id', valueKind: 'int' }, { name: 'tenant_id', valueKind: 'guid' }, { name: 'customer_id', valueKind: 'int' }, { name: 'status_id', valueKind: 'int' },
      { name: 'total', valueKind: 'decimal' }, { name: 'title', valueKind: 'string' }, { name: 'paid', valueKind: 'boolean' }, { name: 'created', valueKind: 'date' },
      { name: 'at', valueKind: 'time' }, { name: 'changed', valueKind: 'dateTime' }, { name: 'external_id', valueKind: 'guid' },
    ] },
    { tableId: 'sales.customers', stableKey: ['id'], columns: [{ name: 'id', valueKind: 'int' }, { name: 'name', valueKind: 'string' }] },
    { tableId: 'sales.lines', stableKey: ['id'], columns: [{ name: 'id', valueKind: 'int' }, { name: 'order_id', valueKind: 'int' }, { name: 'sku', valueKind: 'string' }] },
    { tableId: 'sales.statuses', stableKey: ['id'], columns: [{ name: 'id', valueKind: 'int' }] },
    { tableId: 'sales.blocked', stableKey: null, columns: [{ name: 'id', valueKind: 'int' }] },
  ],
  foreignKeys: [
    { foreignKeyId: 'fk_orders_customer', childTableId: 'sales.orders', parentTableId: 'sales.customers' },
    { foreignKeyId: 'fk_lines_orders', childTableId: 'sales.lines', parentTableId: 'sales.orders' },
  ],
};

const fullSelection: VisualSelection = {
  root: { tableId: 'sales.orders', alias: 'o', stableKey: ['id', 'tenant_id'] },
  joins: [
    { kind: 'foreignKey', fromAlias: 'o', alias: 'c', foreignKeyId: 'fk_orders_customer', direction: 'forward' },
    { kind: 'foreignKey', fromAlias: 'c', alias: 'co', foreignKeyId: 'fk_orders_customer', direction: 'reverse' },
    { kind: 'manual', fromAlias: 'o', tableId: 'sales.statuses', alias: 's', pairs: [{ fromColumn: 'status_id', toColumn: 'id' }] },
  ],
  predicate: {
    kind: 'and', terms: [
      { kind: 'comparison', column: { alias: 'o', name: 'id', valueKind: 'int' }, operator: 'greaterThan', value: { kind: 'int', value: 1 } },
      { kind: 'between', column: { alias: 'o', name: 'total', valueKind: 'decimal' }, lower: { kind: 'decimal', value: 1 }, upper: { kind: 'decimal', value: 2 } },
      { kind: 'set', column: { alias: 'o', name: 'external_id', valueKind: 'guid' }, negated: true, values: [{ kind: 'guid', value: 'a' }] },
      { kind: 'null', column: { alias: 'o', name: 'external_id', valueKind: 'guid' }, negated: false },
      { kind: 'text', column: { alias: 'o', name: 'title', valueKind: 'string' }, match: 'startsWith', value: { kind: 'string', value: 'A' } },
      { kind: 'boolean', column: { alias: 'o', name: 'paid', valueKind: 'boolean' }, value: { kind: 'boolean', value: true } },
      { kind: 'temporalRange', column: { alias: 'o', name: 'created', valueKind: 'date' }, temporalKind: 'date', lower: { kind: 'date', value: '2026-09-01' }, upper: { kind: 'date', value: '2026-09-02' } },
      { kind: 'temporalRange', column: { alias: 'o', name: 'at', valueKind: 'time' }, temporalKind: 'time', lower: { kind: 'time', value: '10:00' }, upper: { kind: 'time', value: '11:00' } },
      { kind: 'not', term: { kind: 'or', terms: [
        { kind: 'temporalRange', column: { alias: 'o', name: 'changed', valueKind: 'dateTime' }, temporalKind: 'dateTime', lower: { kind: 'dateTime', value: '2026-09-01T10:00:00' }, upper: { kind: 'dateTime', value: '2026-09-02T10:00:00' } },
        { kind: 'exists', tableId: 'sales.lines', alias: 'l', correlations: [{ outer: { alias: 'o', name: 'id', valueKind: 'int' }, innerColumn: 'order_id' }], predicate: { kind: 'text', column: { alias: 'l', name: 'sku', valueKind: 'string' }, match: 'endsWith', value: { kind: 'string', value: 'Z' } }, negated: true },
      ] } },
    ],
  },
};

it('offers the complete operator policy for every value kind', () => {
  expect(operatorsFor('int')).toEqual(['equal', 'notEqual', 'greaterThan', 'greaterOrEqual', 'lessThan', 'lessOrEqual', 'between', 'in', 'isNull', 'isNotNull']);
  expect(operatorsFor('decimal')).toEqual(['equal', 'notEqual', 'greaterThan', 'greaterOrEqual', 'lessThan', 'lessOrEqual', 'between', 'in', 'isNull', 'isNotNull']);
  expect(operatorsFor('boolean')).toEqual(['equal', 'notEqual', 'isNull', 'isNotNull']);
  expect(operatorsFor('time')).toEqual(['equal', 'notEqual', 'between', 'isNull', 'isNotNull', 'timeRange']);
  expect(operatorsFor('dateTime')).toEqual(['equal', 'notEqual', 'between', 'isNull', 'isNotNull', 'dateTimeRange']);
  expect(operatorsFor('guid')).toEqual(['equal', 'notEqual', 'in', 'isNull', 'isNotNull']);
});

it('validates every typed predicate, relationship, and stable key without mutating the AST', () => {
  const errors = validateVisualSelection(fullSelection, schema);

  expect(errors).toEqual([]);
  expect(Object.isFrozen(errors)).toBe(true);
  expect(selectionFingerprint(fullSelection)).toBe(JSON.stringify(fullSelection));
  expect(selectionFingerprint(replacePredicate(fullSelection, null))).not.toBe(selectionFingerprint(fullSelection));
  expect(addJoin(fullSelection, { kind: 'manual', fromAlias: 'o', tableId: 'sales.statuses', alias: 's2', pairs: [{ fromColumn: 'status_id', toColumn: 'id' }] }).joins).toHaveLength(4);
  expect(validateVisualSelection({ ...fullSelection, joins: [{ kind: 'manual', fromAlias: 'o', tableId: 'sales.customers', alias: 'named', pairs: [{ fromColumn: 'title', toColumn: 'name' }] }] }, schema)).toEqual([]);
});

it('accepts a selection without a predicate', () => {
  expect(validateVisualSelection(replacePredicate(fullSelection, null), schema)).toEqual([]);
});

it('reports invalid roots, aliases, joins, and predicates for inline rendering', () => {
  expect(validateVisualSelection({ ...fullSelection, root: { ...fullSelection.root, tableId: 'missing' } }, schema)).toContain('Root table is unknown.');
  expect(validateVisualSelection({ ...fullSelection, root: { ...fullSelection.root, stableKey: ['tenant_id', 'id'] } }, schema)).toContain('Root stable key must match the schema order.');
  expect(validateVisualSelection({ ...fullSelection, root: { ...fullSelection.root, stableKey: ['id'] } }, schema)).toContain('Root stable key must match the schema order.');
  expect(validateVisualSelection({ ...fullSelection, root: { tableId: 'sales.blocked', alias: 'o', stableKey: ['id'] } }, schema)).toContain('Root stable key must match the schema order.');
  expect(validateVisualSelection({ ...fullSelection, root: { ...fullSelection.root, alias: '1bad' } }, schema)).toContain('Alias "1bad" is invalid.');
  expect(validateVisualSelection({ ...fullSelection, joins: [{ kind: 'foreignKey', fromAlias: 'missing', alias: 'c', foreignKeyId: 'fk_orders_customer', direction: 'forward' }] }, schema)).toContain('Join source alias "missing" is unknown.');
  expect(validateVisualSelection({ ...fullSelection, joins: [{ kind: 'foreignKey', fromAlias: 'o', alias: 'c', foreignKeyId: 'missing', direction: 'forward' }] }, schema)).toContain('Foreign key "missing" is unknown.');
  expect(validateVisualSelection({ ...fullSelection, joins: [{ kind: 'foreignKey', fromAlias: 'o', alias: 'c', foreignKeyId: 'fk_orders_customer', direction: 'reverse' }] }, schema)).toContain('Foreign key "fk_orders_customer" does not match its direction.');
  expect(validateVisualSelection({ ...fullSelection, joins: [{ kind: 'foreignKey', fromAlias: 'o', alias: 'o', foreignKeyId: 'fk_orders_customer', direction: 'forward' }] }, schema)).toContain('Alias "o" is already in use.');
  expect(validateVisualSelection({ ...fullSelection, joins: [{ kind: 'manual', fromAlias: 'o', tableId: 'missing', alias: 'm', pairs: [] }] }, schema)).toContain('Manual join table "missing" is unknown.');
  expect(validateVisualSelection({ ...fullSelection, joins: [{ kind: 'manual', fromAlias: 'o', tableId: 'sales.statuses', alias: 'm', pairs: [] }] }, schema)).toContain('Manual joins require at least one column pair.');
  expect(validateVisualSelection({ ...fullSelection, joins: [{ kind: 'manual', fromAlias: 'o', tableId: 'sales.statuses', alias: 'm', pairs: [{ fromColumn: 'status_id', toColumn: 'missing' }] }] }, schema)).toContain('Column "m.missing" is unknown.');
  expect(validateVisualSelection({ ...fullSelection, joins: [{ kind: 'manual', fromAlias: 'o', tableId: 'sales.statuses', alias: 'm', pairs: [{ fromColumn: 'title', toColumn: 'id' }] }] }, schema)).toContain('Manual join columns must have matching value kinds.');
  expect(validateVisualSelection({ ...fullSelection, predicate: { kind: 'and', terms: [] } }, schema)).toContain('AND groups require at least two terms.');
  expect(validateVisualSelection({ ...fullSelection, predicate: { kind: 'or', terms: [] } }, schema)).toContain('OR groups require at least two terms.');
  expect(validateVisualSelection({ ...fullSelection, predicate: { kind: 'comparison', column: { alias: 'missing', name: 'id', valueKind: 'int' }, operator: 'equal', value: { kind: 'int', value: 1 } } }, schema)).toContain('Column "missing.id" is unknown.');
  expect(validateVisualSelection({ ...fullSelection, predicate: { kind: 'comparison', column: { alias: 'o', name: 'id', valueKind: 'string' }, operator: 'equal', value: { kind: 'string', value: '1' } } }, schema)).toContain('Column "o.id" has the wrong value kind.');
  expect(validateVisualSelection({ ...fullSelection, predicate: { kind: 'between', column: { alias: 'o', name: 'id', valueKind: 'int' }, lower: { kind: 'int', value: 1 }, upper: { kind: 'decimal', value: 2 } } }, schema)).toContain('Value kind must match "o.id".');
  expect(validateVisualSelection({ ...fullSelection, predicate: { kind: 'set', column: { alias: 'o', name: 'id', valueKind: 'int' }, negated: false, values: [] } }, schema)).toContain('IN lists require at least one value.');
  expect(validateVisualSelection({ ...fullSelection, predicate: { kind: 'text', column: { alias: 'o', name: 'id', valueKind: 'int' }, match: 'contains', value: { kind: 'int', value: 1 } } }, schema)).toContain('Text predicates require a string column.');
  expect(validateVisualSelection({ ...fullSelection, predicate: { kind: 'boolean', column: { alias: 'o', name: 'id', valueKind: 'int' }, value: { kind: 'int', value: 1 } } }, schema)).toContain('Boolean predicates require a boolean column.');
  expect(validateVisualSelection({ ...fullSelection, predicate: { kind: 'temporalRange', column: { alias: 'o', name: 'created', valueKind: 'date' }, temporalKind: 'time', lower: { kind: 'date', value: '2026-09-01' }, upper: { kind: 'date', value: '2026-09-02' } } }, schema)).toContain('Temporal range kind must match its column.');
  expect(validateVisualSelection({ ...fullSelection, predicate: { kind: 'exists', tableId: 'missing', alias: 'e', correlations: [], predicate: null, negated: false } }, schema)).toContain('EXISTS table "missing" is unknown.');
  expect(validateVisualSelection({ ...fullSelection, predicate: { kind: 'exists', tableId: 'sales.lines', alias: 'e', correlations: [], predicate: null, negated: false } }, schema)).toContain('EXISTS requires at least one correlation.');
  expect(validateVisualSelection({ ...fullSelection, predicate: { kind: 'exists', tableId: 'sales.lines', alias: 'e', correlations: [{ outer: { alias: 'o', name: 'title', valueKind: 'string' }, innerColumn: 'order_id' }], predicate: null, negated: false } }, schema)).toContain('EXISTS correlation columns must have matching value kinds.');
});
