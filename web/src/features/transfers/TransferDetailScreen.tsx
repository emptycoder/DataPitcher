import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useEffect, useMemo, useState } from 'react';
import { formatBytes, formatDuration, formatNumber, formatRate, formatRelative } from '../../api/format';
import { isActive, isTerminal, jobsApi, legalCommands, normalizeJobState, streamJobEvents, type JobCommand, type JobState, type JobStreamEvent, type JobStreamStatus } from '../../api/jobs';
import { queryKeys } from '../../api/keys';
import { isSealed } from '../../api/plans';
import { describeError } from '../../api/problem';
import { useAuth } from '../../auth/AuthContext';
import { usePermissions } from '../../auth/permissions';
import { Link } from '../../app/router';
import { usePlanEntry } from '../../stores/registryStore';
import { Alert, Badge, Button, Card, CardHeader, KeyValue, Modal, PageHeader, ProgressBar, Skeleton, Stat, StatusBadge, Stepper, cx, humanizeState, shortId, type StepStatus } from '../../ui';
import { Icons } from '../../ui/icons';
import { useToast } from '../../ui/toast';
import { useJob, useJobs, usePlanReview } from '../shared/queries';

type Live = Readonly<{ state: JobState | 'unknown'; rows: number; bytes: number; at: number; source: 'stream' | 'poll' }>;

const pipeline: readonly JobState[] = ['queued', 'preparing', 'running', 'verifying', 'succeeded'];

function stepStatuses(state: JobState | 'unknown'): readonly StepStatus[] {
  const index = pipeline.indexOf(state as JobState);
  if (state === 'failed' || state === 'verificationfailed' || state === 'cancelled' || state === 'cancelling') {
    return pipeline.map((_, i) => (i < 2 ? 'done' : i === 2 ? 'error' : 'todo'));
  }
  if (state === 'paused' || state === 'pausing') return pipeline.map((_, i) => (i < 2 ? 'done' : i === 2 ? 'active' : 'todo'));
  if (index < 0) return pipeline.map(() => 'todo');
  return pipeline.map((_, i) => (i < index ? 'done' : i === index ? (state === 'succeeded' ? 'done' : 'active') : 'todo'));
}

export function TransferDetailScreen({ jobId }: Readonly<{ jobId: string }>) {
  const { authentication } = useAuth();
  const { hasPermission } = usePermissions();
  const queryClient = useQueryClient();
  const toast = useToast();
  const job = useJob(jobId);
  const jobs = useJobs({ live: false });
  const [live, setLive] = useState<Live | null>(null);
  const [events, setEvents] = useState<readonly JobStreamEvent[]>([]);
  const [streamState, setStreamStatus] = useState<JobStreamStatus>('connecting');
  const [cancelling, setCancelling] = useState(false);
  const [now, setNow] = useState(() => Date.now());
  const planId = job.data?.planId ?? null;
  const plan = usePlanEntry(planId);
  const review = usePlanReview(planId, { enabled: planId !== null });

  const state = live?.state ?? (job.data ? normalizeJobState(job.data.state) : 'unknown');
  const rows = live?.rows ?? job.data?.rowsTransferred ?? 0;
  const bytes = live?.bytes ?? job.data?.bytesTransferred ?? 0;
  const terminal = job.data ? isTerminal(state) : false;
  const streamStatus: JobStreamStatus = terminal ? 'ended' : streamState;
  const active = isActive(state);
  const plannedWrites = isSealed(review.data) ? review.data!.totals.plannedWrites : (plan?.plannedWrites ?? null);
  const fraction = plannedWrites && plannedWrites > 0 ? Math.min(1, rows / plannedWrites) : null;

  // Live event stream.
  useEffect(() => {
    if (!job.data || isTerminal(job.data.state)) return;
    const stop = streamJobEvents(jobId, authentication, {
      onEvent: (event) => {
        setLive({ state: event.state, rows: event.rowsTransferred, bytes: event.bytesTransferred, at: event.receivedAt, source: 'stream' });
        setEvents((current) => [...current.slice(-199), event]);
        if (isTerminal(event.state)) {
          void queryClient.invalidateQueries({ queryKey: queryKeys.job(jobId) });
          void queryClient.invalidateQueries({ queryKey: queryKeys.jobs });
        }
      },
      onStatus: setStreamStatus,
    });
    return stop;
    // Reconnect only when the job identity changes or it first loads.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [jobId, authentication, job.data?.jobId]);

  // Fall back to polling while the stream is not live (e.g. between events after a reconnect).
  useEffect(() => {
    if (terminal || streamStatus === 'live') return;
    const handle = window.setInterval(() => void queryClient.invalidateQueries({ queryKey: queryKeys.job(jobId) }), 2500);
    return () => window.clearInterval(handle);
  }, [terminal, streamStatus, queryClient, jobId]);

  // Clock for elapsed time.
  useEffect(() => {
    if (terminal) return;
    const handle = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(handle);
  }, [terminal]);

  const throughput = useMemo(() => {
    const window = events.filter((event) => event.receivedAt >= (events.at(-1)?.receivedAt ?? 0) - 10_000);
    const first = window[0];
    const last = window.at(-1);
    if (!first || !last || first === last) return { rows: null, bytes: null };
    const seconds = (last.receivedAt - first.receivedAt) / 1000;
    if (seconds <= 0) return { rows: null, bytes: null };
    return { rows: (last.rowsTransferred - first.rowsTransferred) / seconds, bytes: (last.bytesTransferred - first.bytesTransferred) / seconds };
  }, [events]);

  const summary = jobs.data?.find((item) => item.jobId === jobId) ?? null;
  const startedAt = summary ? Date.parse(summary.createdUtc) : null;
  const endedAt = terminal && summary ? Date.parse(summary.updatedUtc) : null;
  const elapsed = startedAt ? (endedAt ?? now) - startedAt : null;
  const eta = active && throughput.rows && plannedWrites ? Math.max(0, (plannedWrites - rows) / throughput.rows) * 1000 : null;

  const command = useMutation({
    mutationFn: (kind: JobCommand) => jobsApi.command(jobId, kind, authentication),
    onSuccess: async (_, kind) => {
      toast.success(`${kind} requested`, 'The worker applies it at the next checkpoint.');
      await queryClient.invalidateQueries({ queryKey: queryKeys.job(jobId) });
      await queryClient.invalidateQueries({ queryKey: queryKeys.jobs });
    },
    onError: (error) => toast.error('Command rejected', describeError(error)),
  });

  const title = plan?.name || (planId ? `Plan ${shortId(planId)}` : 'Transfer');
  const commands = hasPermission('Transfers.Write') ? legalCommands(state) : [];

  if (job.isPending) {
    return (
      <>
        <PageHeader eyebrow="Transfer" title="Loading…" />
        <Skeleton className="h-64" />
      </>
    );
  }
  if (job.isError) {
    return (
      <>
        <PageHeader eyebrow="Transfer" title={`Job ${shortId(jobId)}`} />
        <Alert title="Unable to load this transfer" tone="danger">
          {describeError(job.error)}
        </Alert>
      </>
    );
  }

  const tone = state === 'succeeded' ? 'success' : state === 'failed' || state === 'verificationfailed' ? 'danger' : state === 'cancelled' ? 'neutral' : state === 'paused' || state === 'pausing' ? 'warning' : 'accent';
  const barValue = active ? fraction : terminal ? (state === 'succeeded' ? 1 : (fraction ?? 1)) : (fraction ?? 0);

  return (
    <>
      <PageHeader
        actions={
          <>
            {commands.includes('Pause') ? (
              <Button icon={<Icons.Pause size={15} />} loading={command.isPending && command.variables === 'Pause'} onClick={() => command.mutate('Pause')}>
                Pause
              </Button>
            ) : null}
            {commands.includes('Resume') ? (
              <Button icon={<Icons.Play size={15} />} loading={command.isPending && command.variables === 'Resume'} onClick={() => command.mutate('Resume')} variant="primary">
                Resume
              </Button>
            ) : null}
            {commands.includes('Cancel') ? (
              <Button icon={<Icons.Stop size={15} />} onClick={() => setCancelling(true)} variant="danger">
                Cancel
              </Button>
            ) : null}
          </>
        }
        eyebrow="Transfer"
        title={
          <span className="flex flex-wrap items-center gap-3">
            {title}
            <StatusBadge state={state} />
          </span>
        }
      />

      <Card className="mb-5">
        <div className="mb-5 flex flex-wrap items-center justify-between gap-3">
          <div className="text-sm text-fg-muted">
            {plan?.name ? (
              <>
                Plan{' '}
                <Link className="font-medium text-accent hover:underline" to={`/plans/${planId}`}>
                  {plan.name}
                </Link>
              </>
            ) : planId ? (
              <Link className="font-medium text-accent hover:underline" to={`/plans/${planId}`}>
                View plan
              </Link>
            ) : null}
            <span className="ml-3 font-mono text-xs text-fg-faint">job {jobId}</span>
          </div>
          <StreamIndicator status={streamStatus} terminal={terminal} />
        </div>
        <ProgressBar
          detail={plannedWrites ? `${formatNumber(rows)} of ${formatNumber(plannedWrites)} rows` : `${formatNumber(rows)} rows`}
          label={humanizeState(state)}
          showPercent={fraction !== null}
          size="lg"
          striped={active}
          tone={tone}
          value={barValue}
        />
        {fraction === null && active ? <p className="mt-2 text-xs text-fg-faint">Planned row count unknown for this job, so progress is shown as activity.</p> : null}
        <div className="mt-5">
          <Stepper steps={pipeline.map((step, index) => ({ key: step, label: humanizeState(step), status: stepStatuses(state)[index]! }))} />
        </div>
        {state === 'paused' ? <Alert className="mt-4" tone="warning">Paused at the last committed checkpoint. Resume to continue exactly where it stopped.</Alert> : null}
        {state === 'failed' ? <Alert className="mt-4" tone="danger">The transfer failed. Committed batches remain in the target; nothing partial was left uncommitted.</Alert> : null}
        {state === 'verificationfailed' ? <Alert className="mt-4" tone="danger">Rows were written, but post-transfer verification did not pass.</Alert> : null}
        {state === 'cancelled' ? <Alert className="mt-4" tone="neutral">Cancelled by request. Committed batches remain in the target.</Alert> : null}
        {state === 'succeeded' ? <Alert className="mt-4" tone="success">Transfer complete and verified.</Alert> : null}
      </Card>

      <div className="mb-5 grid grid-cols-2 gap-4 lg:grid-cols-5">
        <Stat icon={<Icons.Layers size={16} />} label="Rows written" tone="accent" value={formatNumber(rows)} />
        <Stat icon={<Icons.Upload size={16} />} label="Bytes written" value={formatBytes(bytes)} />
        <Stat hint={throughput.bytes !== null ? formatRate(throughput.bytes, 'B') : undefined} icon={<Icons.Zap size={16} />} label="Throughput" tone="info" value={formatRate(throughput.rows, 'rows')} />
        <Stat hint={startedAt ? `started ${formatRelative(summary!.createdUtc)}` : undefined} icon={<Icons.Clock size={16} />} label="Elapsed" value={formatDuration(elapsed)} />
        <Stat icon={<Icons.Target size={16} />} label="Remaining" tone={eta !== null ? 'info' : 'neutral'} value={eta !== null ? `~${formatDuration(eta)}` : terminal ? 'Done' : '—'} />
      </div>

      <div className="grid gap-5 lg:grid-cols-[1fr_360px]">
        <Card padded={false}>
          <div className="flex items-center justify-between border-b border-border px-4 py-3">
            <div className="text-[13px] font-semibold text-fg">Event log</div>
            <div className="text-xs text-fg-muted">{events.length} events this session</div>
          </div>
          {events.length === 0 ? (
            <p className="px-4 py-6 text-sm text-fg-muted">{terminal ? 'This job finished before this page opened, so no live events were received.' : 'Waiting for the first event…'}</p>
          ) : (
            <ul className="scrollbar-thin max-h-[420px] divide-y divide-border overflow-y-auto">
              {events
                .slice()
                .reverse()
                .map((event) => (
                  <li className="grid grid-cols-[auto_auto_1fr_auto] items-center gap-3 px-4 py-2 text-[13px]" key={event.id}>
                    <span className="tnum font-mono text-[11px] text-fg-faint">#{event.id}</span>
                    <Badge className="!h-5 !px-1.5 !text-[10px]" tone={event.type === 'state' ? 'accent' : 'neutral'}>
                      {event.type}
                    </Badge>
                    <span className="text-fg">
                      {humanizeState(event.state)} · <span className="tnum">{formatNumber(event.rowsTransferred)}</span> rows · {formatBytes(event.bytesTransferred)}
                    </span>
                    <span className="tnum text-xs text-fg-faint">{new Date(event.receivedAt).toLocaleTimeString()}</span>
                  </li>
                ))}
            </ul>
          )}
        </Card>

        <Card className="h-fit">
          <CardHeader icon={<Icons.Info size={16} />} title="Details" />
          <KeyValue
            items={[
              { label: 'State', value: humanizeState(state) },
              { label: 'Plan', value: planId ? <Link className="text-accent hover:underline" to={`/plans/${planId}`}>{shortId(planId)}</Link> : '—' },
              { label: 'Source', value: review.data?.source?.displayName ?? '—' },
              { label: 'Target', value: <span className="text-warning">{review.data?.target?.displayName ?? '—'}</span> },
              { label: 'Tables', value: review.data ? formatNumber(review.data.tables.length) : '—' },
              { label: 'Started', value: summary ? new Date(summary.createdUtc).toLocaleString() : '—' },
              { label: 'Last update', value: summary ? formatRelative(summary.updatedUtc) : '—' },
            ]}
          />
          {commands.length > 0 ? (
            <p className="mt-4 text-xs text-fg-faint">Pause and cancel take effect at the next committed batch, so the counters may advance briefly after a request.</p>
          ) : null}
        </Card>
      </div>

      <Modal
        description="Committed batches stay in the target. Uncommitted work is discarded and the job cannot be resumed."
        footer={
          <>
            <Button onClick={() => setCancelling(false)}>Keep running</Button>
            <Button
              icon={<Icons.Stop size={15} />}
              loading={command.isPending}
              onClick={() => {
                command.mutate('Cancel');
                setCancelling(false);
              }}
              variant="danger"
            >
              Cancel transfer
            </Button>
          </>
        }
        onClose={() => setCancelling(false)}
        open={cancelling}
        title="Cancel this transfer?"
        tone="danger"
      />
    </>
  );
}

function StreamIndicator({ status, terminal }: Readonly<{ status: JobStreamStatus; terminal: boolean }>) {
  if (terminal) return <Badge tone="neutral">Finished</Badge>;
  const map: Record<JobStreamStatus, { label: string; tone: 'success' | 'warning' | 'danger' | 'info' | 'neutral' }> = {
    connecting: { label: 'Connecting to live events', tone: 'info' },
    live: { label: 'Live', tone: 'success' },
    reconnecting: { label: 'Reconnecting…', tone: 'warning' },
    ended: { label: 'Stream ended', tone: 'neutral' },
    forbidden: { label: 'Live events forbidden', tone: 'danger' },
    unauthorized: { label: 'Session expired', tone: 'danger' },
    'cursor-expired': { label: 'Resyncing…', tone: 'warning' },
  };
  const item = map[status];
  return (
    <Badge dot pulse={status === 'live' || status === 'connecting' || status === 'reconnecting'} tone={item.tone}>
      <span className={cx(status === 'live' && 'font-semibold')}>{item.label}</span>
    </Badge>
  );
}
