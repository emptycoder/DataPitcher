import { describe, expect, it } from 'vitest';
import type { PlanMapping } from '../../api/plans';
import { problemCounts, storedOverrides, toInput } from './MappingPanel';

const mapping: PlanMapping = {
  planId: 'p',
  version: 1,
  eTag: '"1"',
  targetSnapshotId: null,
  problems: [],
  tables: [
    {
      source: { schema: 'dbo', name: 'Orders' },
      target: { schema: 'dbo', name: 'Orders' },
      targetExists: true,
      isRoot: true,
      targetColumns: ['Id', 'Remark'],
      columns: [
        { source: 'Id', sourceType: 'int', sourceNullable: false, target: 'Id', targetType: 'int', targetNullable: false, isKey: true, isForeignKey: false, origin: 'default', problems: [] },
        { source: 'Note', sourceType: 'text', sourceNullable: true, target: 'Remark', targetType: 'text', targetNullable: true, isKey: false, isForeignKey: false, origin: 'override', problems: [] },
        { source: 'Legacy', sourceType: 'text', sourceNullable: true, target: null, targetType: null, targetNullable: null, isKey: false, isForeignKey: false, origin: 'excluded', problems: [] },
        {
          source: 'Extra',
          sourceType: 'text',
          sourceNullable: true,
          target: null,
          targetType: null,
          targetNullable: null,
          isKey: false,
          isForeignKey: false,
          origin: 'unmapped',
          problems: [{ code: 'column_unmapped', message: 'no target', isBlocker: false }],
        },
      ],
      targetOnlyColumns: [{ name: 'Region', type: 'text', isNullable: false, problems: [{ code: 'target_required_unfilled', message: 'required', isBlocker: false }] }],
      problems: [{ code: 'target_table_missing', message: 'missing', isBlocker: true }],
    },
    { source: { schema: 'dbo', name: 'Customers' }, target: { schema: 'dbo', name: 'Customers' }, targetExists: true, isRoot: false, targetColumns: ['Id'], columns: [], targetOnlyColumns: [], problems: [] },
  ],
};

describe('MappingPanel helpers', () => {
  it('reads only the operator choices back from the mapping', () => {
    expect(storedOverrides(mapping)).toEqual({ 'dbo.Orders': { Note: 'Remark', Legacy: null } });
  });

  it('sends only tables that carry a choice', () => {
    expect(toInput(mapping, storedOverrides(mapping))).toEqual([
      { source: { schema: 'dbo', name: 'Orders' }, target: null, columns: [{ source: 'Note', target: 'Remark' }, { source: 'Legacy', target: null }] },
    ]);
    expect(toInput(mapping, { 'dbo.Orders': {} })).toEqual([]);
  });

  it('counts blockers and warnings apart', () => {
    const table = mapping.tables[0]!;
    expect(problemCounts([...table.problems, ...table.columns.flatMap((column) => column.problems), ...table.targetOnlyColumns.flatMap((column) => column.problems)])).toEqual({ blockers: 1, warnings: 2 });
  });
});
