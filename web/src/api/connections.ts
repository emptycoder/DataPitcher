import { z } from 'zod';
import type { AuthenticationAdapter } from '../auth/authAdapter';
import { requestJson } from './http';

export const ConnectionSchema = z.object({
    connectionId: z.string(),
    displayName: z.string(),
    providerId: z.string(),
    health: z.string(),
    eTag: z.string(),
});
export type Connection = z.infer<typeof ConnectionSchema>;

export const ProviderSchema = z.object({ providerId: z.string(), displayName: z.string() });
export type Provider = z.infer<typeof ProviderSchema>;

export const OperationReceiptSchema = z.object({
    operationId: z.string(),
    state: z.string(),
    statusUri: z.string(),
    connectionId: z.string().nullable().optional(),
    planId: z.string().nullable().optional(),
    jobId: z.string().nullable().optional(),
});
export type OperationReceipt = z.infer<typeof OperationReceiptSchema>;

export const SnapshotSummarySchema = z.object({ snapshotId: z.string(), hash: z.string(), capturedAtUtc: z.string() });
export type SnapshotSummary = z.infer<typeof SnapshotSummarySchema>;

const AddressSchema = z.object({ schema: z.string(), name: z.string() });
export type TableAddress = z.infer<typeof AddressSchema>;
export const SnapshotSchema = z.object({
    connectionId: z.string(),
    snapshotId: z.string(),
    hash: z.string(),
    capturedAtUtc: z.string(),
    tables: z.array(
        z.object({
            schema: z.string(),
            name: z.string(),
            columns: z.array(z.object({ name: z.string(), storeType: z.string(), isNullable: z.boolean() })),
            primaryKey: z.object({ name: z.string(), columns: z.array(z.string()) }).nullable(),
        }),
    ),
    foreignKeys: z.array(
        z.object({
            name: z.string(),
            childTable: AddressSchema,
            parentTable: AddressSchema,
            childColumns: z.array(z.string()),
            parentColumns: z.array(z.string()),
            isEnforced: z.boolean(),
            isTrusted: z.boolean(),
        }),
    ),
});
export type Snapshot = z.infer<typeof SnapshotSchema>;
export type SnapshotTable = Snapshot['tables'][number];
export type SnapshotForeignKey = Snapshot['foreignKeys'][number];

export type CreateConnectionInput = Readonly<{
    displayName: string;
    providerId: string;
    credentialId: string;
    connectionString?: string | null;
}>;

export const providerLabels: Readonly<Record<string, string>> = { sqlserver: 'SQL Server', postgresql: 'PostgreSQL' };

export function credentialEnvironmentVariable(credentialId: string) {
    return `DATAPITCHER_CREDENTIAL_${credentialId.replace(/-/g, '').toUpperCase()}`;
}

export const connectionsApi = {
    providers: async (signal?: AbortSignal) => {
        const response = await fetch('/api/providers', { signal });
        if (!response.ok) throw new Error('Unable to load providers.');
        return z.array(ProviderSchema).parse(await response.json());
    },
    list: (auth: AuthenticationAdapter, signal?: AbortSignal) =>
        requestJson<unknown>('/api/connections', auth, { signal }).then((data) =>
            z.array(ConnectionSchema).parse(data),
        ),
    create: (input: CreateConnectionInput, auth: AuthenticationAdapter) =>
        requestJson<unknown>('/api/connections', auth, { method: 'POST', body: { ...input, ifMatch: '*' } }).then(
            (data) => ConnectionSchema.parse(data),
        ),
    remove: (connectionId: string, eTag: string, auth: AuthenticationAdapter) =>
        requestJson<void>(`/api/connections/${connectionId}`, auth, {
            method: 'DELETE',
            headers: { 'If-Match': eTag },
        }),
    check: (connectionId: string, auth: AuthenticationAdapter) =>
        requestJson<unknown>(`/api/connections/${connectionId}/checks`, auth, { method: 'POST' }).then((data) =>
            OperationReceiptSchema.parse(data),
        ),
    scan: (connectionId: string, auth: AuthenticationAdapter) =>
        requestJson<unknown>(`/api/connections/${connectionId}/schema-scans`, auth, { method: 'POST' }).then((data) =>
            OperationReceiptSchema.parse(data),
        ),
    snapshots: (connectionId: string, auth: AuthenticationAdapter, signal?: AbortSignal) =>
        requestJson<unknown>(`/api/connections/${connectionId}/snapshots`, auth, { signal }).then((data) =>
            z
                .array(SnapshotSummarySchema)
                .parse(data)
                .toSorted((left, right) => right.capturedAtUtc.localeCompare(left.capturedAtUtc)),
        ),
    snapshot: (connectionId: string, snapshotId: string, auth: AuthenticationAdapter, signal?: AbortSignal) =>
        requestJson<unknown>(`/api/connections/${connectionId}/snapshots/${snapshotId}`, auth, { signal }).then(
            (data) => SnapshotSchema.parse(data),
        ),
};
