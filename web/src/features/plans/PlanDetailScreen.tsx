import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useRef, useState, type FormEvent } from 'react';
import { formatBytes, formatNumber } from '../../api/format';
import { queryKeys } from '../../api/keys';
import { isSealed, plansApi, type PlanReview, type PlanTable } from '../../api/plans';
import { describeError, isNotWired } from '../../api/problem';
import { useAuth } from '../../auth/AuthContext';
import { usePermissions } from '../../auth/permissions';
import { Link, navigate } from '../../app/router';
import { registryActions, usePlanEntry, useSelectionRegistry } from '../../stores/registryStore';
import {
  Alert,
  Badge,
  Button,
  Card,
  CardHeader,
  Code,
  EmptyState,
  Field,
  KeyValue,
  Modal,
  PageHeader,
  ProgressBar,
  Skeleton,
  Stat,
  StatusBadge,
  Tabs,
  TextInput,
  cx,
  shortId,
} from '../../ui';
import { Icons } from '../../ui/icons';
import { useToast } from '../../ui/toast';
import { ProviderMark } from '../connections/ConnectionsScreen';
import type { SchemaGraphProjection, SchemaTableAddress } from '../graph/graphLayout';
import { SchemaGraph, tableKey, type NodeTone } from '../schema/SchemaGraph';
import { usePlan, usePlanReview } from '../shared/queries';

type Tab = 'tables' | 'graph' | 'checks' | 'trace';

export function PlanDetailScreen({ planId }: Readonly<{ planId: string }>) {
  const { authentication } = useAuth();
  const { hasPermission } = usePermissions();
  const queryClient = useQueryClient();
  const toast = useToast();
  const entry = usePlanEntry(planId);
  const selectionNames = useSelectionRegistry();
  const stored = usePlan(planId);
  const review = usePlanReview(planId);
  const [tab, setTab] = useState<Tab>('tables');
  const [confirming, setConfirming] = useState(false);
  const idempotencyKey = useRef<string | null>(null);

  const sealed = isSealed(review.data);
  const associated = Boolean(review.data?.selection && review.data.source && review.data.target);

  // Keep the local registry in sync with what the server reports.
  useEffect(() => {
    if (!review.data) return;
    registryActions.upsertPlan({
      planId,
      selectionId: review.data.selection?.selectionId ?? null,
      sourceConnectionId: review.data.source?.connectionId ?? null,
      targetConnectionId: review.data.target?.connectionId ?? null,
      sealed: isSealed(review.data),
      plannedWrites: isSealed(review.data) ? review.data.totals.plannedWrites : null,
    });
  }, [planId, review.data]);
  useEffect(() => {
    if (!stored.data) return;
    registryActions.upsertPlan({ planId, name: stored.data.displayName, note: stored.data.operatorNote });
  }, [planId, stored.data]);

  const seal = useMutation({
    mutationFn: () => plansApi.seal(planId, authentication),
    onSuccess: async () => {
      const latest = await queryClient.fetchQuery({ queryKey: queryKeys.planReview(planId), queryFn: ({ signal }) => plansApi.review(planId, authentication, signal) });
      await queryClient.invalidateQueries({ queryKey: queryKeys.planGraph(planId) });
      if (isSealed(latest)) toast.success('Plan sealed', `${formatNumber(latest.totals.plannedWrites)} rows across ${latest.tables.length} tables are ready to transfer.`);
      else toast.push({ tone: 'warning', title: 'Plan is not sealed yet', description: latest.blockers[0]?.message ?? 'The plan needs a selection, a source and a target.' });
    },
    onError: (error) =>
      toast.error(
        'Sealing failed',
        isNotWired(error)
          ? 'Sealing reads the source and target databases. Check that both connections are healthy and that the selection SQL runs against the source.'
          : describeError(error, 'The closure could not be computed.'),
      ),
  });

  const start = useMutation({
    mutationFn: () => {
      idempotencyKey.current ??= crypto.randomUUID();
      return plansApi.startJob(planId, idempotencyKey.current, authentication);
    },
    onSuccess: async (receipt) => {
      const jobId = receipt.jobId ?? receipt.operationId;
      registryActions.upsertPlan({ planId, lastJobId: jobId });
      await queryClient.invalidateQueries({ queryKey: queryKeys.jobs });
      toast.success('Transfer started', 'Watching live progress.');
      navigate(`/transfers/${jobId}`);
    },
    onError: (error) => toast.error('Unable to start the transfer', describeError(error)),
  });

  const title = stored.data?.displayName || entry?.name || `Plan ${shortId(planId)}`;
  const note = stored.data ? stored.data.operatorNote : entry?.note;

  if (review.isPending) {
    return (
      <>
        <PageHeader eyebrow="Transfer plan" title={title} />
        <Skeleton className="h-40" />
      </>
    );
  }
  if (review.isError) {
    return (
      <>
        <PageHeader eyebrow="Transfer plan" title={title} />
        <Alert title="Unable to load this plan" tone="danger">
          {describeError(review.error)} The plan may not exist on this API.
        </Alert>
      </>
    );
  }
  const plan = review.data;
  const canSeal = associated && hasPermission('Plans.Seal') && !seal.isPending;
  const canStart = sealed && hasPermission('Transfers.Start') && plan.blockers.length === 0;
  const selectionLabel = plan.selection ? selectionNames[plan.selection.selectionId]?.name || plan.selection.displayName || `Selection ${shortId(plan.selection.selectionId)}` : null;

  return (
    <>
      <PageHeader
        actions={
          <>
            {hasPermission('Plans.Write') ? (
              <Button icon={<Icons.Clipboard size={15} />} onClick={() => navigate(`/plans/${planId}/edit`)}>
                Edit
              </Button>
            ) : null}
            {!sealed ? (
              <Button disabled={!canSeal} icon={<Icons.Lock size={15} />} loading={seal.isPending} onClick={() => seal.mutate()} variant="primary">
                Seal plan
              </Button>
            ) : (
              <>
                {hasPermission('Plans.Seal') ? (
                  <Button icon={<Icons.Refresh size={15} />} loading={seal.isPending} onClick={() => seal.mutate()}>
                    Re-seal
                  </Button>
                ) : null}
                <Button disabled={!canStart} icon={<Icons.Play size={15} />} onClick={() => setConfirming(true)} variant="primary">
                  Start transfer
                </Button>
              </>
            )}
          </>
        }
        description={note ?? undefined}
        eyebrow="Transfer plan"
        title={
          <span className="flex flex-wrap items-center gap-3">
            {title}
            <StatusBadge state={sealed ? 'sealed' : 'draft'} />
            <span className="font-mono text-xs font-normal text-fg-faint">v{plan.version}</span>
          </span>
        }
      />

      {seal.isPending ? (
        <Card className="mb-5">
          <ProgressBar
            detail="Reading both schemas, validating the selection, computing the closure…"
            label="Sealing in progress"
            striped
            value={null}
          />
          <p className="mt-2 text-xs text-fg-faint">This runs against the source and target databases and can take a while on large schemas.</p>
        </Card>
      ) : null}

      <div className="mb-5 grid gap-4 md:grid-cols-3">
        <AssociationCard icon={<Icons.Filter size={16} />} label="Selection" value={selectionLabel} sub={plan.selection ? shortId(plan.selection.selectionId) : 'Not associated'} />
        <ConnectionCard connection={plan.source ?? null} label="Source" />
        <ConnectionCard connection={plan.target ?? null} label="Target" tone="warning" />
      </div>

      {plan.blockers.length > 0 ? (
        <Alert className="mb-5" title={sealed ? 'Blocked' : 'Not sealed yet'} tone={sealed ? 'danger' : 'warning'}>
          <ul className={cx(plan.blockers.length > 1 && 'list-disc pl-4')}>
            {plan.blockers.map((blocker) => (
              <li key={blocker.code}>
                {blocker.message}
                {!associated && blocker.code === 'plan_not_sealed' ? ' Associate a selection, a source and a target, then seal.' : ''}
              </li>
            ))}
          </ul>
        </Alert>
      ) : null}
      {plan.warnings.length > 0 ? (
        <Alert className="mb-5" title="Warnings" tone="warning">
          <ul className="list-disc pl-4">
            {plan.warnings.map((warning) => (
              <li key={warning.code}>{warning.message}</li>
            ))}
          </ul>
        </Alert>
      ) : null}

      {sealed ? (
        <div className="mb-5 grid grid-cols-2 gap-4 lg:grid-cols-5">
          <Stat icon={<Icons.Layers size={16} />} label="Included rows" tone="accent" value={formatNumber(plan.totals.included)} />
          <Stat hint="Rows the transfer will write" icon={<Icons.Upload size={16} />} label="Planned writes" tone="info" value={formatNumber(plan.totals.plannedWrites)} />
          <Stat icon={<Icons.Plus size={16} />} label="Inserts" tone="success" value={formatNumber(plan.totals.inserts)} />
          <Stat icon={<Icons.Refresh size={16} />} label="Updates" value={formatNumber(plan.totals.updates)} />
          <Stat hint={plan.totals.estimatedBytes > 0 ? formatBytes(plan.totals.estimatedBytes) : 'Estimate unavailable'} icon={<Icons.Table size={16} />} label="Tables" value={formatNumber(plan.tables.length)} />
        </div>
      ) : null}

      <Tabs
        className="mb-4 w-fit"
        items={[
          { value: 'tables', label: 'Transfer set', count: plan.tables.length },
          { value: 'graph', label: 'Dependency graph' },
          { value: 'checks', label: 'Checks', count: plan.conflicts.length + plan.cycles.length + plan.startPreconditions.length },
          { value: 'trace', label: 'Why is this row included?' },
        ]}
        onChange={setTab}
        value={tab}
      />
      {tab === 'tables' ? <TransferSet plan={plan} /> : null}
      {tab === 'graph' ? <PlanGraphPanel plan={plan} planId={planId} /> : null}
      {tab === 'checks' ? <ChecksPanel plan={plan} /> : null}
      {tab === 'trace' ? <InclusionPathPanel plan={plan} planId={planId} /> : null}

      <div className="mt-6 text-xs text-fg-faint">
        Canonical hash: <Code>{plan.canonicalHash || '—'}</Code>
      </div>

      <Modal
        description="This writes into the target database. Only the sealed transfer set is written; nothing else in the target is touched."
        footer={
          <>
            <Button disabled={start.isPending} onClick={() => setConfirming(false)}>
              Cancel
            </Button>
            <Button icon={<Icons.Play size={15} />} loading={start.isPending} onClick={() => start.mutate()} variant="primary">
              Start transfer
            </Button>
          </>
        }
        onClose={() => setConfirming(false)}
        open={confirming}
        title="Start this transfer?"
        tone="warning"
      >
        <KeyValue
          items={[
            { label: 'Source', value: plan.source?.displayName ?? '—' },
            { label: 'Target', value: <span className="font-semibold text-warning">{plan.target?.displayName ?? '—'}</span> },
            { label: 'Planned writes', value: `${formatNumber(plan.totals.plannedWrites)} rows in ${plan.tables.length} tables` },
            { label: 'Conflict policy', value: plan.conflicts[0]?.policy ?? 'FailOnConflict' },
          ]}
        />
      </Modal>
    </>
  );
}

function AssociationCard({ icon, label, value, sub }: Readonly<{ icon: React.ReactNode; label: string; value: string | null; sub: string }>) {
  return (
    <Card className="flex items-center gap-3">
      <div className="flex size-10 shrink-0 items-center justify-center rounded-xl bg-accent-soft text-accent">{icon}</div>
      <div className="min-w-0">
        <div className="text-[11px] font-semibold tracking-wide text-fg-muted uppercase">{label}</div>
        <div className={cx('truncate text-sm', value ? 'font-semibold text-fg' : 'text-fg-faint')}>{value ?? 'Not associated'}</div>
        <div className="font-mono text-[11px] text-fg-faint">{sub}</div>
      </div>
    </Card>
  );
}

function ConnectionCard({ label, connection, tone = 'accent' }: Readonly<{ label: string; connection: Readonly<{ connectionId: string; displayName: string; providerId: string; health: string }> | null; tone?: 'accent' | 'warning' }>) {
  return (
    <Card className={cx('flex items-center gap-3', tone === 'warning' && connection && 'border-warning/50')}>
      {connection ? <ProviderMark providerId={connection.providerId} /> : <div className="size-10 shrink-0 rounded-xl bg-surface-3" />}
      <div className="min-w-0 flex-1">
        <div className="text-[11px] font-semibold tracking-wide text-fg-muted uppercase">{label}</div>
        <div className={cx('truncate text-sm', connection ? 'font-semibold text-fg' : 'text-fg-faint')}>{connection?.displayName ?? 'Not associated'}</div>
        {connection ? (
          <Link className="font-mono text-[11px] text-accent hover:underline" to={`/schema/${connection.connectionId}`}>
            browse schema
          </Link>
        ) : null}
      </div>
      {connection ? <StatusBadge state={connection.health} /> : null}
    </Card>
  );
}

const stateTone: Readonly<Record<string, NodeTone>> = {
  Root: 'root',
  RequiredDependency: 'dependency',
  ExplicitDependent: 'dependency',
  CycleMember: 'cycle',
  Blocked: 'blocked',
  Conflict: 'blocked',
  TargetSatisfied: 'muted',
  Excluded: 'muted',
};

function TransferSet({ plan }: Readonly<{ plan: PlanReview }>) {
  const [expanded, setExpanded] = useState<string | null>(null);
  if (plan.tables.length === 0) {
    return (
      <Card padded={false}>
        <EmptyState description="Seal the plan to compute which tables and rows are required." icon={<Icons.Layers size={22} />} title="No transfer set yet" />
      </Card>
    );
  }
  const max = Math.max(1, ...plan.tables.map((table) => table.plannedWrites));
  const ordered = plan.tables.toSorted((a, b) => a.transferOrder - b.transferOrder);
  return (
    <Card padded={false}>
      <div className="flex items-center justify-between border-b border-border px-4 py-3">
        <div className="text-[13px] font-semibold text-fg">Tables in transfer order</div>
        <div className="text-xs text-fg-muted">Parents first, so foreign keys always resolve.</div>
      </div>
      <ul className="divide-y divide-border">
        {ordered.map((table) => {
          const key = `${table.source.schema}.${table.source.name}`;
          const open = expanded === key;
          return (
            <li key={key}>
              <button className="grid w-full grid-cols-[2rem_1fr_auto] items-center gap-3 px-4 py-3 text-left hover:bg-surface-2" onClick={() => setExpanded(open ? null : key)} type="button">
                <span className="tnum text-xs font-semibold text-fg-faint">#{table.transferOrder + 1}</span>
                <span className="min-w-0">
                  <span className="flex flex-wrap items-center gap-2">
                    <span className="font-mono text-[13px] font-semibold text-fg">{key}</span>
                    <TableStateBadge state={table.state} />
                    {table.source.schema !== table.target.schema || table.source.name !== table.target.name ? (
                      <span className="text-xs text-fg-muted">
                        → {table.target.schema}.{table.target.name}
                      </span>
                    ) : null}
                  </span>
                  <ProgressBar className="mt-2 max-w-md" size="xs" tone={table.state === 'Root' ? 'accent' : 'info'} value={table.plannedWrites / max} />
                </span>
                <span className="tnum text-right text-sm">
                  <span className="font-semibold text-fg">{formatNumber(table.plannedWrites)}</span>
                  <span className="text-fg-muted"> writes</span>
                  <span className="block text-xs text-fg-faint">
                    {formatNumber(table.inserts)} ins · {formatNumber(table.updates)} upd
                  </span>
                </span>
              </button>
              {open ? <ColumnMappings table={table} /> : null}
            </li>
          );
        })}
      </ul>
    </Card>
  );
}

function ColumnMappings({ table }: Readonly<{ table: PlanTable }>) {
  return (
    <div className="border-t border-border bg-surface-2 px-4 py-3">
      <div className="mb-2 text-xs font-semibold text-fg-muted">Column mapping ({table.columns.length})</div>
      <div className="flex flex-wrap gap-1.5">
        {table.columns.map((column) => (
          <span className="rounded-md bg-surface px-2 py-0.5 font-mono text-[11.5px] text-fg" key={column.source}>
            {column.source}
            {column.source !== column.target ? <span className="text-fg-faint"> → {column.target}</span> : null}
          </span>
        ))}
      </div>
    </div>
  );
}

function TableStateBadge({ state }: Readonly<{ state: string }>) {
  const tone = state === 'Root' ? 'accent' : state === 'RequiredDependency' || state === 'ExplicitDependent' ? 'info' : state === 'CycleMember' ? 'warning' : state === 'Blocked' || state === 'Conflict' ? 'danger' : 'neutral';
  return (
    <Badge className="!h-5 !px-1.5 !text-[10px]" tone={tone}>
      {state.replace(/([a-z])([A-Z])/g, '$1 $2')}
    </Badge>
  );
}

function PlanGraphPanel({ plan, planId }: Readonly<{ plan: PlanReview; planId: string }>) {
  const { authentication } = useAuth();
  const graph = useQuery({ queryKey: queryKeys.planGraph(planId), queryFn: ({ signal }) => plansApi.graph(planId, authentication, signal), retry: false, enabled: isSealed(plan) });
  const [selectedKey, setSelectedKey] = useState<string | null>(null);
  const planned = useMemo(() => new Map(plan.tables.map((table) => [`${table.source.schema}.${table.source.name}`, table.state])), [plan.tables]);
  const projection = useMemo<SchemaGraphProjection | null>(() => {
    if (!graph.data) return null;
    const byId = new Map(graph.data.tables.map((table) => [table.id, { schema: table.schema, name: table.name }]));
    return {
      tables: [...byId.values()],
      edges: graph.data.relationships.flatMap((relationship) => {
        const child = byId.get(relationship.childTableId);
        const parent = byId.get(relationship.parentTableId);
        return child && parent ? [{ child, parent, foreignKeyName: relationship.name }] : [];
      }),
    };
  }, [graph.data]);
  const cycleMembers = useMemo(() => new Set(graph.data?.tables.filter((table) => table.state === 'cycle-member').map((table) => table.id) ?? []), [graph.data]);

  function toneFor(table: SchemaTableAddress): NodeTone {
    const key = tableKey(table);
    const state = planned.get(key);
    if (state) return stateTone[state] ?? 'dependency';
    if (cycleMembers.has(key)) return 'cycle';
    return 'muted';
  }

  if (!isSealed(plan)) {
    return (
      <Card padded={false}>
        <EmptyState description="The dependency graph is built from the sealed source schema snapshot." icon={<Icons.Schema size={22} />} title="Seal the plan to see its graph" />
      </Card>
    );
  }
  if (graph.isPending) return <Skeleton className="h-[520px]" />;
  if (graph.isError) return <Alert tone="danger">{describeError(graph.error)}</Alert>;
  return (
    <div className="grid gap-3">
      <div className="flex flex-wrap items-center gap-3 text-xs text-fg-muted">
        <Legend label="Root" tone="root" />
        <Legend label="Required dependency" tone="dependency" />
        <Legend label="Cycle member" tone="cycle" />
        <Legend label="Not in transfer set" tone="muted" />
        {selectedKey ? <span className="ml-auto font-mono text-fg">{selectedKey}</span> : null}
      </div>
      <SchemaGraph graph={projection!} height={560} onSelect={(table) => setSelectedKey(tableKey(table))} selectedKey={selectedKey} toneFor={toneFor} />
    </div>
  );
}

function Legend({ label, tone }: Readonly<{ label: string; tone: NodeTone }>) {
  const color = tone === 'root' ? 'bg-accent' : tone === 'dependency' ? 'bg-info' : tone === 'cycle' ? 'bg-warning' : 'bg-fg-faint';
  return (
    <span className="inline-flex items-center gap-1.5">
      <span className={cx('size-2.5 rounded-sm', color)} /> {label}
    </span>
  );
}

function ChecksPanel({ plan }: Readonly<{ plan: PlanReview }>) {
  return (
    <div className="grid gap-4 lg:grid-cols-3">
      <Card>
        <CardHeader icon={<Icons.Shield size={16} />} title="Start preconditions" />
        {plan.startPreconditions.length === 0 ? (
          <p className="text-sm text-fg-muted">The API reports no start preconditions for this plan. Health of both connections is re-validated when the transfer starts.</p>
        ) : (
          <ul className="grid gap-2">
            {plan.startPreconditions.map((precondition) => (
              <li className="flex items-start gap-2 text-sm" key={precondition.code}>
                <span className={cx('mt-0.5 flex size-5 shrink-0 items-center justify-center rounded-full', precondition.satisfied ? 'bg-success text-white' : 'bg-danger text-white')}>
                  {precondition.satisfied ? <Icons.Check size={12} strokeWidth={3} /> : <Icons.X size={12} strokeWidth={3} />}
                </span>
                <span>
                  <span className="font-medium text-fg">{precondition.code}</span>
                  <span className="block text-fg-muted">{precondition.message}</span>
                </span>
              </li>
            ))}
          </ul>
        )}
      </Card>
      <Card>
        <CardHeader icon={<Icons.Alert size={16} />} title="Conflict policies" />
        {plan.conflicts.length === 0 ? (
          <p className="text-sm text-fg-muted">None recorded.</p>
        ) : (
          <ul className="grid gap-2">
            {plan.conflicts.map((conflict) => (
              <li className="rounded-lg bg-surface-2 px-3 py-2 text-sm" key={conflict.table}>
                <div className="font-mono text-[12.5px] font-semibold text-fg">{conflict.table}</div>
                <div className="text-fg-muted">
                  <Badge tone="warning">{conflict.policy}</Badge> {conflict.message || (conflict.policy === 'FailOnConflict' ? 'The transfer stops if a root row already exists in the target.' : '')}
                </div>
              </li>
            ))}
          </ul>
        )}
      </Card>
      <Card>
        <CardHeader icon={<Icons.Refresh size={16} />} title="Referential cycles" />
        {plan.cycles.length === 0 ? (
          <p className="text-sm text-fg-muted">No cycles in the transfer set.</p>
        ) : (
          <ul className="grid gap-2">
            {plan.cycles.map((cycle, index) => (
              <li className="rounded-lg bg-surface-2 px-3 py-2 text-sm" key={index}>
                <div className="font-mono text-[12px] text-fg">{cycle.tables.join(' ⇄ ')}</div>
                <div className="mt-1 text-fg-muted">
                  <Badge tone="info">{cycle.strategy}</Badge> {cycle.message}
                </div>
              </li>
            ))}
          </ul>
        )}
      </Card>
    </div>
  );
}

function InclusionPathPanel({ plan, planId }: Readonly<{ plan: PlanReview; planId: string }>) {
  const { authentication } = useAuth();
  const [table, setTable] = useState(plan.tables[0] ? `${plan.tables[0].source.schema}.${plan.tables[0].source.name}` : '');
  const [stableKey, setStableKey] = useState('');
  const lookup = useMutation({ mutationFn: () => plansApi.inclusionPath(planId, table, stableKey, authentication) });

  function submit(event: FormEvent) {
    event.preventDefault();
    if (table && stableKey) lookup.mutate();
  }

  return (
    <Card>
      <CardHeader description="Trace the chain of foreign keys that pulled a specific row into the transfer set." icon={<Icons.Search size={16} />} title="Inclusion path" />
      <form className="grid gap-3 md:grid-cols-[1fr_1fr_auto] md:items-end" onSubmit={submit}>
        <Field label="Table">
          <TextInput className="font-mono" list="plan-tables" onChange={(event) => setTable(event.target.value)} placeholder="schema.table" value={table} />
        </Field>
        <datalist id="plan-tables">
          {plan.tables.map((item) => (
            <option key={`${item.source.schema}.${item.source.name}`} value={`${item.source.schema}.${item.source.name}`} />
          ))}
        </datalist>
        <Field label="Stable key value">
          <TextInput className="font-mono" onChange={(event) => setStableKey(event.target.value)} placeholder="e.g. 42" value={stableKey} />
        </Field>
        <Button disabled={!table || !stableKey} loading={lookup.isPending} type="submit" variant="primary">
          Trace
        </Button>
      </form>
      {lookup.isError ? (
        <Alert className="mt-4" tone={isNotWired(lookup.error) ? 'info' : 'danger'}>
          {isNotWired(lookup.error) ? 'Inclusion-path lookup needs persisted closure provenance, which this API build does not record yet.' : describeError(lookup.error)}
        </Alert>
      ) : null}
      {lookup.data ? (
        <div className="mt-4">
          <div className="mb-2 text-sm text-fg-muted">
            Root selection: <Code>{lookup.data.rootSelection}</Code>
          </div>
          <ol className="grid gap-2">
            {lookup.data.steps.map((step, index) => (
              <li className="flex items-center gap-3 rounded-lg bg-surface-2 px-3 py-2 text-sm" key={index}>
                <span className="tnum text-xs font-bold text-fg-faint">{index + 1}</span>
                <span className="font-mono text-[12.5px] text-fg">
                  {step.from} <span className="text-fg-faint">→</span> {step.to}
                </span>
                <Badge>{step.relationship}</Badge>
                <span className="ml-auto text-fg-muted">{step.reason}</span>
              </li>
            ))}
          </ol>
        </div>
      ) : null}
    </Card>
  );
}
