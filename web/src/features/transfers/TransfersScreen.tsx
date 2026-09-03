import { useMemo, useState } from 'react';
import { formatBytes, formatNumber, formatRelative } from '../../api/format';
import { isActive, isTerminal, normalizeJobState } from '../../api/jobs';
import { describeError } from '../../api/problem';
import { Link, navigate } from '../../app/router';
import { usePlanRegistry } from '../../stores/registryStore';
import { Alert, Badge, Button, Card, EmptyState, PageHeader, ProgressBar, Skeleton, StatusBadge, Tabs, shortId } from '../../ui';
import { Icons } from '../../ui/icons';
import { useJobs } from '../shared/queries';

type Filter = 'all' | 'active' | 'finished' | 'failed';

export function TransfersScreen() {
  const jobs = useJobs();
  const plans = usePlanRegistry();
  const [filter, setFilter] = useState<Filter>('all');

  const counts = useMemo(() => {
    const list = jobs.data ?? [];
    return {
      all: list.length,
      active: list.filter((job) => isActive(job.state)).length,
      finished: list.filter((job) => normalizeJobState(job.state) === 'succeeded' || normalizeJobState(job.state) === 'cancelled').length,
      failed: list.filter((job) => ['failed', 'verificationfailed'].includes(normalizeJobState(job.state))).length,
    };
  }, [jobs.data]);

  const visible = (jobs.data ?? []).filter((job) => {
    const state = normalizeJobState(job.state);
    if (filter === 'active') return isActive(job.state);
    if (filter === 'finished') return state === 'succeeded' || state === 'cancelled';
    if (filter === 'failed') return state === 'failed' || state === 'verificationfailed';
    return true;
  });

  return (
    <>
      <PageHeader
        actions={
          <Button icon={<Icons.Refresh size={15} />} loading={jobs.isFetching && !jobs.isPending} onClick={() => void jobs.refetch()}>
            Refresh
          </Button>
        }
        description={counts.active > 0 ? `${counts.active} transfer${counts.active === 1 ? '' : 's'} running. This list refreshes every two seconds while work is in progress.` : 'Every transfer job the API knows about, newest first.'}
        title="Transfers"
      />
      <Tabs
        className="mb-4 w-fit"
        items={[
          { value: 'all', label: 'All', count: counts.all },
          { value: 'active', label: 'Active', count: counts.active },
          { value: 'finished', label: 'Finished', count: counts.finished },
          { value: 'failed', label: 'Failed', count: counts.failed },
        ]}
        onChange={setFilter}
        value={filter}
      />
      {jobs.isPending ? (
        <Skeleton className="h-64" />
      ) : jobs.isError ? (
        <Alert tone="danger">{describeError(jobs.error)}</Alert>
      ) : visible.length === 0 ? (
        <Card padded={false}>
          <EmptyState
            action={
              filter === 'all' ? (
                <Button icon={<Icons.Clipboard size={15} />} onClick={() => navigate('/plans')} variant="primary">
                  Go to plans
                </Button>
              ) : undefined
            }
            description={filter === 'all' ? 'Seal a plan and start it to create a transfer job.' : 'Nothing matches this filter.'}
            icon={<Icons.Rocket size={22} />}
            title={filter === 'all' ? 'No transfers yet' : 'Nothing here'}
          />
        </Card>
      ) : (
        <div className="grid gap-3">
          {visible.map((job) => {
            const plan = plans[job.planId];
            const state = normalizeJobState(job.state);
            const active = isActive(job.state);
            const fraction = plan?.plannedWrites && plan.plannedWrites > 0 ? Math.min(1, job.rowsTransferred / plan.plannedWrites) : null;
            const tone = state === 'succeeded' ? 'success' : state === 'failed' || state === 'verificationfailed' ? 'danger' : state === 'cancelled' ? 'neutral' : state === 'paused' || state === 'pausing' ? 'warning' : 'accent';
            return (
              <Card className="grid gap-3 md:grid-cols-[1fr_260px_auto] md:items-center" interactive key={job.jobId}>
                <div className="min-w-0">
                  <Link className="flex items-center gap-2 text-[15px] font-semibold text-fg hover:text-accent" to={`/transfers/${job.jobId}`}>
                    {plan?.name || `Plan ${shortId(job.planId)}`}
                    <Icons.ArrowRight className="text-fg-faint" size={14} />
                  </Link>
                  <div className="mt-0.5 flex flex-wrap items-center gap-x-3 gap-y-1 text-xs text-fg-muted">
                    <span className="font-mono">job {shortId(job.jobId)}</span>
                    <span>started {formatRelative(job.createdUtc)}</span>
                    <span>updated {formatRelative(job.updatedUtc)}</span>
                  </div>
                </div>
                <ProgressBar
                  detail={`${formatNumber(job.rowsTransferred)}${plan?.plannedWrites ? ` / ${formatNumber(plan.plannedWrites)}` : ''} rows · ${formatBytes(job.bytesTransferred)}`}
                  showPercent={fraction !== null}
                  size="sm"
                  striped={active}
                  tone={tone}
                  value={active ? fraction : isTerminal(job.state) ? (state === 'succeeded' ? 1 : (fraction ?? 1)) : (fraction ?? 0)}
                />
                <div className="flex items-center gap-2 md:justify-end">
                  <StatusBadge state={job.state} />
                  {plan ? (
                    <Badge tone="neutral">
                      <Link className="hover:underline" to={`/plans/${job.planId}`}>
                        plan
                      </Link>
                    </Badge>
                  ) : null}
                </div>
              </Card>
            );
          })}
        </div>
      )}
    </>
  );
}
