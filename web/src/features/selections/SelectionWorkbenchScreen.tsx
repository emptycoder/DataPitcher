import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useMemo, useRef, useState, type KeyboardEvent } from 'react';
import type { SnapshotTable } from '../../api/connections';
import { formatNumber } from '../../api/format';
import { queryKeys } from '../../api/keys';
import { describeError, isNotWired } from '../../api/problem';
import {
    coerceParameterValue,
    parameterNamesIn,
    sameQuery,
    selectionsApi,
    validateParameterValue,
    valueKinds,
    type Compilation,
    type ParameterValue,
    type SelectionRequestBody,
    type ValueKind,
} from '../../api/selections';
import { useAuth } from '../../auth/AuthContext';
import { usePermissions } from '../../auth/permissions';
import { navigate, useLocationSearch } from '../../app/router';
import { registryActions } from '../../stores/registryStore';
import { useSourceConnectionId } from '../../stores/sessionStore';
import {
    Alert,
    Badge,
    Button,
    Card,
    CardHeader,
    Code,
    DataTable,
    Field,
    PageHeader,
    ProgressBar,
    Select,
    Skeleton,
    TextInput,
    cx,
} from '../../ui';
import { Icons } from '../../ui/icons';
import { useToast } from '../../ui/toast';
import { tableKey } from '../schema/SchemaGraph';
import { useConnections, useSelection, useSnapshot, useSnapshots } from '../shared/queries';

type ParameterDraft = Readonly<{ name: string; kind: ValueKind; raw: string }>;

/** Sealing requires each stable-key column to be projected under a fixed alias, in key order. */
export function stableKeyAlias(ordinal: number) {
    return `__datapitcher_key_${ordinal}`;
}

export function quoteIdentifier(providerId: string, name: string) {
    return providerId === 'postgresql' ? `"${name.replace(/"/g, '""')}"` : `[${name.replace(/]/g, ']]')}]`;
}

export function defaultSql(table: SnapshotTable, providerId: string) {
    const columns = (table.primaryKey?.columns ?? []).map(
        (column, index) => `${column} AS ${quoteIdentifier(providerId, stableKeyAlias(index))}`,
    );
    return `SELECT ${columns.length ? columns.join(', ') : '*'}\nFROM ${table.schema}.${table.name}\nWHERE 1 = 1\n`;
}

export function missingStableKeyAliases(sql: string, keyColumns: readonly string[]): readonly string[] {
    return keyColumns.map((_, index) => stableKeyAlias(index)).filter((alias) => !sql.includes(alias));
}

/** Builds a new selection, or edits the one at `selectionId` with every field prefilled from the API. */
export function SelectionWorkbenchScreen({ selectionId = null }: Readonly<{ selectionId?: string | null }> = {}) {
    const search = useLocationSearch();
    const { authentication } = useAuth();
    const { hasPermission, isVerified } = usePermissions();
    const toast = useToast();
    const queryClient = useQueryClient();
    const connections = useConnections();
    const sessionSource = useSourceConnectionId();
    const existing = useSelection(selectionId);
    const loaded = existing.data ?? null;
    const loadedRootKey = loaded?.rootSchema && loaded.rootTable ? `${loaded.rootSchema}.${loaded.rootTable}` : null;
    const loadedSql = loaded?.query.rawSql ?? '';

    // Null means "not touched yet": values then come from the selection being edited, the URL, the session's source
    // connection, and the latest snapshot, in that order.
    const [connectionChoice, setConnectionId] = useState<string | null>(search.get('connection'));
    const [snapshotChoice, setSnapshotId] = useState<string | null>(search.get('snapshot'));
    const [rootChoice, setRootKey] = useState<string | null>(search.get('table'));
    const [nameChoice, setName] = useState<string | null>(null);
    const [sqlChoice, setSql] = useState<string | null>(null);
    const [parameterSettings, setParameterSettings] = useState<
        Readonly<Record<string, Readonly<{ kind: ValueKind; raw: string }>>>
    >({});
    const [compilation, setCompilation] = useState<Compilation | null>(null);
    const [compiledFor, setCompiledFor] = useState<string | null>(null);
    const [liveNote, setLiveNote] = useState<string | null>(null);

    const connectionId =
        connectionChoice ?? loaded?.connectionId ?? sessionSource ?? connections.data?.[0]?.connectionId ?? '';
    const snapshots = useSnapshots(connectionId || null);
    const snapshotId = snapshotChoice ?? loaded?.snapshotId ?? snapshots.data?.[0]?.snapshotId ?? '';
    const rootKey = rootChoice ?? loadedRootKey ?? '';
    const name = nameChoice ?? loaded?.displayName ?? '';
    const sql = sqlChoice ?? loadedSql;
    const snapshot = useSnapshot(connectionId || null, snapshotId || null);

    const rootTable = useMemo(
        () => snapshot.data?.tables.find((table) => tableKey(table) === rootKey) ?? null,
        [snapshot.data, rootKey],
    );
    const providerId =
        connections.data?.find((connection) => connection.connectionId === connectionId)?.providerId ?? 'sqlserver';
    const missingAliases = rootTable ? missingStableKeyAliases(sql, rootTable.primaryKey?.columns ?? []) : [];
    const candidateTables = useMemo(
        () => (snapshot.data?.tables ?? []).filter((table) => table.primaryKey !== null),
        [snapshot.data],
    );

    function chooseRoot(key: string) {
        setRootKey(key);
        const table = snapshot.data?.tables.find((item) => tableKey(item) === key);
        if (table && (!sql.trim() || compiledFor === null)) setSql(defaultSql(table, providerId));
        if (table && !name) setName(`${table.name} selection`);
        setCompilation(null);
        setCompiledFor(null);
    }

    // Parameter kinds and values stored with the selection being edited, keyed by name.
    const loadedSettings = useMemo<Readonly<Record<string, Readonly<{ kind: ValueKind; raw: string }>>>>(
        () =>
            Object.fromEntries(
                (loaded?.query.parameters ?? []).map((parameter) => [
                    parameter.name,
                    {
                        kind: (valueKinds as readonly string[]).includes(parameter.kind)
                            ? (parameter.kind as ValueKind)
                            : 'string',
                        raw: String(parameter.value),
                    },
                ]),
            ),
        [loaded],
    );
    // The parameter list follows the @names used in the SQL; kinds and values are remembered per name.
    const parameters = useMemo<readonly ParameterDraft[]>(
        () =>
            parameterNamesIn(sql).map((parameterName) => ({
                name: parameterName,
                ...(parameterSettings[parameterName] ?? loadedSettings[parameterName] ?? { kind: 'int', raw: '' }),
            })),
        [sql, parameterSettings, loadedSettings],
    );
    function updateParameter(parameterName: string, patch: Partial<Readonly<{ kind: ValueKind; raw: string }>>) {
        setParameterSettings((current) => ({
            ...current,
            [parameterName]: {
                kind: 'int',
                raw: '',
                ...(current[parameterName] ?? loadedSettings[parameterName]),
                ...patch,
            },
        }));
    }

    const parameterErrors = parameters.map((parameter) => validateParameterValue(parameter.kind, parameter.raw));
    const parametersValid = parameterErrors.every((error) => error === null);
    const sqlDirty = compiledFor !== sql;

    function body(): SelectionRequestBody {
        const typed: ParameterValue[] = parameters.map((parameter) => ({
            name: parameter.name,
            kind: parameter.kind,
            value: coerceParameterValue(parameter.kind, parameter.raw.trim()),
        }));
        return {
            mode: 'raw',
            visual: null,
            rawSql: sql,
            parameters: typed,
            schemaRevision: snapshot.data?.hash ?? '',
            connectionId: connectionId || null,
            snapshotId: snapshotId || null,
            rootSchema: rootTable?.schema ?? null,
            rootTable: rootTable?.name ?? null,
            stableKeyConstraintName: rootTable?.primaryKey?.name ?? null,
            stableKeyColumns: rootTable?.primaryKey?.columns ?? null,
        };
    }

    const compile = useMutation({
        mutationFn: () => selectionsApi.compile(body(), authentication),
        onSuccess: (result) => {
            setCompilation(result);
            setCompiledFor(sql);
            setLiveNote(null);
        },
        onError: () => {
            setCompilation(null);
            setCompiledFor(null);
        },
    });
    const count = useMutation({
        mutationFn: () => selectionsApi.count(body(), authentication),
        onError: (error) =>
            setLiveNote(
                isNotWired(error)
                    ? 'Live row counting is not wired to a source connection on this API build yet. Sealing the plan will validate and execute the query.'
                    : describeError(error),
            ),
        onSuccess: () => setLiveNote(null),
    });
    const preview = useMutation({
        mutationFn: () => selectionsApi.preview(body(), authentication),
        onError: (error) =>
            setLiveNote(
                isNotWired(error)
                    ? 'Live preview is not wired to a source connection on this API build yet. Sealing the plan will validate and execute the query.'
                    : describeError(error),
            ),
        onSuccess: () => setLiveNote(null),
    });
    const save = useMutation({
        mutationFn: async () => {
            const next = body();
            const displayName = name.trim() || `${rootTable?.name ?? 'Untitled'} selection`;
            if (selectionId && loaded) {
                // Send only what changed; the API keeps everything else as stored.
                const changes = {
                    ...(displayName !== loaded.displayName ? { displayName } : {}),
                    ...(sameQuery(next, loaded.query) ? {} : { query: next }),
                };
                const changed = Object.keys(changes).length > 0;
                if (changed) await selectionsApi.update(selectionId, changes, loaded.eTag, authentication);
                return { id: selectionId, displayName, changed };
            }
            const saved = await selectionsApi.save(next, authentication);
            try {
                await selectionsApi.update(saved.selectionId, { displayName }, saved.eTag, authentication);
            } catch {
                /* The selection is saved; only the name failed to stick. The registry still remembers it. */
            }
            return { id: saved.selectionId, displayName, changed: true };
        },
        onSuccess: async ({ id, displayName, changed }) => {
            registryActions.upsertSelection({
                selectionId: id,
                name: displayName,
                connectionId,
                snapshotId,
                rootTable: rootTable ? tableKey(rootTable) : null,
            });
            await queryClient.invalidateQueries({ queryKey: queryKeys.selections });
            await queryClient.invalidateQueries({ queryKey: queryKeys.selection(id) });
            if (selectionId) {
                if (changed) toast.success('Selection updated', 'Plans that use it must be sealed again.');
                else
                    toast.push({
                        tone: 'info',
                        title: 'Nothing to save',
                        description: 'The selection already matches what is shown.',
                    });
                navigate('/selections');
            } else {
                toast.success('Selection saved', 'Next, pair it with a target in a transfer plan.');
                navigate(`/plans/new?selection=${id}`);
            }
        },
        onError: (error) => toast.error('Unable to save the selection', describeError(error)),
    });

    const checklist = [
        { label: 'Source connection and snapshot', done: Boolean(connectionId && snapshotId) },
        { label: 'Root table with a primary key', done: rootTable !== null },
        {
            label: 'SQL validated',
            done: (compilation !== null && !sqlDirty) || (loaded !== null && sql === loadedSql),
        },
        {
            label: 'Key columns aliased as __datapitcher_key_N',
            done: rootTable !== null && missingAliases.length === 0,
        },
        { label: 'Parameters filled in', done: parametersValid },
    ];
    const doneCount = checklist.filter((item) => item.done).length;
    const canSave = checklist.every((item) => item.done) && hasPermission('Selections.Write') && !save.isPending;
    const canRawSql = !isVerified || hasPermission('Selections.RawSql');

    const editorRef = useRef<HTMLTextAreaElement>(null);
    function onEditorKey(event: KeyboardEvent<HTMLTextAreaElement>) {
        if (event.key === 'Tab') {
            event.preventDefault();
            const element = event.currentTarget;
            const start = element.selectionStart;
            const end = element.selectionEnd;
            const next = `${sql.slice(0, start)}  ${sql.slice(end)}`;
            setSql(next);
            requestAnimationFrame(() => element.setSelectionRange(start + 2, start + 2));
        }
        if ((event.metaKey || event.ctrlKey) && event.key === 'Enter' && sql.trim()) compile.mutate();
    }
    const lineCount = Math.max(1, sql.split('\n').length);
    const saveLabel = selectionId ? 'Save changes' : 'Save selection';

    if (selectionId && existing.isPending)
        return (
            <>
                <PageHeader eyebrow="Edit selection" title="Loading selection…" />
                <Skeleton className="h-64" />
            </>
        );
    if (selectionId && existing.isError)
        return (
            <>
                <PageHeader eyebrow="Edit selection" title="Selection" />
                <Alert title="Unable to load this selection" tone="danger">
                    {describeError(existing.error)} It may have been removed from the API.
                </Alert>
            </>
        );

    return (
        <>
            <PageHeader
                actions={
                    <>
                        {selectionId ? (
                            <Button onClick={() => navigate('/selections')} variant="ghost">
                                Cancel
                            </Button>
                        ) : null}
                        <Button
                            disabled={!canSave}
                            icon={<Icons.Check size={16} />}
                            loading={save.isPending}
                            onClick={() => save.mutate()}
                            variant="primary"
                        >
                            {saveLabel}
                        </Button>
                    </>
                }
                description={
                    selectionId
                        ? 'Every field below is prefilled from the saved selection. Only what you change is sent back.'
                        : "Write a SELECT that returns the root table's stable key columns. Only those rows and their required parents move."
                }
                eyebrow={selectionId ? 'Edit selection' : 'Selection workbench'}
                title={name || (selectionId ? 'Edit selection' : 'New selection')}
            />

            <div className="grid gap-5 xl:grid-cols-[320px_1fr_300px]">
                {/* Scope */}
                <div className="grid content-start gap-4">
                    <Card>
                        <CardHeader icon={<Icons.Database size={16} />} title="Scope" />
                        <div className="grid gap-4">
                            <Field label="Source connection" required>
                                <Select
                                    onChange={(event) => {
                                        setConnectionId(event.target.value);
                                        setSnapshotId(null);
                                        setRootKey('');
                                    }}
                                    value={connectionId}
                                >
                                    <option value="">Choose…</option>
                                    {(connections.data ?? []).map((connection) => (
                                        <option key={connection.connectionId} value={connection.connectionId}>
                                            {connection.displayName}
                                        </option>
                                    ))}
                                </Select>
                            </Field>
                            <Field
                                hint={
                                    snapshots.data && snapshots.data.length === 0
                                        ? 'No snapshot yet. Scan the schema from Connections.'
                                        : undefined
                                }
                                label="Schema snapshot"
                                required
                            >
                                <Select
                                    disabled={!connectionId}
                                    onChange={(event) => {
                                        setSnapshotId(event.target.value);
                                        setRootKey('');
                                    }}
                                    value={snapshotId}
                                >
                                    <option value="">Choose…</option>
                                    {(snapshots.data ?? []).map((item, index) => (
                                        <option key={item.snapshotId} value={item.snapshotId}>
                                            {index === 0 ? 'Latest · ' : ''}
                                            {item.hash.slice(0, 10)}
                                        </option>
                                    ))}
                                </Select>
                            </Field>
                            <Field hint="Only tables with a primary key can be a root." label="Root table" required>
                                <Select
                                    disabled={!snapshot.data}
                                    onChange={(event) => chooseRoot(event.target.value)}
                                    value={rootKey}
                                >
                                    <option value="">Choose…</option>
                                    {candidateTables.map((table) => (
                                        <option key={tableKey(table)} value={tableKey(table)}>
                                            {tableKey(table)}
                                        </option>
                                    ))}
                                </Select>
                            </Field>
                            {rootTable ? (
                                <div className="rounded-xl bg-surface-2 p-3 text-[13px]">
                                    <div className="flex items-center gap-1.5 font-semibold text-fg">
                                        <Icons.Key className="text-accent" size={14} /> Stable key
                                    </div>
                                    <div className="mt-1 font-mono text-[12px] text-fg-muted">
                                        {rootTable.primaryKey!.name}
                                    </div>
                                    <div className="mt-1 flex flex-wrap gap-1">
                                        {rootTable.primaryKey!.columns.map((column) => (
                                            <Badge key={column} tone="accent">
                                                {column}
                                            </Badge>
                                        ))}
                                    </div>
                                </div>
                            ) : null}
                            <Field label="Selection name">
                                <TextInput
                                    onChange={(event) => setName(event.target.value)}
                                    placeholder="e.g. Orders for customer 42"
                                    value={name}
                                />
                            </Field>
                        </div>
                    </Card>
                </div>

                {/* Editor */}
                <div className="grid content-start gap-4">
                    <Card padded={false}>
                        <div className="flex items-center justify-between gap-3 border-b border-border px-4 py-2.5">
                            <div className="flex items-center gap-2 text-[13px] font-semibold text-fg">
                                <Icons.Code size={15} /> Raw SQL
                                {compilation && !sqlDirty ? (
                                    <Badge dot tone="success">
                                        Validated
                                    </Badge>
                                ) : sql.trim() ? (
                                    <Badge dot tone="warning">
                                        Not validated
                                    </Badge>
                                ) : null}
                            </div>
                            <div className="flex items-center gap-2">
                                <span className="hidden text-[11px] text-fg-faint sm:inline">
                                    ⌘/Ctrl + Enter to validate
                                </span>
                                <Button
                                    disabled={!sql.trim() || !canRawSql}
                                    icon={<Icons.Check size={14} />}
                                    loading={compile.isPending}
                                    onClick={() => compile.mutate()}
                                    size="sm"
                                    variant="primary"
                                >
                                    Validate
                                </Button>
                            </div>
                        </div>
                        <div className="relative flex bg-surface font-mono text-[13px] leading-6">
                            <div
                                aria-hidden="true"
                                className="w-11 shrink-0 border-r border-border bg-surface-2 py-3 text-right text-fg-faint select-none"
                            >
                                {Array.from({ length: lineCount }, (_, index) => (
                                    <div className="pr-2" key={index}>
                                        {index + 1}
                                    </div>
                                ))}
                            </div>
                            <textarea
                                aria-label="Selection SQL"
                                className="dp-editor min-h-72 flex-1 resize-y bg-transparent px-4 py-3 text-fg outline-none placeholder:text-fg-faint"
                                disabled={!canRawSql}
                                onChange={(event) => setSql(event.target.value)}
                                onKeyDown={onEditorKey}
                                placeholder={
                                    rootTable
                                        ? undefined
                                        : 'Choose a root table to start from a template, or write SQL that returns the root key columns.'
                                }
                                ref={editorRef}
                                spellCheck={false}
                                value={sql}
                            />
                        </div>
                        {!canRawSql ? (
                            <div className="border-t border-border px-4 py-2 text-xs text-warning">
                                Editing raw SQL requires the Selections.RawSql permission.
                            </div>
                        ) : null}
                    </Card>

                    {compile.isError ? (
                        <Alert title="SQL rejected" tone="danger">
                            {isNotWired(compile.error)
                                ? 'The safety validator rejected this statement. Use a single SELECT with no data-modifying keywords, comments, or batch separators.'
                                : describeError(compile.error)}
                        </Alert>
                    ) : null}
                    {compilation && !sqlDirty && compilation.warnings.length > 0 ? (
                        <Alert title="Warnings" tone="warning">
                            <ul className="list-disc pl-4">
                                {compilation.warnings.map((warning) => (
                                    <li key={warning}>{warning}</li>
                                ))}
                            </ul>
                        </Alert>
                    ) : null}

                    <Card>
                        <CardHeader
                            description={
                                parameters.length === 0
                                    ? 'Reference parameters as @name in the SQL to add them here.'
                                    : 'Values are sent typed. They never leave this form until you save.'
                            }
                            icon={<Icons.Zap size={16} />}
                            title={`Parameters${parameters.length ? ` (${parameters.length})` : ''}`}
                        />
                        {parameters.length > 0 ? (
                            <DataTable>
                                <thead>
                                    <tr>
                                        <th>Name</th>
                                        <th>Kind</th>
                                        <th>Value</th>
                                    </tr>
                                </thead>
                                <tbody>
                                    {parameters.map((parameter, index) => (
                                        <tr key={parameter.name}>
                                            <td className="font-mono text-[12.5px]">@{parameter.name}</td>
                                            <td className="w-40">
                                                <Select
                                                    aria-label={`Kind of @${parameter.name}`}
                                                    onChange={(event) =>
                                                        updateParameter(parameter.name, {
                                                            kind: event.target.value as ValueKind,
                                                        })
                                                    }
                                                    value={parameter.kind}
                                                >
                                                    {valueKinds.map((kind) => (
                                                        <option key={kind} value={kind}>
                                                            {kind}
                                                        </option>
                                                    ))}
                                                </Select>
                                            </td>
                                            <td>
                                                <TextInput
                                                    aria-invalid={parameterErrors[index] !== null}
                                                    aria-label={`Value of @${parameter.name}`}
                                                    className="font-mono"
                                                    onChange={(event) =>
                                                        updateParameter(parameter.name, { raw: event.target.value })
                                                    }
                                                    placeholder={
                                                        parameter.kind === 'boolean'
                                                            ? 'true / false'
                                                            : parameter.kind === 'date'
                                                              ? 'YYYY-MM-DD'
                                                              : ''
                                                    }
                                                    value={parameter.raw}
                                                />
                                                {parameterErrors[index] ? (
                                                    <div className="mt-1 text-xs text-danger">
                                                        {parameterErrors[index]}
                                                    </div>
                                                ) : null}
                                            </td>
                                        </tr>
                                    ))}
                                </tbody>
                            </DataTable>
                        ) : null}
                        {count.data ? (
                            <p className="mt-3 text-sm text-fg">
                                <strong className="tnum">{formatNumber(count.data.distinctStableKeyCount)}</strong>{' '}
                                distinct root keys.
                            </p>
                        ) : null}
                        {preview.data ? (
                            <div className="mt-3">
                                <DataTable>
                                    <thead>
                                        <tr>
                                            {preview.data.columns.map((column) => (
                                                <th key={column}>{column}</th>
                                            ))}
                                        </tr>
                                    </thead>
                                    <tbody>
                                        {preview.data.rows.map((row, index) => (
                                            <tr key={index}>
                                                {preview.data.columns.map((column) => (
                                                    <td className="font-mono text-[12px]" key={column}>
                                                        {String(row[column] ?? 'NULL')}
                                                    </td>
                                                ))}
                                            </tr>
                                        ))}
                                    </tbody>
                                </DataTable>
                                {preview.data.hasMore ? (
                                    <p className="mt-2 text-xs text-fg-faint">More rows exist.</p>
                                ) : null}
                            </div>
                        ) : null}
                        {liveNote ? (
                            <Alert className="mt-3" tone="info">
                                {liveNote}
                            </Alert>
                        ) : null}
                        <div className="mt-4 flex flex-wrap gap-2 border-t border-border pt-4">
                            <Button
                                disabled={!sql.trim()}
                                icon={<Icons.Activity size={14} />}
                                loading={count.isPending}
                                onClick={() => count.mutate()}
                                size="sm"
                            >
                                Count keys
                            </Button>
                            <Button
                                disabled={!sql.trim()}
                                icon={<Icons.Eye size={14} />}
                                loading={preview.isPending}
                                onClick={() => preview.mutate()}
                                size="sm"
                            >
                                Preview rows
                            </Button>
                            <span className="self-center text-xs text-fg-faint">
                                Live checks run against the source connection.
                            </span>
                        </div>
                    </Card>
                </div>

                {/* Checklist */}
                <div className="grid content-start gap-4">
                    <Card>
                        <CardHeader icon={<Icons.Sparkles size={16} />} title="Ready to save?" />
                        <ProgressBar
                            detail={`${doneCount} of ${checklist.length}`}
                            label="Checklist"
                            size="sm"
                            tone={doneCount === checklist.length ? 'success' : 'accent'}
                            value={doneCount / checklist.length}
                        />
                        <ul className="mt-4 grid gap-2">
                            {checklist.map((item) => (
                                <li
                                    className={cx(
                                        'flex items-center gap-2.5 text-[13px]',
                                        item.done ? 'text-fg' : 'text-fg-muted',
                                    )}
                                    key={item.label}
                                >
                                    <span
                                        className={cx(
                                            'flex size-5 shrink-0 items-center justify-center rounded-full',
                                            item.done ? 'bg-success text-white' : 'border border-border-strong',
                                        )}
                                    >
                                        {item.done ? <Icons.Check size={12} strokeWidth={3} /> : null}
                                    </span>
                                    {item.label}
                                </li>
                            ))}
                        </ul>
                        <Button
                            block
                            className="mt-5"
                            disabled={!canSave}
                            icon={<Icons.Check size={16} />}
                            loading={save.isPending}
                            onClick={() => save.mutate()}
                            variant="primary"
                        >
                            {saveLabel}
                        </Button>
                    </Card>
                    <Card className="text-[13px] text-fg-muted">
                        <div className="mb-2 flex items-center gap-2 font-semibold text-fg">
                            <Icons.Info size={15} /> How selection works
                        </div>
                        <ul className="grid gap-2">
                            <li>
                                Project each stable key column of the root (its primary key) as{' '}
                                <Code>__datapitcher_key_0</Code>, <Code>__datapitcher_key_1</Code>… in key order. The
                                template does this for you.
                            </li>
                            <li>
                                Rows referenced through foreign keys are added automatically. Child rows are{' '}
                                <em>not</em>.
                            </li>
                            <li>Joins only help find keys. Joined tables never become transfer roots.</li>
                            <li>
                                The statement must be a single read-only <Code>SELECT</Code>.
                            </li>
                        </ul>
                    </Card>
                </div>
            </div>
        </>
    );
}
