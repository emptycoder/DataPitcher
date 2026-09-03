import { z } from 'zod';

export const TransferJobState = z.enum([
  'draft', 'queued', 'preparing', 'running', 'pausing', 'paused', 'cancelling', 'cancelled', 'verifying', 'succeeded', 'failed', 'verificationfailed',
]);
export type TransferJobState = z.infer<typeof TransferJobState>;

export const TableProgress = z.object({
  table: z.string(),
  rowsTransferred: z.number().int().nonnegative(),
  bytesTransferred: z.number().int().nonnegative(),
  totalRows: z.number().int().nonnegative().optional(),
  totalBytes: z.number().int().nonnegative().optional(),
});
export type TableProgress = z.infer<typeof TableProgress>;

export const TransferEventPayload = z.object({
  State: TransferJobState,
  RowsTransferred: z.number().int().nonnegative(),
  BytesTransferred: z.number().int().nonnegative(),
  TableProgress: z.array(TableProgress).optional(),
});
export type TransferEventPayload = z.infer<typeof TransferEventPayload>;

export const TransferJobSnapshot = z.object({
  jobId: z.uuid(),
  planId: z.uuid(),
  state: TransferJobState,
  rowsTransferred: z.number().int().nonnegative(),
  bytesTransferred: z.number().int().nonnegative(),
  tableProgress: z.array(TableProgress).default([]),
});
export type TransferJobSnapshot = z.infer<typeof TransferJobSnapshot>;
