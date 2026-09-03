import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import {
    connectionsApi,
    defaultBusinessSchema,
    providerLabels,
    type Connection,
    type ConnectionTest,
} from '../../api/connections';
import {
    authOption,
    authOptionsFor,
    buildConnectionString,
    defaultConnectionDetails,
    needsStoredPassword,
    parseConnectionString,
    validateConnectionDetails,
    withProvider,
    type AuthMethod,
    type ConnectionDetails,
} from '../../api/connectionStrings';
import { formatRelative } from '../../api/format';
import { queryKeys } from '../../api/keys';
import { pollOperation, type OperationStatus } from '../../api/operations';
import { describeError } from '../../api/problem';
import { useAuth } from '../../auth/AuthContext';
import { usePermissions } from '../../auth/permissions';
import { Link, navigate } from '../../app/router';
import { sessionActions, useSourceConnectionId, useTargetConnectionId } from '../../stores/sessionStore';
import {
    Alert,
    Badge,
    Button,
    Card,
    cx,
    EmptyState,
    Field,
    IconButton,
    Modal,
    PageHeader,
    ProgressBar,
    SecretInput,
    Select,
    Skeleton,
    StatusBadge,
    Tabs,
    TextArea,
    TextInput,
} from '../../ui';
import { Icons } from '../../ui/icons';
import { useToast } from '../../ui/toast';
import { useConnectionDetails, useConnections, useProviders, useSnapshots } from '../shared/queries';

export function ConnectionsScreen() {
    const connections = useConnections();
    const { hasPermission } = usePermissions();
    const [adding, setAdding] = useState(false);
    const canWrite = hasPermission('Connections.Write');

    return (
        <>
            <PageHeader
                actions={
                    canWrite ? (
                        <Button icon={<Icons.Plus size={16} />} onClick={() => setAdding(true)} variant="primary">
                            Add connection
                        </Button>
                    ) : null
                }
                description="Register the databases DataPitcher may read from and write to, verify they are reachable, and capture schema snapshots."
                title="Connections"
            />

            {connections.isPending ? (
                <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
                    {[0, 1, 2].map((index) => (
                        <Skeleton className="h-52" key={index} />
                    ))}
                </div>
            ) : connections.isError ? (
                <Alert title="Unable to load connections" tone="danger">
                    {describeError(connections.error)}
                </Alert>
            ) : connections.data.length === 0 ? (
                <Card padded={false}>
                    <EmptyState
                        action={
                            canWrite ? (
                                <Button
                                    icon={<Icons.Plus size={16} />}
                                    onClick={() => setAdding(true)}
                                    variant="primary"
                                >
                                    Add your first connection
                                </Button>
                            ) : undefined
                        }
                        description="A transfer needs a source and a target. Add both, check their health, then scan the source schema."
                        icon={<Icons.Plug size={22} />}
                        title="No connections yet"
                    />
                </Card>
            ) : (
                <div className="grid gap-4 md:grid-cols-2 xl:grid-cols-3">
                    {connections.data.map((connection) => (
                        <ConnectionCard connection={connection} key={connection.connectionId} />
                    ))}
                </div>
            )}

            <ConnectionDialog onClose={() => setAdding(false)} open={adding} />
        </>
    );
}

/* ------------------------------ Connection card ---------------------------- */

type ScanProgress = Readonly<{
    operationId: string;
    status: OperationStatus | null;
    phase: 'queued' | 'running' | 'completed' | 'failed' | 'lost';
}>;

function scanFraction(progress: ScanProgress | null): number | null {
    if (!progress) return 0;
    if (progress.phase === 'completed') return 1;
    if (progress.phase === 'failed' || progress.phase === 'lost') return 1;
    return null;
}

function ConnectionCard({ connection }: Readonly<{ connection: Connection }>) {
    const { authentication } = useAuth();
    const { hasPermission } = usePermissions();
    const queryClient = useQueryClient();
    const toast = useToast();
    const sourceId = useSourceConnectionId();
    const targetId = useTargetConnectionId();
    const snapshots = useSnapshots(connection.connectionId);
    const [scan, setScan] = useState<ScanProgress | null>(null);
    const [removing, setRemoving] = useState(false);
    const [editing, setEditing] = useState(false);
    const [checkDetail, setCheckDetail] = useState<ConnectionTest | null>(null);
    const abort = useRef<AbortController | null>(null);
    useEffect(() => () => abort.current?.abort(), []);

    const isSource = sourceId === connection.connectionId;
    const isTarget = targetId === connection.connectionId;
    const canWrite = hasPermission('Connections.Write');
    const canScan = hasPermission('Schema.Write');

    const check = useMutation({
        mutationFn: () => connectionsApi.check(connection.connectionId, authentication),
        onSuccess: async () => {
            await queryClient.invalidateQueries({ queryKey: queryKeys.connections });
            const refreshed = queryClient
                .getQueryData<readonly Connection[]>(queryKeys.connections)
                ?.find((item) => item.connectionId === connection.connectionId);
            const health = refreshed?.health ?? 'Unknown';
            if (health === 'Healthy' || health === 'Degraded') {
                // Healthy. Optional capabilities that are unavailable are shown as a note, never as a failure.
                try {
                    const detail = await connectionsApi.test(
                        { providerId: connection.providerId, connectionId: connection.connectionId },
                        authentication,
                    );
                    setCheckDetail(detail.missingOptional?.length ? detail : null);
                } catch {
                    setCheckDetail(null);
                }
                toast.success(`${connection.displayName} is healthy`);
            } else {
                try {
                    setCheckDetail(
                        await connectionsApi.test(
                            { providerId: connection.providerId, connectionId: connection.connectionId },
                            authentication,
                        ),
                    );
                } catch {
                    setCheckDetail(null);
                }
                toast.push({
                    tone: 'warning',
                    title: `${connection.displayName} is ${health.toLowerCase()}`,
                    description: 'Check the credential environment variable and network access.',
                });
            }
        },
        onError: (error) => toast.error('Health check failed', describeError(error)),
    });

    const startScan = useMutation({
        mutationFn: () => connectionsApi.scan(connection.connectionId, authentication),
        onSuccess: async (receipt) => {
            abort.current?.abort();
            const controller = new AbortController();
            abort.current = controller;
            setScan({ operationId: receipt.operationId, status: null, phase: 'queued' });
            const outcome = await pollOperation(receipt.operationId, authentication, {
                signal: controller.signal,
                onStatus: (status) =>
                    setScan({
                        operationId: receipt.operationId,
                        status,
                        phase: status.state.toLowerCase() === 'running' ? 'running' : 'queued',
                    }),
            });
            if (controller.signal.aborted) return;
            if (outcome.kind === 'finished') {
                const failed = outcome.status.failed;
                setScan({
                    operationId: receipt.operationId,
                    status: outcome.status,
                    phase: failed ? 'failed' : 'completed',
                });
                await queryClient.invalidateQueries({ queryKey: queryKeys.snapshots(connection.connectionId) });
                await queryClient.invalidateQueries({ queryKey: queryKeys.connections });
                if (failed)
                    toast.error(
                        'Schema scan failed',
                        outcome.status.failureDetail ?? outcome.status.failureCode ?? 'The scan did not complete.',
                    );
                else toast.success('Schema snapshot captured', `${connection.displayName} is ready to explore.`);
            } else {
                setScan({
                    operationId: receipt.operationId,
                    status: outcome.kind === 'timeout' ? outcome.last : null,
                    phase: 'lost',
                });
            }
        },
        onError: (error) => toast.error('Unable to start schema scan', describeError(error)),
    });

    const [removingSnapshot, setRemovingSnapshot] = useState(false);
    const removeSnapshot = useMutation({
        mutationFn: (snapshotId: string) =>
            connectionsApi.removeSnapshot(connection.connectionId, snapshotId, authentication),
        onSuccess: async () => {
            setRemovingSnapshot(false);
            await queryClient.invalidateQueries({ queryKey: queryKeys.snapshots(connection.connectionId) });
            toast.success('Snapshot removed');
        },
        onError: (error) => {
            setRemovingSnapshot(false);
            toast.error('Unable to remove the snapshot', describeError(error));
        },
    });
    const remove = useMutation({
        mutationFn: () => connectionsApi.remove(connection.connectionId, connection.eTag, authentication),
        onSuccess: async () => {
            if (isSource || isTarget)
                sessionActions.setConnectionIds(isSource ? null : sourceId, isTarget ? null : targetId);
            setRemoving(false);
            await queryClient.invalidateQueries({ queryKey: queryKeys.connections });
            toast.success('Connection removed', `${connection.displayName} and its schema snapshots were deleted.`);
        },
        onError: (error) => toast.error('Unable to remove the connection', describeError(error)),
    });

    const latest = snapshots.data?.[0] ?? null;
    const scanning = scan !== null && (scan.phase === 'queued' || scan.phase === 'running');

    return (
        <Card className="flex flex-col gap-4" interactive>
            <div className="flex items-start justify-between gap-3">
                <div className="flex min-w-0 items-center gap-3">
                    <ProviderMark providerId={connection.providerId} />
                    <div className="min-w-0">
                        <h3 className="truncate text-[15px] font-semibold text-fg">{connection.displayName}</h3>
                        <p className="text-xs text-fg-muted">
                            {providerLabels[connection.providerId] ?? connection.providerId}
                        </p>
                    </div>
                </div>
                <div className="flex shrink-0 items-center gap-1">
                    <StatusBadge state={connection.health} />
                    {canWrite ? (
                        <>
                            <IconButton label="Edit connection" onClick={() => setEditing(true)} size="sm">
                                <Icons.Clipboard size={15} />
                            </IconButton>
                            <IconButton label="Remove connection" onClick={() => setRemoving(true)} size="sm">
                                <Icons.X size={15} />
                            </IconButton>
                        </>
                    ) : null}
                </div>
            </div>

            <div className="flex flex-wrap gap-2">
                <RoleToggle
                    active={isSource}
                    disabled={!canWrite}
                    icon={<Icons.Database size={13} />}
                    label="Source"
                    onClick={() =>
                        sessionActions.setConnectionIds(
                            isSource ? null : connection.connectionId,
                            isTarget ? null : targetId,
                        )
                    }
                />
                <RoleToggle
                    active={isTarget}
                    disabled={!canWrite}
                    icon={<Icons.Target size={13} />}
                    label="Target"
                    onClick={() =>
                        sessionActions.setConnectionIds(
                            isSource ? null : sourceId,
                            isTarget ? null : connection.connectionId,
                        )
                    }
                    tone="warning"
                />
            </div>

            <div className="rounded-xl bg-surface-2 p-3 text-[13px]">
                {scan ? (
                    <div className="grid gap-2">
                        <ProgressBar
                            detail={
                                scan.phase === 'completed'
                                    ? 'Snapshot captured'
                                    : scan.phase === 'failed'
                                      ? (scan.status?.failureCode ?? 'Failed')
                                      : scan.phase === 'lost'
                                        ? 'Status unknown'
                                        : (scan.status?.state ?? 'Queued')
                            }
                            label="Schema scan"
                            size="sm"
                            striped={scanning}
                            tone={
                                scan.phase === 'completed'
                                    ? 'success'
                                    : scan.phase === 'failed'
                                      ? 'danger'
                                      : scan.phase === 'lost'
                                        ? 'warning'
                                        : 'accent'
                            }
                            value={scanFraction(scan)}
                        />
                        {scan.phase === 'failed' ? (
                            <p className="text-xs break-words text-danger">
                                {scan.status?.failureDetail ??
                                    'The API could not read this schema. Run "Check health" on the connection for details.'}
                            </p>
                        ) : null}
                    </div>
                ) : snapshots.isPending ? (
                    <Skeleton className="h-5 w-40" />
                ) : latest ? (
                    <div className="flex items-center justify-between gap-3">
                        <div>
                            <div className="font-medium text-fg">
                                {snapshots.data?.length ?? 0} snapshot{(snapshots.data?.length ?? 0) === 1 ? '' : 's'}
                            </div>
                            <div className="text-xs text-fg-muted">
                                Latest captured {formatRelative(latest.capturedAtUtc)} ·{' '}
                                <span className="font-mono">{latest.hash.slice(0, 10)}</span>
                            </div>
                        </div>
                        <div className="flex items-center gap-1">
                            <Link
                                className="flex items-center gap-1 text-[13px] font-medium text-accent hover:underline"
                                to={`/schema/${connection.connectionId}/${latest.snapshotId}`}
                            >
                                Explore <Icons.ArrowRight size={14} />
                            </Link>
                            {canScan ? (
                                <IconButton
                                    label="Remove latest snapshot"
                                    onClick={() => setRemovingSnapshot(true)}
                                    size="sm"
                                >
                                    <Icons.X size={14} />
                                </IconButton>
                            ) : null}
                        </div>
                    </div>
                ) : (
                    <div className="text-fg-muted">
                        No schema snapshot yet. Scan the schema to explore tables and relationships.
                    </div>
                )}
            </div>

            {checkDetail?.succeeded && checkDetail.missingOptional?.length ? (
                <Alert tone="info">
                    <div className="font-medium">Healthy, with a note</div>
                    <div className="mt-0.5 text-xs opacity-80">Optional: {checkDetail.missingOptional.join(', ')}</div>
                    <ProbeNotes notes={checkDetail.notes} />
                </Alert>
            ) : null}
            {checkDetail && !checkDetail.succeeded ? (
                <Alert tone="danger">
                    <div className="font-medium">Connection check failed</div>
                    <div className="mt-0.5 break-words">{checkDetail.error ?? 'The database did not answer.'}</div>
                    {checkDetail.missingRequired.length ? (
                        <div className="mt-1 text-xs opacity-80">Missing: {checkDetail.missingRequired.join(', ')}</div>
                    ) : null}
                    <ProbeNotes notes={checkDetail.notes} />
                </Alert>
            ) : null}
            <div className="mt-auto flex flex-wrap gap-2">
                <Button
                    disabled={!canWrite}
                    icon={<Icons.Activity size={14} />}
                    loading={check.isPending}
                    onClick={() => check.mutate()}
                    size="sm"
                >
                    Check health
                </Button>
                <Button
                    disabled={!canScan || scanning}
                    icon={<Icons.Schema size={14} />}
                    loading={startScan.isPending}
                    onClick={() => startScan.mutate()}
                    size="sm"
                >
                    {latest ? 'Rescan schema' : 'Scan schema'}
                </Button>
            </div>

            {editing ? <ConnectionDialog existing={connection} onClose={() => setEditing(false)} open /> : null}
            <Modal
                description="Only DataPitcher's captured copy of the schema is deleted; the database is not touched. Selections built on this snapshot keep it until they are edited or removed."
                footer={
                    <>
                        <Button disabled={removeSnapshot.isPending} onClick={() => setRemovingSnapshot(false)}>
                            Keep
                        </Button>
                        <Button
                            icon={<Icons.X size={15} />}
                            loading={removeSnapshot.isPending}
                            onClick={() => latest && removeSnapshot.mutate(latest.snapshotId)}
                            variant="danger"
                        >
                            Remove snapshot
                        </Button>
                    </>
                }
                onClose={() => setRemovingSnapshot(false)}
                open={removingSnapshot}
                title="Remove the latest snapshot?"
                tone="danger"
            />
            <Modal
                description="This deletes the connection profile and every schema snapshot captured from it. Plans that reference it will no longer load until they are edited."
                footer={
                    <>
                        <Button disabled={remove.isPending} onClick={() => setRemoving(false)}>
                            Keep
                        </Button>
                        <Button
                            icon={<Icons.X size={15} />}
                            loading={remove.isPending}
                            onClick={() => remove.mutate()}
                            variant="danger"
                        >
                            Remove connection
                        </Button>
                    </>
                }
                onClose={() => setRemoving(false)}
                open={removing}
                title={`Remove ${connection.displayName}?`}
                tone="danger"
            >
                <p className="text-sm text-fg-muted">
                    The database itself is not touched. Only DataPitcher&apos;s record of it
                    {snapshots.data?.length
                        ? ` and ${snapshots.data.length} snapshot${snapshots.data.length === 1 ? '' : 's'}`
                        : ''}{' '}
                    will be removed.
                </p>
            </Modal>
        </Card>
    );
}

/** What the probe found on the server: login, schema presence, readable tables. Explains a capability verdict. */
function ProbeNotes({ notes }: Readonly<{ notes: readonly string[] | null | undefined }>) {
    if (!notes?.length) return null;
    return (
        <ul className="mt-1 grid gap-0.5 text-xs opacity-80">
            {notes.map((note) => (
                <li key={note}>{note}</li>
            ))}
        </ul>
    );
}

function RoleToggle({
    active,
    disabled,
    icon,
    label,
    onClick,
    tone = 'accent',
}: Readonly<{
    active: boolean;
    disabled: boolean;
    icon: React.ReactNode;
    label: string;
    onClick: () => void;
    tone?: 'accent' | 'warning';
}>) {
    return (
        <button
            aria-pressed={active}
            className={cx(
                'inline-flex h-7 items-center gap-1.5 rounded-full border px-2.5 text-xs font-semibold transition-colors disabled:cursor-not-allowed disabled:opacity-60',
                active
                    ? tone === 'accent'
                        ? 'border-accent bg-accent text-accent-fg'
                        : 'border-warning bg-warning text-white'
                    : 'border-border text-fg-muted hover:border-border-strong hover:text-fg',
            )}
            disabled={disabled}
            onClick={onClick}
            type="button"
        >
            {active ? <Icons.Check size={13} strokeWidth={3} /> : icon}
            {active ? `Used as ${label.toLowerCase()}` : `Use as ${label.toLowerCase()}`}
        </button>
    );
}

export function ProviderMark({ providerId, size = 'md' }: Readonly<{ providerId: string; size?: 'sm' | 'md' }>) {
    const label =
        providerId === 'sqlserver' ? 'MS' : providerId === 'postgresql' ? 'PG' : providerId.slice(0, 2).toUpperCase();
    return (
        <span
            className={cx(
                'flex shrink-0 items-center justify-center rounded-xl font-bold text-white',
                size === 'sm' ? 'size-7 text-[10px]' : 'size-10 text-xs',
                providerId === 'postgresql' ? 'bg-[#336791]' : 'bg-[#a4373a]',
            )}
        >
            {label}
        </span>
    );
}

/* ------------------------- Add / edit connection dialog ---------------------- */

type CredentialMode = 'details' | 'raw';

type StoredBaseline = Readonly<{
    details: ConnectionDetails;
    raw: string;
    unsupportedKeys: readonly string[];
}>;

type SavePayload = Readonly<{ connectionString: string | null; keepStoredPassword: boolean }>;

function sameDetails(left: ConnectionDetails, right: ConnectionDetails): boolean {
    return (Object.keys(left) as (keyof ConnectionDetails)[]).every((key) => left[key] === right[key]);
}

function ConnectionDialog({
    open,
    onClose,
    existing,
}: Readonly<{ open: boolean; onClose: () => void; existing?: Connection }>) {
    const isEdit = existing !== undefined;
    const { authentication } = useAuth();
    const queryClient = useQueryClient();
    const toast = useToast();
    const providers = useProviders();
    // The API returns the stored connection string minus its password so the form can be prefilled.
    const stored = useConnectionDetails(isEdit && open ? existing.connectionId : null);
    const fallback = useMemo(
        () => defaultConnectionDetails(existing?.providerId ?? 'sqlserver'),
        [existing?.providerId],
    );
    const baseline = useMemo<StoredBaseline | null>(() => {
        if (!stored.data) return null;
        const parsed = parseConnectionString(stored.data.providerId, stored.data.connectionString);
        return { details: parsed.details, raw: stored.data.connectionString, unsupportedKeys: parsed.unsupportedKeys };
    }, [stored.data]);
    const hasStoredPassword = stored.data?.hasPassword ?? false;

    const [displayName, setDisplayName] = useState(existing?.displayName ?? '');
    // Null means "whatever was loaded (or the defaults)": the operator has not touched the credentials yet.
    const [editedDetails, setEditedDetails] = useState<ConnectionDetails | null>(null);
    const [editedRaw, setEditedRaw] = useState<string | null>(null);
    const [schemaChoice, setSchemaChoice] = useState<string | null>(null);
    const [mode, setMode] = useState<CredentialMode>('details');
    const [keepRawPassword, setKeepRawPassword] = useState(true);
    const [credentialId, setCredentialId] = useState(() => crypto.randomUUID());
    const [error, setError] = useState<string | null>(null);
    const [testResult, setTestResult] = useState<ConnectionTest | null>(null);

    const baselineDetails = baseline?.details ?? fallback;
    const baselineRaw = baseline?.raw ?? '';
    const details = editedDetails ?? baselineDetails;
    const rawConnectionString = editedRaw ?? baselineRaw;
    const providerId = details.providerId;
    const businessSchema = schemaChoice ?? stored.data?.businessSchema ?? defaultBusinessSchema(providerId);
    /** Sent only when it differs from what is stored (or was typed while the stored value was unknown). */
    const businessSchemaChange = stored.data
        ? businessSchema.trim() !== stored.data.businessSchema
            ? businessSchema.trim() || null
            : null
        : schemaChoice !== null
          ? schemaChoice.trim() || null
          : null;
    const passwordOptional = isEdit && hasStoredPassword;
    const setDetails = (update: (current: ConnectionDetails) => ConnectionDetails) =>
        setEditedDetails((current) => update(current ?? baselineDetails));
    const patch = (changes: Partial<ConnectionDetails>) => setDetails((current) => ({ ...current, ...changes }));

    function reset() {
        setDisplayName('');
        setEditedDetails(null);
        setEditedRaw(null);
        setSchemaChoice(null);
        setCredentialId(crypto.randomUUID());
        setError(null);
        setTestResult(null);
    }

    const credentialsUnchanged =
        isEdit &&
        (mode === 'details'
            ? sameDetails(details, baselineDetails)
            : rawConnectionString.trim() === baselineRaw.trim());
    const validationProblem =
        mode === 'details'
            ? validateConnectionDetails(details, { passwordOptional })
            : rawConnectionString.trim()
              ? null
              : 'Paste a connection string.';
    const credentialsReady = credentialsUnchanged || validationProblem === null;

    /** What the API should store: null keeps the current secret untouched. */
    function payload(): SavePayload {
        if (credentialsUnchanged) return { connectionString: null, keepStoredPassword: false };
        return mode === 'details'
            ? {
                  connectionString: buildConnectionString(details),
                  keepStoredPassword: passwordOptional && needsStoredPassword(details),
              }
            : { connectionString: rawConnectionString.trim(), keepStoredPassword: passwordOptional && keepRawPassword };
    }

    const test = useMutation({
        mutationFn: () => {
            const { connectionString, keepStoredPassword } = payload();
            return connectionsApi.test(
                connectionString === null
                    ? { providerId, connectionId: existing!.connectionId }
                    : {
                          providerId,
                          connectionString,
                          connectionId: existing?.connectionId ?? null,
                          keepStoredPassword,
                          businessSchema: businessSchema.trim() || null,
                      },
                authentication,
            );
        },
        onSuccess: setTestResult,
        onError: (caught) =>
            setTestResult({
                succeeded: false,
                health: 'Unknown',
                databaseIdentity: null,
                providerVersion: null,
                capabilities: [],
                missingRequired: [],
                error: describeError(caught, 'The test request failed.'),
            }),
    });
    const save = useMutation({
        mutationFn: (input: SavePayload) =>
            existing
                ? connectionsApi.update(
                      existing.connectionId,
                      {
                          displayName: displayName.trim(),
                          providerId,
                          connectionString: input.connectionString,
                          keepStoredPassword: input.keepStoredPassword,
                          businessSchema: businessSchemaChange,
                      },
                      existing.eTag,
                      authentication,
                  )
                : connectionsApi.create(
                      {
                          displayName: displayName.trim(),
                          providerId,
                          credentialId,
                          connectionString: input.connectionString ?? '',
                          businessSchema: businessSchema.trim() || null,
                      },
                      authentication,
                  ),
        onSuccess: async (connection, input) => {
            await queryClient.invalidateQueries({ queryKey: queryKeys.connections });
            if (existing) {
                await queryClient.invalidateQueries({ queryKey: queryKeys.connectionDetails(existing.connectionId) });
                toast.success(
                    'Connection updated',
                    input.connectionString === null
                        ? 'Credentials were left unchanged.'
                        : input.keepStoredPassword
                          ? 'Settings were updated and the stored password was kept.'
                          : 'New credentials are stored on the API host.',
                );
            } else toast.success('Connection added', `${connection.displayName} is registered. Check its health next.`);
            reset();
            onClose();
        },
        onError: (caught) =>
            setError(
                describeError(caught, existing ? 'Unable to update the connection.' : 'Unable to add the connection.'),
            ),
    });

    function submit(event: FormEvent) {
        event.preventDefault();
        setError(null);
        if (!displayName.trim()) return setError('Give the connection a name.');
        if (credentialsUnchanged && existing && existing.providerId !== providerId)
            return setError(
                'Changing the provider requires new credentials. Enter connection details or a connection string.',
            );
        if (!credentialsUnchanged && validationProblem) return setError(validationProblem);
        save.mutate(payload());
    }
    const example =
        providerId === 'postgresql'
            ? 'Host=localhost;Port=5432;Database=app;Username=app;Password=…'
            : 'Server=localhost,1433;Database=app;User Id=sa;Password=…;TrustServerCertificate=True';
    const option = authOption(details);

    return (
        <Modal
            description={
                isEdit
                    ? 'The stored settings are shown below without the password, which never leaves the API host. Leave the password blank to keep it, or type a new one to replace it.'
                    : 'The connection string is stored on the API host under its secrets folder, never in the control database, and is never sent back to the browser.'
            }
            footer={
                <>
                    <Button
                        className="mr-auto"
                        disabled={!credentialsReady}
                        icon={<Icons.Activity size={15} />}
                        loading={test.isPending}
                        onClick={() => test.mutate()}
                    >
                        Test connection
                    </Button>
                    <Button onClick={onClose}>Cancel</Button>
                    <Button form="add-connection" loading={save.isPending} type="submit" variant="primary">
                        {isEdit ? 'Save changes' : 'Add connection'}
                    </Button>
                </>
            }
            onClose={onClose}
            open={open}
            size="lg"
            title={isEdit ? `Edit ${existing.displayName}` : 'Add connection'}
        >
            <form className="grid min-w-0 gap-5" id="add-connection" onSubmit={submit}>
                <div className="grid gap-4 md:grid-cols-[1fr_150px_auto]">
                    <Field label="Display name" required>
                        <TextInput
                            onChange={(event) => setDisplayName(event.target.value)}
                            placeholder="e.g. Production replica"
                            value={displayName}
                        />
                    </Field>
                    <Field label="Schema">
                        <TextInput
                            onChange={(event) => setSchemaChoice(event.target.value)}
                            placeholder={defaultBusinessSchema(providerId)}
                            value={businessSchema}
                        />
                    </Field>
                    <fieldset className="min-w-0">
                        <legend className="mb-1.5 text-[13px] font-medium text-fg-muted">Provider</legend>
                        <div className="flex gap-2">
                            {(
                                providers.data ?? [
                                    { providerId: 'sqlserver', displayName: 'SQL Server' },
                                    { providerId: 'postgresql', displayName: 'PostgreSQL' },
                                ]
                            ).map((provider) => (
                                <label
                                    className={cx(
                                        'flex h-9.5 cursor-pointer items-center gap-2 rounded-lg border px-3 text-sm font-medium transition-colors',
                                        providerId === provider.providerId
                                            ? 'border-accent bg-accent-soft/60 text-fg'
                                            : 'border-border text-fg-muted hover:border-border-strong',
                                    )}
                                    key={provider.providerId}
                                >
                                    <input
                                        checked={providerId === provider.providerId}
                                        className="sr-only"
                                        name="provider"
                                        onChange={() =>
                                            setDetails((current) => withProvider(current, provider.providerId))
                                        }
                                        type="radio"
                                    />
                                    <ProviderMark providerId={provider.providerId} size="sm" />
                                    {provider.displayName}
                                </label>
                            ))}
                        </div>
                    </fieldset>
                </div>

                <Tabs
                    items={[
                        { value: 'details', label: 'Connection details' },
                        { value: 'raw', label: 'Connection string' },
                    ]}
                    onChange={(next) => {
                        setMode(next);
                        setTestResult(null);
                    }}
                    value={mode}
                />

                {isEdit && stored.isPending ? (
                    <Alert tone="info">Loading the stored settings…</Alert>
                ) : isEdit && stored.isError ? (
                    <Alert title="Stored settings could not be read" tone="warning">
                        {describeError(stored.error)} The stored credentials stay as they are unless you enter new ones
                        below.
                    </Alert>
                ) : isEdit && credentialsUnchanged ? (
                    <Alert tone="info">
                        Credentials are unchanged. Only the display name will be updated unless you edit the settings
                        below.
                    </Alert>
                ) : null}

                {mode === 'details' ? (
                    <div className="grid gap-4">
                        {baseline && baseline.unsupportedKeys.length > 0 ? (
                            <Alert tone="warning">
                                The stored connection string also sets{' '}
                                <span className="font-mono">{baseline.unsupportedKeys.join(', ')}</span>, which this
                                form cannot show. Saving from this tab drops them; use the connection string tab to keep
                                them.
                            </Alert>
                        ) : null}
                        <div className="grid gap-4 sm:grid-cols-[1fr_120px_1fr]">
                            <Field label="Server host" required>
                                <TextInput
                                    onChange={(event) => patch({ host: event.target.value })}
                                    placeholder="db.internal"
                                    value={details.host}
                                />
                            </Field>
                            <Field label="Port">
                                <TextInput
                                    inputMode="numeric"
                                    onChange={(event) => patch({ port: event.target.value })}
                                    value={details.port}
                                />
                            </Field>
                            <Field label="Database" required>
                                <TextInput
                                    onChange={(event) => patch({ database: event.target.value })}
                                    placeholder="app"
                                    value={details.database}
                                />
                            </Field>
                        </div>
                        <Field hint={option.description} label="Login method">
                            <Select
                                onChange={(event) => patch({ auth: event.target.value as AuthMethod })}
                                value={details.auth}
                            >
                                {(['Database', 'Microsoft Entra ID'] as const)
                                    .filter((group) => authOptionsFor(providerId).some((item) => item.group === group))
                                    .map((group) => (
                                        <optgroup key={group} label={group}>
                                            {authOptionsFor(providerId)
                                                .filter((item) => item.group === group)
                                                .map((item) => (
                                                    <option key={item.value} value={item.value}>
                                                        {item.label}
                                                    </option>
                                                ))}
                                        </optgroup>
                                    ))}
                            </Select>
                        </Field>
                        {option.usernameLabel || option.passwordLabel ? (
                            <div className="grid gap-4 sm:grid-cols-2">
                                {option.usernameLabel ? (
                                    <Field label={option.usernameLabel} required={option.usernameRequired}>
                                        <TextInput
                                            autoComplete="off"
                                            onChange={(event) => patch({ username: event.target.value })}
                                            value={details.username}
                                        />
                                    </Field>
                                ) : null}
                                {option.passwordLabel ? (
                                    <Field
                                        hint={
                                            passwordOptional
                                                ? `Leave blank to keep the stored ${option.passwordLabel.toLowerCase()}.`
                                                : undefined
                                        }
                                        label={option.passwordLabel}
                                        required={!passwordOptional}
                                    >
                                        <SecretInput
                                            autoComplete="new-password"
                                            onChange={(event) => patch({ password: event.target.value })}
                                            placeholder={passwordOptional ? '••••••••  (unchanged)' : undefined}
                                            value={details.password}
                                        />
                                    </Field>
                                ) : null}
                            </div>
                        ) : null}
                        {details.auth === 'entra-interactive' || details.auth === 'entra-device-code' ? (
                            <Alert tone="warning">
                                This login completes on the API host, not in this browser: the API process opens the
                                sign-in prompt (or prints a device code) when it first connects. Use it for local runs;
                                prefer a managed identity or service principal for servers.
                            </Alert>
                        ) : null}
                        <div className="flex flex-wrap gap-5 text-sm text-fg">
                            <label className="flex items-center gap-2">
                                <input
                                    checked={details.encrypt}
                                    className="accent-accent"
                                    onChange={(event) => patch({ encrypt: event.target.checked })}
                                    type="checkbox"
                                />
                                {providerId === 'postgresql' ? 'Require SSL' : 'Encrypt connection'}
                            </label>
                            <label className={cx('flex items-center gap-2', !details.encrypt && 'opacity-50')}>
                                <input
                                    checked={details.trustServerCertificate}
                                    className="accent-accent"
                                    disabled={!details.encrypt}
                                    onChange={(event) => patch({ trustServerCertificate: event.target.checked })}
                                    type="checkbox"
                                />
                                Trust server certificate
                            </label>
                        </div>
                        <div className="min-w-0 rounded-xl border border-border bg-surface-2 p-3">
                            <div className="mb-1 text-xs font-semibold text-fg-muted">Resulting connection string</div>
                            <code className="block min-w-0 font-mono text-[12px] break-all whitespace-pre-wrap text-fg">
                                {buildConnectionString(details, { maskPassword: true })}
                            </code>
                        </div>
                    </div>
                ) : null}

                {mode === 'raw' ? (
                    <div className="grid gap-4">
                        <Field
                            hint={
                                isEdit
                                    ? 'The stored string is shown without its password. Edit it freely; it is sent once over the API connection and stored on the API host.'
                                    : "Use the provider's native format. It is sent once over the API connection and stored on the API host."
                            }
                            label="Connection string"
                            required
                        >
                            <TextArea
                                autoComplete="off"
                                className="font-mono text-[12.5px]"
                                onChange={(event) => setEditedRaw(event.target.value)}
                                placeholder={example}
                                rows={4}
                                spellCheck={false}
                                value={rawConnectionString}
                            />
                        </Field>
                        {passwordOptional ? (
                            <label className="flex items-start gap-2 text-sm text-fg">
                                <input
                                    checked={keepRawPassword}
                                    className="accent-accent mt-0.5"
                                    onChange={(event) => setKeepRawPassword(event.target.checked)}
                                    type="checkbox"
                                />
                                <span>
                                    Keep the stored password
                                    <span className="block text-xs text-fg-muted">
                                        Appended on the API host when the string above has no password of its own.
                                    </span>
                                </span>
                            </label>
                        ) : null}
                    </div>
                ) : null}

                {testResult ? (
                    testResult.succeeded ? (
                        <Alert title="Connection succeeded" tone="success">
                            {testResult.databaseIdentity ? (
                                <span className="font-mono">{testResult.databaseIdentity}</span>
                            ) : null}
                            {testResult.providerVersion ? <span> · {testResult.providerVersion}</span> : null}
                            <span> · {testResult.capabilities.length} capabilities verified</span>
                            {testResult.missingOptional?.length ? (
                                <div className="mt-1 text-xs opacity-80">
                                    Optional, not available: {testResult.missingOptional.join(', ')}
                                </div>
                            ) : null}
                            <ProbeNotes notes={testResult.notes} />
                        </Alert>
                    ) : (
                        <Alert tone="danger" title={`Connection failed (${testResult.health})`}>
                            <div className="break-words">{testResult.error ?? 'The database did not answer.'}</div>
                            {testResult.missingRequired.length ? (
                                <div className="mt-1 text-xs opacity-80">
                                    Missing capabilities: {testResult.missingRequired.join(', ')}
                                </div>
                            ) : null}
                            <ProbeNotes notes={testResult.notes} />
                        </Alert>
                    )
                ) : null}
                {error ? <Alert tone="danger">{error}</Alert> : null}
            </form>
        </Modal>
    );
}

export function ConnectionPicker({
    value,
    onChange,
    label,
    emphasis,
    hint,
    exclude,
}: Readonly<{
    value: string;
    onChange: (id: string) => void;
    label: string;
    emphasis?: boolean;
    hint?: React.ReactNode;
    exclude?: string;
}>) {
    const connections = useConnections();
    return (
        <Field hint={hint} label={label} required>
            <select
                className={cx(
                    'h-9.5 w-full appearance-none rounded-lg border bg-surface px-3 text-sm text-fg focus:ring-2 focus:ring-accent/25 focus:outline-none',
                    emphasis ? 'border-warning' : 'border-border',
                )}
                onChange={(event) => onChange(event.target.value)}
                value={value}
            >
                <option value="">Choose a connection…</option>
                {(connections.data ?? [])
                    .filter((connection) => connection.connectionId !== exclude)
                    .map((connection) => (
                        <option key={connection.connectionId} value={connection.connectionId}>
                            {connection.displayName} · {providerLabels[connection.providerId] ?? connection.providerId}{' '}
                            · {connection.health}
                        </option>
                    ))}
            </select>
        </Field>
    );
}

export function HealthHint({ connectionId }: Readonly<{ connectionId: string | null }>) {
    const connections = useConnections();
    const connection = connections.data?.find((item) => item.connectionId === connectionId);
    if (!connection) return null;
    const tone = connection.health === 'Healthy' ? 'success' : connection.health === 'Unknown' ? 'neutral' : 'warning';
    return (
        <Badge dot tone={tone}>
            {connection.health}
            {connection.health !== 'Healthy' ? (
                <button className="ml-1 underline" onClick={() => navigate('/connections')} type="button">
                    check
                </button>
            ) : null}
        </Badge>
    );
}
