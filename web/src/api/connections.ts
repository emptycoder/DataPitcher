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
    /** Schema holding the tables to transfer; blank means the provider default. */
    businessSchema?: string | null;
}>;

/** The schema most databases keep their tables in. */
export function defaultBusinessSchema(providerId: string) {
    return providerId === 'postgresql' ? 'public' : 'dbo';
}

export const providerLabels: Readonly<Record<string, string>> = { sqlserver: 'SQL Server', postgresql: 'PostgreSQL' };

export function credentialEnvironmentVariable(credentialId: string) {
    return `DATAPITCHER_CREDENTIAL_${credentialId.replace(/-/g, '').toUpperCase()}`;
}

export type UpdateConnectionInput = Readonly<{
    displayName: string;
    providerId: string;
    /** Null keeps the stored credentials untouched. */
    connectionString: string | null;
    /** When the new connection string has no password, the API appends the stored one. */
    keepStoredPassword?: boolean;
    /** Null keeps the stored schema. */
    businessSchema?: string | null;
}>;

/** The stored connection string with every password removed, so an operator can edit the other settings. */
export const ConnectionDetailsSchema = z.object({
    connectionId: z.string(),
    providerId: z.string(),
    connectionString: z.string(),
    hasPassword: z.boolean(),
    businessSchema: z.string(),
});
export type ConnectionDetails = z.infer<typeof ConnectionDetailsSchema>;

export const ConnectionTestSchema = z.object({
    succeeded: z.boolean(),
    health: z.string(),
    databaseIdentity: z.string().nullable(),
    providerVersion: z.string().nullable(),
    capabilities: z.array(z.string()),
    missingRequired: z.array(z.string()),
    error: z.string().nullable(),
});
export type ConnectionTest = z.infer<typeof ConnectionTestSchema>;
export type ConnectionTestInput = Readonly<{
    providerId: string;
    connectionString?: string | null;
    connectionId?: string | null;
    keepStoredPassword?: boolean;
    businessSchema?: string | null;
}>;

export const connectionsApi = {
    test: (input: ConnectionTestInput, auth: AuthenticationAdapter) =>
        requestJson<unknown>('/api/connections/test', auth, { method: 'POST', body: input }).then((data) =>
            ConnectionTestSchema.parse(data),
        ),
    update: (connectionId: string, input: UpdateConnectionInput, eTag: string, auth: AuthenticationAdapter) =>
        requestJson<unknown>(`/api/connections/${connectionId}`, auth, {
            method: 'PUT',
            body: { ...input, ifMatch: eTag },
        }).then((data) => ConnectionSchema.parse(data)),
    providers: async (signal?: AbortSignal) => {
        const response = await fetch('/api/providers', { signal });
        if (!response.ok) throw new Error('Unable to load providers.');
        return z.array(ProviderSchema).parse(await response.json());
    },
    list: (auth: AuthenticationAdapter, signal?: AbortSignal) =>
        requestJson<unknown>('/api/connections', auth, { signal }).then((data) =>
            z.array(ConnectionSchema).parse(data),
        ),
    /** The credential id doubles as the idempotency key: a retry of the same submission returns the same profile. */
    create: (input: CreateConnectionInput, auth: AuthenticationAdapter) =>
        requestJson<unknown>('/api/connections', auth, {
            method: 'POST',
            body: { ...input, ifMatch: input.credentialId },
        }).then((data) => ConnectionSchema.parse(data)),
    details: (connectionId: string, auth: AuthenticationAdapter, signal?: AbortSignal) =>
        requestJson<unknown>(`/api/connections/${connectionId}/details`, auth, { signal }).then((data) =>
            ConnectionDetailsSchema.parse(data),
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
