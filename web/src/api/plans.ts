import { z } from 'zod';
import type { AuthenticationAdapter } from '../auth/authAdapter';
import { ConnectionSchema, OperationReceiptSchema } from './connections';
import { requestJson } from './http';

const MessageSchema = z.object({ code: z.string(), message: z.string() });
const AddressSchema = z.object({ schema: z.string(), name: z.string() });

export const PlanReviewSchema = z.object({
  planId: z.string(),
  version: z.number(),
  canonicalHash: z.string().nullable(),
  seal: z.object({ status: z.string(), invalidationReasons: z.array(MessageSchema) }),
  totals: z.object({ included: z.number(), plannedWrites: z.number(), inserts: z.number(), updates: z.number(), estimatedBytes: z.number() }),
  startPreconditions: z.array(z.object({ code: z.string(), satisfied: z.boolean(), message: z.string() })),
  tables: z.array(
    z.object({
      source: AddressSchema,
      target: AddressSchema,
      state: z.string(),
      transferOrder: z.number(),
      included: z.number(),
      plannedWrites: z.number(),
      inserts: z.number(),
      updates: z.number(),
      estimatedBytes: z.number(),
      columns: z.array(z.object({ source: z.string(), target: z.string() })),
    }),
  ),
  conflicts: z.array(z.object({ table: z.string(), policy: z.string(), message: z.string() })),
  cycles: z.array(z.object({ tables: z.array(z.string()), strategy: z.string(), message: z.string() })),
  warnings: z.array(MessageSchema),
  blockers: z.array(MessageSchema),
  selection: z
    .object({ selectionId: z.string(), displayName: z.string(), connectionId: z.string().nullable(), snapshotId: z.string().nullable() })
    .nullable()
    .optional(),
  source: ConnectionSchema.nullable().optional(),
  target: ConnectionSchema.nullable().optional(),
});
export type PlanReview = z.infer<typeof PlanReviewSchema>;
export type PlanTable = PlanReview['tables'][number];

const ProblemSchema = z.object({ code: z.string(), message: z.string(), isBlocker: z.boolean() });

/** The column mapping as sealing will apply it: prefilled by name, the operator's overrides on top, every problem the target would raise. */
export const PlanMappingSchema = z.object({
  planId: z.string(),
  version: z.number(),
  eTag: z.string(),
  targetSnapshotId: z.string().nullable(),
  problems: z.array(ProblemSchema),
  tables: z.array(
    z.object({
      source: AddressSchema,
      target: AddressSchema,
      targetExists: z.boolean(),
      isRoot: z.boolean(),
      targetColumns: z.array(z.string()),
      columns: z.array(
        z.object({
          source: z.string(),
          sourceType: z.string(),
          sourceNullable: z.boolean(),
          target: z.string().nullable(),
          targetType: z.string().nullable(),
          targetNullable: z.boolean().nullable(),
          isKey: z.boolean(),
          isForeignKey: z.boolean(),
          origin: z.string(),
          problems: z.array(ProblemSchema),
        }),
      ),
      targetOnlyColumns: z.array(z.object({ name: z.string(), type: z.string(), isNullable: z.boolean(), problems: z.array(ProblemSchema) })),
      problems: z.array(ProblemSchema),
    }),
  ),
});
export type PlanMapping = z.infer<typeof PlanMappingSchema>;
export type PlanMappingTable = PlanMapping['tables'][number];
export type PlanMappingColumn = PlanMappingTable['columns'][number];
export type MappingProblem = z.infer<typeof ProblemSchema>;

/** One table's overrides as the API stores them; a null column target means "do not transfer". */
export type PlanTableMappingInput = Readonly<{
  source: Readonly<{ schema: string; name: string }>;
  target: Readonly<{ schema: string; name: string }> | null;
  columns: readonly Readonly<{ source: string; target: string | null }>[];
}>;

export const PlanResponseSchema = z.object({ planId: z.string(), version: z.number(), canonicalHash: z.string().nullable(), eTag: z.string() });
export type PlanResponse = z.infer<typeof PlanResponseSchema>;

export const InclusionPathSchema = z.object({
  table: z.string(),
  stableKey: z.string(),
  rootSelection: z.string(),
  steps: z.array(z.object({ relationship: z.string(), from: z.string(), to: z.string(), reason: z.string() })),
});
export type InclusionPath = z.infer<typeof InclusionPathSchema>;

export const PlanGraphSchema = z.object({
  revision: z.string(),
  plannedTableIds: z.array(z.string()),
  tables: z.array(z.object({ id: z.string(), schema: z.string(), name: z.string(), componentId: z.string(), state: z.string() })),
  relationships: z.array(z.object({ id: z.string(), name: z.string(), childTableId: z.string(), parentTableId: z.string() })),
});
export type PlanGraph = z.infer<typeof PlanGraphSchema>;

/** Partial save: null members keep the stored values; an empty operator note clears it. */
export type SavePlanInput = Readonly<{
  displayName: string | null;
  operatorNote: string | null;
  ifMatch: string;
  selectionId: string | null;
  sourceConnectionId: string | null;
  targetConnectionId: string | null;
  /** Omit to keep the stored mapping; an empty list returns every table to its defaults. */
  mappings?: readonly PlanTableMappingInput[];
}>;

/** The editable plan record, read back so forms prefill from the API rather than from this browser's registry. */
export const PlanDetailsSchema = z.object({
  planId: z.string(),
  displayName: z.string(),
  operatorNote: z.string().nullable(),
  version: z.number(),
  eTag: z.string(),
  canonicalHash: z.string().nullable(),
  sealed: z.boolean(),
  selectionId: z.string().nullable(),
  sourceConnectionId: z.string().nullable(),
  targetConnectionId: z.string().nullable(),
  updatedUtc: z.string(),
});
export type PlanDetails = z.infer<typeof PlanDetailsSchema>;

export const plansApi = {
  get: (planId: string, auth: AuthenticationAdapter, signal?: AbortSignal) =>
    requestJson<unknown>(`/api/plans/${planId}`, auth, { signal }).then((data) => PlanDetailsSchema.parse(data)),
  save: (planId: string, input: SavePlanInput, auth: AuthenticationAdapter) =>
    requestJson<unknown>(`/api/plans/${planId}`, auth, { method: 'PUT', body: input }).then((data) => PlanResponseSchema.parse(data)),
  seal: (planId: string, auth: AuthenticationAdapter) =>
    requestJson<unknown>(`/api/plans/${planId}/seal`, auth, { method: 'POST' }).then((data) => OperationReceiptSchema.parse(data)),
  review: (planId: string, auth: AuthenticationAdapter, signal?: AbortSignal) =>
    requestJson<unknown>(`/api/plans/${planId}/review`, auth, { signal }).then((data) => PlanReviewSchema.parse(data)),
  mapping: (planId: string, auth: AuthenticationAdapter, signal?: AbortSignal) =>
    requestJson<unknown>(`/api/plans/${planId}/mapping`, auth, { signal }).then((data) => PlanMappingSchema.parse(data)),
  inclusionPath: (planId: string, table: string, stableKey: string, auth: AuthenticationAdapter) =>
    requestJson<unknown>(`/api/plans/${planId}/inclusion-paths`, auth, { method: 'POST', body: { table, stableKey } }).then((data) => InclusionPathSchema.parse(data)),
  graph: (planId: string, auth: AuthenticationAdapter, signal?: AbortSignal) =>
    requestJson<unknown>(`/api/plans/${planId}/schema-dependency-graph`, auth, { signal }).then((data) => PlanGraphSchema.parse(data)),
  startJob: (planId: string, idempotencyKey: string, auth: AuthenticationAdapter) =>
    requestJson<unknown>(`/api/plans/${planId}/jobs`, auth, { method: 'POST', headers: { 'Idempotency-Key': idempotencyKey } }).then((data) =>
      OperationReceiptSchema.parse(data),
    ),
};

export function isSealed(review: PlanReview | null | undefined): boolean {
  return review?.seal.status.toLowerCase() === 'sealed';
}
