import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useEffect, useRef, useState, type FormEvent } from 'react';
import { connectionsApi, credentialEnvironmentVariable, providerLabels, type Connection } from '../../api/connections';
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
    Code,
    CopyButton,
    EmptyState,
    Field,
    IconButton,
    Modal,
    PageHeader,
    ProgressBar,
    Skeleton,
    StatusBadge,
    TextInput,
    cx,
} from '../../ui';
import { Icons } from '../../ui/icons';
import { useToast } from '../../ui/toast';
import { useConnections, useProviders, useSnapshots } from '../shared/queries';

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

            <AddConnectionDialog onClose={() => setAdding(false)} open={adding} />
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
            if (health === 'Healthy') toast.success(`${connection.displayName} is healthy`);
            else
                toast.push({
                    tone: 'warning',
                    title: `${connection.displayName} is ${health.toLowerCase()}`,
                    description: 'Check the credential environment variable and network access.',
                });
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
                    toast.error('Schema scan failed', outcome.status.failureCode ?? 'The scan did not complete.');
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
                        <IconButton label="Remove connection" onClick={() => setRemoving(true)} size="sm">
                            <Icons.X size={15} />
                        </IconButton>
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
                            <p className="text-xs text-danger">
                                The API could not introspect this database. Make sure{' '}
                                <Code>{'DATAPITCHER_CREDENTIAL_…'}</Code> is set for the API process.
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
                        <Link
                            className="flex items-center gap-1 text-[13px] font-medium text-accent hover:underline"
                            to={`/schema/${connection.connectionId}/${latest.snapshotId}`}
                        >
                            Explore <Icons.ArrowRight size={14} />
                        </Link>
                    </div>
                ) : (
                    <div className="text-fg-muted">
                        No schema snapshot yet. Scan the schema to explore tables and relationships.
                    </div>
                )}
            </div>

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

/* --------------------------- Add connection dialog ------------------------- */

function AddConnectionDialog({ open, onClose }: Readonly<{ open: boolean; onClose: () => void }>) {
    const { authentication } = useAuth();
    const queryClient = useQueryClient();
    const toast = useToast();
    const providers = useProviders();
    const [displayName, setDisplayName] = useState('');
    const [providerId, setProviderId] = useState('sqlserver');
    const [credentialId, setCredentialId] = useState(() => crypto.randomUUID());
    const [error, setError] = useState<string | null>(null);

    const create = useMutation({
        mutationFn: () =>
            connectionsApi.create({ displayName: displayName.trim(), providerId, credentialId }, authentication),
        onSuccess: async (connection) => {
            await queryClient.invalidateQueries({ queryKey: queryKeys.connections });
            toast.success('Connection added', `${connection.displayName} is registered. Check its health next.`);
            setDisplayName('');
            setCredentialId(crypto.randomUUID());
            onClose();
        },
        onError: (caught) => setError(describeError(caught, 'Unable to add the connection.')),
    });

    function submit(event: FormEvent) {
        event.preventDefault();
        setError(null);
        if (!displayName.trim()) {
            setError('Give the connection a name.');
            return;
        }
        create.mutate();
    }

    const envVar = credentialEnvironmentVariable(credentialId);
    const example =
        providerId === 'postgresql'
            ? 'Host=localhost;Port=5432;Database=app;Username=app;Password=…'
            : 'Server=localhost,1433;Database=app;User Id=sa;Password=…;TrustServerCertificate=True';

    return (
        <Modal
            description="DataPitcher never stores connection strings. It reads them from an environment variable of the API process at connection time."
            footer={
                <>
                    <Button onClick={onClose}>Cancel</Button>
                    <Button form="add-connection" loading={create.isPending} type="submit" variant="primary">
                        Add connection
                    </Button>
                </>
            }
            onClose={onClose}
            open={open}
            size="lg"
            title="Add connection"
        >
            <form className="grid min-w-0 gap-5" id="add-connection" onSubmit={submit}>
                <Field label="Display name" required>
                    <TextInput
                        onChange={(event) => setDisplayName(event.target.value)}
                        placeholder="e.g. Production replica"
                        value={displayName}
                    />
                </Field>
                <fieldset className="min-w-0">
                    <legend className="mb-1.5 text-[13px] font-medium text-fg-muted">Provider</legend>
                    <div className="grid gap-2 sm:grid-cols-2">
                        {(
                            providers.data ?? [
                                { providerId: 'sqlserver', displayName: 'SQL Server' },
                                { providerId: 'postgresql', displayName: 'PostgreSQL' },
                            ]
                        ).map((provider) => (
                            <label
                                className={cx(
                                    'flex cursor-pointer items-center gap-2 rounded-xl border p-2.5 text-sm font-medium transition-colors',
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
                                    onChange={() => setProviderId(provider.providerId)}
                                    type="radio"
                                />
                                <ProviderMark providerId={provider.providerId} size="sm" />
                                {provider.displayName}
                            </label>
                        ))}
                    </div>
                </fieldset>

                <div className="min-w-0 overflow-hidden rounded-xl border border-border bg-surface-2 p-4">
                    <div className="flex items-center justify-between gap-3">
                        <div className="text-[13px] font-semibold text-fg">Credential</div>
                        <Button
                            icon={<Icons.Refresh size={14} />}
                            onClick={() => setCredentialId(crypto.randomUUID())}
                            size="sm"
                            variant="ghost"
                        >
                            New ID
                        </Button>
                    </div>
                    <p className="mt-1 text-[13px] text-fg-muted">
                        Before checking health, export the connection string under this variable in the shell that runs
                        the API, then restart it:
                    </p>
                    <div className="mt-3 flex items-start gap-2 rounded-lg bg-surface p-2 font-mono text-[12px] text-fg">
                        <code className="min-w-0 flex-1 break-all whitespace-pre-wrap">
                            export {envVar}=&quot;{example}&quot;
                        </code>
                        <CopyButton label="Copy" value={`export ${envVar}="${example}"`} />
                    </div>
                    <div className="mt-2 flex flex-wrap items-center gap-2 text-xs text-fg-faint">
                        Credential ID <Code className="break-all">{credentialId}</Code>
                    </div>
                </div>

                {providerId === 'postgresql' ? (
                    <Alert tone="warning">
                        Plan sealing and transfer execution currently run against SQL Server only. PostgreSQL
                        connections can be registered and scanned.
                    </Alert>
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
