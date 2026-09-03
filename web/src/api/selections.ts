import { z } from 'zod';
import type { AuthenticationAdapter } from '../auth/authAdapter';
import { requestJson } from './http';

export const valueKinds = ['int', 'decimal', 'string', 'boolean', 'date', 'time', 'dateTime', 'guid'] as const;
export type ValueKind = (typeof valueKinds)[number];

export type ParameterValue = Readonly<{ name: string; kind: ValueKind; value: string | number | boolean }>;

export type SelectionRequestBody = Readonly<{
    mode: 'raw' | 'visual';
    visual: Record<string, unknown> | null;
    rawSql: string | null;
    parameters: readonly ParameterValue[];
    schemaRevision: string;
    connectionId?: string | null;
    snapshotId?: string | null;
    rootSchema?: string | null;
    rootTable?: string | null;
    stableKeyConstraintName?: string | null;
    stableKeyColumns?: readonly string[] | null;
}>;

export const SavedSelectionSchema = z.object({
    selectionId: z.string(),
    displayName: z.string(),
    version: z.number(),
    eTag: z.string(),
    mode: z.string(),
    warnings: z.array(z.string()),
});
export type SavedSelection = z.infer<typeof SavedSelectionSchema>;

export const CompilationSchema = z.object({
    sqlSnapshot: z.string(),
    parameters: z.array(z.object({ name: z.string(), kind: z.string() })),
    warnings: z.array(z.string()),
    schemaRevision: z.string(),
});
export type Compilation = z.infer<typeof CompilationSchema>;

export const PreviewSchema = z.object({
    columns: z.array(z.string()),
    rows: z.array(z.record(z.string(), z.unknown())),
    hasMore: z.boolean(),
    revision: z.string(),
});
export type Preview = z.infer<typeof PreviewSchema>;

export const WorkbenchSchemaSchema = z.object({
    tables: z.array(
        z.object({
            tableId: z.string(),
            schemaName: z.string(),
            tableName: z.string(),
            approximateRowCount: z.number().nullable(),
            stableKeyColumns: z.array(z.string()).nullable(),
            columns: z.array(z.object({ name: z.string(), valueKind: z.string() })),
        }),
    ),
    foreignKeys: z.array(z.object({ foreignKeyId: z.string(), childTableId: z.string(), parentTableId: z.string() })),
    schemaRevision: z.string(),
});

export const selectionsApi = {
    list: (auth: AuthenticationAdapter, signal?: AbortSignal) =>
        requestJson<unknown>('/api/selections', auth, { signal }).then(
            (data) => z.object({ selections: z.array(SavedSelectionSchema) }).parse(data).selections,
        ),
    workbenchSchema: (auth: AuthenticationAdapter, signal?: AbortSignal) =>
        requestJson<unknown>('/api/selections/workbench-schema', auth, { signal }).then((data) =>
            WorkbenchSchemaSchema.parse(data),
        ),
    compile: (body: SelectionRequestBody, auth: AuthenticationAdapter, signal?: AbortSignal) =>
        requestJson<unknown>('/api/selections/compile', auth, { method: 'POST', body, signal }).then((data) =>
            CompilationSchema.parse(data),
        ),
    preview: (body: SelectionRequestBody, auth: AuthenticationAdapter, signal?: AbortSignal) =>
        requestJson<unknown>('/api/selections/preview', auth, { method: 'POST', body, signal }).then((data) =>
            PreviewSchema.parse(data),
        ),
    count: (body: SelectionRequestBody, auth: AuthenticationAdapter, signal?: AbortSignal) =>
        requestJson<unknown>('/api/selections/count', auth, { method: 'POST', body, signal }).then((data) =>
            z.object({ distinctStableKeyCount: z.number() }).parse(data),
        ),
    remove: (selectionId: string, eTag: string, auth: AuthenticationAdapter) =>
        requestJson<void>(`/api/selections/${selectionId}`, auth, { method: 'DELETE', headers: { 'If-Match': eTag } }),
    save: (body: SelectionRequestBody, auth: AuthenticationAdapter) =>
        requestJson<unknown>('/api/selections/save', auth, { method: 'POST', body }).then((data) =>
            SavedSelectionSchema.parse(data),
        ),
};

/** Finds `@name` parameter references in raw SQL, in first-seen order. */
export function parameterNamesIn(sql: string): readonly string[] {
    const seen = new Set<string>();
    for (const match of sql.matchAll(/@([A-Za-z_][A-Za-z0-9_]*)/g)) {
        const name = match[1];
        if (name && !/^@@/.test(match[0])) seen.add(name);
    }
    return [...seen];
}

export function coerceParameterValue(kind: ValueKind, raw: string): string | number | boolean {
    if (kind === 'int') return Number.parseInt(raw, 10);
    if (kind === 'decimal') return Number.parseFloat(raw);
    if (kind === 'boolean') return raw === 'true' || raw === '1';
    return raw;
}

export function validateParameterValue(kind: ValueKind, raw: string): string | null {
    if (raw.trim() === '') return 'Enter a value.';
    if (kind === 'int' && !/^-?\d+$/.test(raw.trim())) return 'Enter a whole number.';
    if (kind === 'decimal' && Number.isNaN(Number.parseFloat(raw))) return 'Enter a number.';
    if (kind === 'boolean' && !['true', 'false', '1', '0'].includes(raw.trim().toLowerCase()))
        return 'Enter true or false.';
    if (kind === 'guid' && !/^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i.test(raw.trim()))
        return 'Enter a GUID.';
    if (kind === 'date' && !/^\d{4}-\d{2}-\d{2}$/.test(raw.trim())) return 'Use YYYY-MM-DD.';
    if (kind === 'time' && !/^\d{2}:\d{2}(:\d{2})?$/.test(raw.trim())) return 'Use HH:MM[:SS].';
    if (kind === 'dateTime' && Number.isNaN(Date.parse(raw))) return 'Enter an ISO date-time.';
    return null;
}
