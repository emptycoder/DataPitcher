import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { queryKeys } from '../../api/keys';
import { selectionsApi, type SavedSelection } from '../../api/selections';
import { useAuth } from '../../auth/AuthContext';
import { useToast } from '../../ui/toast';
import { formatRelative } from '../../api/format';
import { describeError } from '../../api/problem';
import { usePermissions } from '../../auth/permissions';
import { Link, navigate } from '../../app/router';
import { registryActions, usePlanRegistry, useSelectionRegistry } from '../../stores/registryStore';
import {
    Alert,
    Badge,
    Button,
    Card,
    DataTable,
    EmptyState,
    IconButton,
    Modal,
    PageHeader,
    Skeleton,
    shortId,
} from '../../ui';
import { Icons } from '../../ui/icons';
import { useConnections, useSelections } from '../shared/queries';

export function SelectionsScreen() {
    const selections = useSelections();
    const connections = useConnections();
    const registry = useSelectionRegistry();
    const { hasPermission } = usePermissions();
    const { authentication } = useAuth();
    const queryClient = useQueryClient();
    const toast = useToast();
    const plans = usePlanRegistry();
    const canWrite = hasPermission('Selections.Write');
    const [removing, setRemoving] = useState<SavedSelection | null>(null);
    const remove = useMutation({
        mutationFn: (selection: SavedSelection) =>
            selectionsApi.remove(selection.selectionId, selection.eTag, authentication),
        onSuccess: async (_, selection) => {
            registryActions.forgetSelection(selection.selectionId);
            setRemoving(null);
            await queryClient.invalidateQueries({ queryKey: queryKeys.selections });
            toast.success('Selection removed');
        },
        onError: (error) => toast.error('Unable to remove the selection', describeError(error)),
    });
    const plansUsing = (selectionId: string) => Object.values(plans).filter((plan) => plan.selectionId === selectionId);

    const rows = useMemo(
        () =>
            (selections.data ?? [])
                .map((selection) => ({ selection, entry: registry[selection.selectionId] ?? null }))
                .toSorted((a, b) => (b.entry?.savedAt ?? '').localeCompare(a.entry?.savedAt ?? '')),
        [selections.data, registry],
    );

    return (
        <>
            <PageHeader
                actions={
                    canWrite ? (
                        <Button
                            icon={<Icons.Plus size={16} />}
                            onClick={() => navigate('/selections/new')}
                            variant="primary"
                        >
                            New selection
                        </Button>
                    ) : null
                }
                description="A selection names the exact root rows to transfer. DataPitcher computes the minimal dependency set from there."
                title="Selections"
            />
            {selections.isPending ? (
                <Skeleton className="h-64" />
            ) : selections.isError ? (
                <Alert tone="danger">{describeError(selections.error)}</Alert>
            ) : rows.length === 0 ? (
                <Card padded={false}>
                    <EmptyState
                        action={
                            canWrite ? (
                                <Button
                                    icon={<Icons.Plus size={16} />}
                                    onClick={() => navigate('/selections/new')}
                                    variant="primary"
                                >
                                    Build a selection
                                </Button>
                            ) : undefined
                        }
                        description="Pick a root table with a primary key, write a SELECT that returns its key columns, and save it."
                        icon={<Icons.Filter size={22} />}
                        title="No saved selections"
                    />
                </Card>
            ) : (
                <Card padded={false}>
                    <DataTable>
                        <thead>
                            <tr>
                                <th>Name</th>
                                <th>Root table</th>
                                <th>Connection</th>
                                <th>Mode</th>
                                <th>Version</th>
                                <th>Saved</th>
                                <th />
                            </tr>
                        </thead>
                        <tbody>
                            {rows.map(({ selection, entry }) => {
                                const connection = connections.data?.find(
                                    (item) => item.connectionId === entry?.connectionId,
                                );
                                return (
                                    <tr className="hover:bg-surface-2" key={selection.selectionId}>
                                        <td>
                                            <div className="font-semibold text-fg">
                                                {entry?.name || selection.displayName || 'Untitled selection'}
                                            </div>
                                            <div className="font-mono text-[11px] text-fg-faint">
                                                {shortId(selection.selectionId)}
                                            </div>
                                        </td>
                                        <td className="font-mono text-[12.5px]">
                                            {entry?.rootTable ?? <span className="text-fg-faint">unknown</span>}
                                        </td>
                                        <td>{connection?.displayName ?? <span className="text-fg-faint">—</span>}</td>
                                        <td>
                                            <Badge tone={selection.mode === 'raw' ? 'accent' : 'neutral'}>
                                                {selection.mode === 'raw' ? 'Raw SQL' : selection.mode}
                                            </Badge>
                                        </td>
                                        <td className="tnum text-fg-muted">v{selection.version}</td>
                                        <td className="text-fg-muted">{entry ? formatRelative(entry.savedAt) : '—'}</td>
                                        <td className="whitespace-nowrap">
                                            <div className="flex items-center justify-end gap-1">
                                                <Link
                                                    className="inline-flex h-8 items-center gap-1 rounded-lg px-2 text-[13px] font-medium text-accent hover:bg-surface-2"
                                                    to={`/plans/new?selection=${selection.selectionId}`}
                                                >
                                                    Plan a transfer <Icons.ArrowRight size={14} />
                                                </Link>
                                                {canWrite ? (
                                                    <IconButton
                                                        label="Remove selection"
                                                        onClick={() => setRemoving(selection)}
                                                        size="sm"
                                                    >
                                                        <Icons.X size={14} />
                                                    </IconButton>
                                                ) : null}
                                            </div>
                                        </td>
                                    </tr>
                                );
                            })}
                        </tbody>
                    </DataTable>
                </Card>
            )}
            <Modal
                description="The saved query, root table and stable key are deleted from the API."
                footer={
                    <>
                        <Button disabled={remove.isPending} onClick={() => setRemoving(null)}>
                            Keep
                        </Button>
                        <Button
                            icon={<Icons.X size={15} />}
                            loading={remove.isPending}
                            onClick={() => removing && remove.mutate(removing)}
                            variant="danger"
                        >
                            Remove selection
                        </Button>
                    </>
                }
                onClose={() => setRemoving(null)}
                open={removing !== null}
                title={`Remove ${removing ? registry[removing.selectionId]?.name || removing.displayName || 'this selection' : ''}?`}
                tone="danger"
            >
                {removing && plansUsing(removing.selectionId).length > 0 ? (
                    <Alert tone="warning">
                        {plansUsing(removing.selectionId).length} plan
                        {plansUsing(removing.selectionId).length === 1 ? '' : 's'} on this device reference it (
                        {plansUsing(removing.selectionId)
                            .map((plan) => plan.name || shortId(plan.planId))
                            .join(', ')}
                        ). They will stop loading until they are edited to use another selection.
                    </Alert>
                ) : (
                    <p className="text-sm text-fg-muted">
                        Plans that reference this selection will stop loading until they are edited.
                    </p>
                )}
            </Modal>
            <p className="mt-4 text-xs text-fg-faint">
                Names and root tables are remembered in this browser. The API stores the query, root table and stable
                key, but does not expose a display name yet.
            </p>
        </>
    );
}
