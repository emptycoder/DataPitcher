export const planId = '11111111-1111-4111-8111-111111111111';
export const reviewWire = {
  planId, version: 4, canonicalHash: 'A'.repeat(64),
  seal: { status: 'sealed', invalidationReasons: [] },
  totals: { included: 12, plannedWrites: 9, inserts: 7, updates: 2, estimatedBytes: 4096 },
  startPreconditions: [
    { code: 'permission', satisfied: true, message: 'Transfer permission is current.' },
    { code: 'sourceHealthy', satisfied: true, message: 'Source is server-verified Healthy.' },
    { code: 'targetHealthy', satisfied: true, message: 'Target is server-verified Healthy.' },
    { code: 'schemaValid', satisfied: true, message: 'Target schema validation passed.' },
    { code: 'noBlockers', satisfied: true, message: 'No blockers remain.' },
    { code: 'safeMappings', satisfied: true, message: 'All type mappings are safe.' },
    { code: 'cycleSupported', satisfied: true, message: 'Cycle strategy is supported.' },
    { code: 'authenticated', satisfied: true, message: 'Authentication is valid.' },
  ],
  tables: [{ source: { schema: 'sales', name: 'Orders' }, target: { schema: 'sales', name: 'Orders' }, state: 'Root', transferOrder: 2, included: 9, plannedWrites: 9, inserts: 7, updates: 2, estimatedBytes: 3072, columns: [{ source: 'Id', target: 'Id' }] }],
  conflicts: [{ table: 'sales.Orders', policy: 'FailOnConflict', message: 'Existing target keys fail the plan.' }],
  cycles: [{ tables: ['sales.Orders', 'sales.OrderLines'], strategy: 'DeferredConstraints', message: 'Constraints are deferred for this component.' }],
  warnings: [{ code: 'target-satisfied-values', message: 'Target-satisfied dependencies are not refreshed.' }],
  blockers: [],
};
export const inclusionPathWire = { table: 'sales.Orders', stableKey: 'Id=42', rootSelection: 'Open orders', steps: [{ relationship: 'Root selection', from: 'sales.Orders', to: 'sales.Orders', reason: 'Selected as a root row.' }] };
export const jobWire = { jobId: '22222222-2222-4222-8222-222222222222', planId, state: 'Running', rowsTransferred: 3, bytesTransferred: 1024 };
