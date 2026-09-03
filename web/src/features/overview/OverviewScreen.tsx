import { useQueries } from '@tanstack/react-query';
import { useMemo } from 'react';
import { connectionsApi } from '../../api/connections';
import { queryKeys } from '../../api/keys';
import { formatBytes, formatNumber, formatRelative } from '../../api/format';
import { isActive, isTerminal, normalizeJobState } from '../../api/jobs';
import { useAuth } from '../../auth/AuthContext';
import { Link, navigate } from '../../app/router';
import { usePlanRegistry, useSelectionRegistry } from '../../stores/registryStore';
import { useSourceConnectionId, useTargetConnectionId } from '../../stores/sessionStore';
import { Badge, Button, Card, CardHeader, EmptyState, PageHeader, ProgressBar, Stat, StatusBadge, Stepper, shortId, type StepStatus } from '../../ui';
import { Icons } from '../../ui/icons';
import { useConnections, useJobs, useSelections } from '../shared/queries';

export function OverviewScreen() {
  const { principal, authentication } = useAuth();
  const connections = useConnections();
  const selections = useSelections();
  const jobs = useJobs();
  const plans = usePlanRegistry();
  const selectionNames = useSelectionRegistry();
  const sourceId = useSourceConnectionId();
  const targetId = useTargetConnectionId();

  const snapshotQueries = useQueries({
    queries: (connections.data ?? []).map((connection) => ({
      queryKey: queryKeys.snapshots(connection.connectionId),
      queryFn: ({ signal }: { signal: AbortSignal }) => connectionsApi.snapshots(connection.connectionId, authentication, signal),
      staleTime: 30_000,
    })),
  });
  const snapshotCount = snapshotQueries.reduce((total, query) => total + (query.data?.length ?? 0), 0);
  const healthy = connections.data?.filter((connection) => connection.health === 'Healthy').length ?? 0;
  const planList = useMemo(() => Object.values(plans).toSorted((a, b) => b.updatedAt.localeCompare(a.updatedAt)), [plans]);
  const sealedCount = planList.filter((plan) => plan.sealed).length;
  const activeJobs = jobs.data?.filter((job) => isActive(job.state)) ?? [];
  const succeeded = jobs.data?.filter((job) => normalizeJobState(job.state) === 'succeeded').length ?? 0;
  const failed = jobs.data?.filter((job) => ['failed', 'verificationfailed'].includes(normalizeJobState(job.state))).length ?? 0;

  const hasConnections = (connections.data?.length ?? 0) > 0;
  const hasSelections = (selections.data?.length ?? 0) > 0;
  const hasPlans = planList.length > 0;
  const hasSealed = sealedCount > 0;
  const hasJobs = (jobs.data?.length ?? 0) > 0;

  const hasSnapshots = snapshotCount > 0;
  const completion = [hasConnections, hasSnapshots, hasSelections, hasPlans, hasSealed, hasJobs];
  const firstOpen = completion.indexOf(false);
  const statusAt = (index: number): StepStatus => (completion[index] ? 'done' : index === firstOpen ? 'active' : 'todo');
  const steps = [
    { key: 'connect', label: 'Connect', description: hasConnections ? `${healthy}/${connections.data!.length} healthy` : 'Add source and target', status: statusAt(0), onClick: () => navigate('/connections') },
    { key: 'scan', label: 'Scan schema', description: hasSnapshots ? `${snapshotCount} snapshot${snapshotCount === 1 ? '' : 's'}` : 'Capture a snapshot', status: statusAt(1), onClick: () => navigate('/connections') },
    { key: 'select', label: 'Select rows', description: hasSelections ? `${selections.data!.length} saved` : 'Root rows via SQL', status: statusAt(2), onClick: () => navigate('/selections') },
    { key: 'plan', label: 'Plan', description: hasPlans ? `${planList.length} plan${planList.length === 1 ? '' : 's'}` : 'Pair selection with target', status: statusAt(3), onClick: () => navigate('/plans') },
    { key: 'seal', label: 'Seal', description: hasSealed ? `${sealedCount} sealed` : 'Compute the closure', status: statusAt(4), onClick: () => navigate('/plans') },
    { key: 'transfer', label: 'Transfer', description: hasJobs ? `${activeJobs.length} active` : 'Run and verify', status: statusAt(5), onClick: () => navigate('/transfers') },
  ] as const;

  const greeting = new Date().getHours() < 12 ? 'Good morning' : new Date().getHours() < 18 ? 'Good afternoon' : 'Good evening';

  return (
    <>
      <PageHeader
        actions={
          <>
            <Button icon={<Icons.Filter size={15} />} onClick={() => navigate('/selections/new')}>
              New selection
            </Button>
            <Button icon={<Icons.Rocket size={15} />} onClick={() => navigate('/plans/new')} variant="primary">
              New transfer plan
            </Button>
          </>
        }
        description="Targeted relational data transfer: pick exact rows, seal a reviewable plan, and move only what is required."
        title={`${greeting}, ${principal.subjectId}`}
      />

      <Card className="mb-6">
        <CardHeader description="Follow the pipeline left to right. Each step unlocks the next." title="Transfer pipeline" />
        <Stepper steps={steps} />
      </Card>

      <div className="mb-6 grid grid-cols-2 gap-4 lg:grid-cols-5">
        <Stat hint={hasConnections ? `${healthy} healthy` : 'None registered'} icon={<Icons.Plug size={16} />} label="Connections" tone={healthy > 0 ? 'success' : 'neutral'} value={formatNumber(connections.data?.length ?? 0)} />
        <Stat icon={<Icons.Filter size={16} />} label="Selections" tone="accent" value={formatNumber(selections.data?.length ?? 0)} />
        <Stat hint={`${sealedCount} sealed`} icon={<Icons.Clipboard size={16} />} label="Plans" tone="info" value={formatNumber(planList.length)} />
        <Stat hint={`${succeeded} succeeded`} icon={<Icons.Rocket size={16} />} label="Transfers" tone={activeJobs.length > 0 ? 'info' : 'neutral'} value={formatNumber(jobs.data?.length ?? 0)} />
        <Stat icon={<Icons.Alert size={16} />} label="Failed" tone={failed > 0 ? 'danger' : 'neutral'} value={formatNumber(failed)} />
      </div>

      <div className="grid gap-5 xl:grid-cols-[1.4fr_1fr]">
        <Card>
          <CardHeader
            actions={
              <Link className="text-[13px] font-medium text-accent hover:underline" to="/transfers">
                All transfers
              </Link>
            }
            description="Live progress of running and recent transfer jobs."
            icon={<Icons.Activity size={16} />}
            title="Transfers"
          />
          {jobs.isPending ? (
            <p className="text-sm text-fg-muted">Loading…</p>
          ) : (jobs.data?.length ?? 0) === 0 ? (
            <EmptyState className="py-8" description="Seal a plan and start it to see progress here." icon={<Icons.Rocket size={20} />} title="No transfers yet" />
          ) : (
            <ul className="divide-y divide-border">
              {jobs.data!.slice(0, 6).map((job) => {
                const plan = plans[job.planId];
                const fraction = plan?.plannedWrites && plan.plannedWrites > 0 ? Math.min(1, job.rowsTransferred / plan.plannedWrites) : null;
                const active = isActive(job.state);
                const state = normalizeJobState(job.state);
                return (
                  <li className="py-3 first:pt-0 last:pb-0" key={job.jobId}>
                    <div className="mb-1.5 flex items-center justify-between gap-3">
                      <Link className="truncate text-sm font-semibold text-fg hover:text-accent" to={`/transfers/${job.jobId}`}>
                        {plan?.name || `Plan ${shortId(job.planId)}`}
                        <span className="ml-2 font-mono text-[11px] font-normal text-fg-faint">{shortId(job.jobId)}</span>
                      </Link>
                      <StatusBadge state={job.state} />
                    </div>
                    <ProgressBar
                      detail={`${formatNumber(job.rowsTransferred)} rows · ${formatBytes(job.bytesTransferred)} · ${formatRelative(job.updatedUtc)}`}
                      showPercent={fraction !== null}
                      size="sm"
                      striped={active}
                      tone={state === 'succeeded' ? 'success' : state === 'failed' || state === 'verificationfailed' ? 'danger' : state === 'cancelled' ? 'neutral' : state === 'paused' ? 'warning' : 'accent'}
                      value={active ? fraction : isTerminal(job.state) ? (state === 'succeeded' ? 1 : (fraction ?? 1)) : (fraction ?? 0)}
                    />
                  </li>
                );
              })}
            </ul>
          )}
        </Card>

        <div className="grid gap-5">
          <Card>
            <CardHeader description="Connections used as defaults for new plans." icon={<Icons.Database size={16} />} title="Working pair" />
            <div className="grid gap-2">
              <PairRow connectionName={connections.data?.find((item) => item.connectionId === sourceId)?.displayName ?? null} label="Source" tone="accent" />
              <div className="flex justify-center text-fg-faint">
                <Icons.ChevronDown size={16} />
              </div>
              <PairRow connectionName={connections.data?.find((item) => item.connectionId === targetId)?.displayName ?? null} label="Target" tone="warning" />
            </div>
            {!sourceId || !targetId ? (
              <Button block className="mt-4" onClick={() => navigate('/connections')} size="sm">
                Choose connections
              </Button>
            ) : null}
          </Card>

          <Card>
            <CardHeader
              actions={
                <Link className="text-[13px] font-medium text-accent hover:underline" to="/plans">
                  All plans
                </Link>
              }
              icon={<Icons.Clipboard size={16} />}
              title="Recent plans"
            />
            {planList.length === 0 ? (
              <p className="text-sm text-fg-muted">No plans on this device yet.</p>
            ) : (
              <ul className="divide-y divide-border">
                {planList.slice(0, 5).map((plan) => (
                  <li className="flex items-center justify-between gap-3 py-2.5 first:pt-0 last:pb-0" key={plan.planId}>
                    <div className="min-w-0">
                      <Link className="block truncate text-sm font-semibold text-fg hover:text-accent" to={`/plans/${plan.planId}`}>
                        {plan.name || `Plan ${shortId(plan.planId)}`}
                      </Link>
                      <div className="text-xs text-fg-faint">
                        {selectionNames[plan.selectionId ?? '']?.name || (plan.selectionId ? `Selection ${shortId(plan.selectionId)}` : 'No selection')} · {formatRelative(plan.updatedAt)}
                      </div>
                    </div>
                    <Badge tone={plan.sealed ? 'success' : 'warning'}>{plan.sealed ? 'Sealed' : 'Draft'}</Badge>
                  </li>
                ))}
              </ul>
            )}
          </Card>
        </div>
      </div>
    </>
  );
}

function PairRow({ label, connectionName, tone }: Readonly<{ label: string; connectionName: string | null; tone: 'accent' | 'warning' }>) {
  return (
    <div className="flex items-center justify-between rounded-xl border border-border bg-surface-2 px-3 py-2.5">
      <Badge tone={tone}>{label}</Badge>
      <span className={connectionName ? 'text-sm font-medium text-fg' : 'text-sm text-fg-faint'}>{connectionName ?? 'Not chosen'}</span>
    </div>
  );
}
