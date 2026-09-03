import { useEffect, useState } from 'react';
import { HttpError, requestJson } from '../../api/http';
import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { InlineError, LoadingIndicator, StatusBadge } from '../../ui';
import { JobEventState, isTerminalJobState, reduceJobEvent, streamJobEvents, type JobEventProblem, type JobEventRequest, type JobView } from './jobEvents';
import type { TransferMonitorScheduler } from './transferMonitor';

export type TransferMonitorScreenProps = Readonly<{
  jobId: string | null;
  request: JobEventRequest;
  authentication: AuthenticationAdapter;
  clock: () => number;
  scheduler: TransferMonitorScheduler;
}>;

type CurrentJob = Omit<JobView, 'state'> & Readonly<{ state: string }>;

const permanentAuthorizationFailure = 'Authorization failed permanently. You are not allowed to monitor this transfer.';
const streamProblemMessages: Record<JobEventProblem['reason'], string> = {
  'malformed-payload': 'A transfer progress event was invalid.',
  'unknown-event': 'A transfer progress event was not recognized.',
  unauthorized: 'Authorization failed while monitoring this transfer.',
  forbidden: permanentAuthorizationFailure,
  'request-failed': 'The transfer progress stream failed.',
  'missing-body': 'The transfer progress stream was unavailable.',
};

function currentJobView(job: CurrentJob): JobView {
  const state = JobEventState.safeParse(job.state.toLowerCase());
  return { ...job, state: state.success ? state.data : 'unknown' };
}

function initialProblem(error: unknown) {
  return error instanceof HttpError && error.status === 403 ? permanentAuthorizationFailure : 'Unable to load transfer progress.';
}

export function TransferMonitorScreen({ jobId, request, authentication, clock }: TransferMonitorScreenProps) {
  const [view, setView] = useState<JobView | null>(null);
  const [problem, setProblem] = useState<string | null>(null);
  const [startedAt] = useState(clock);

  useEffect(() => {
    if (!jobId) return;
    const id = jobId;
    const controller = new AbortController();

    async function monitor() {
      try {
        let current = currentJobView(await requestJson<CurrentJob>(`/api/jobs/${id}`, authentication, { signal: controller.signal }));
        setView(current);
        if (isTerminalJobState(current.state)) return;

        for await (const event of streamJobEvents(id, request, authentication, controller.signal)) {
          if (event.type === 'problem') {
            setProblem(streamProblemMessages[event.reason]);
            return;
          }
          const next = reduceJobEvent(current, event);
          current = next;
          setView(next);
          if (isTerminalJobState(next.state)) {
            controller.abort();
            return;
          }
        }
      } catch (error) {
        if (!controller.signal.aborted) setProblem(initialProblem(error));
      }
    }

    void monitor();
    return () => controller.abort();
  }, [authentication, jobId, request]);

  if (!jobId) return <p role="status">Choose a transfer job to monitor.</p>;
  if (!view) return problem ? <InlineError>{problem}</InlineError> : <LoadingIndicator label="Loading transfer progress." />;

  const terminal = isTerminalJobState(view.state);
  const totalKnown = view.totalRows !== undefined;
  const elapsedSeconds = Math.max(0, Math.floor((clock() - startedAt) / 1_000));

  return (
    <section aria-label="Transfer monitor">
      <h2>Transfer monitor</h2>
      <StatusBadge state={view.state} />
      <p aria-live="polite" aria-atomic="true">{`Transfer ${view.state}: ${view.rowsTransferred.toLocaleString('en-US')} rows transferred.`}</p>
      {problem ? <InlineError>{problem}</InlineError> : null}
      {view.state === 'verificationfailed' ? <InlineError>Verification failed. This transfer did not succeed.</InlineError> : null}
      {view.state === 'unknown' ? <InlineError>Transfer state is unknown.</InlineError> : null}
      {view.state === 'succeeded' ? <p role="status">Transfer succeeded.</p> : null}
      <dl>
        <div><dt>Rows transferred</dt><dd aria-label="Rows transferred">{view.rowsTransferred.toLocaleString('en-US')}</dd></div>
        <div><dt>Total rows</dt><dd>{totalKnown ? view.totalRows.toLocaleString('en-US') : 'Unknown'}</dd></div>
        <div><dt>Current table</dt><dd>{view.currentTable ?? 'No table reported'}</dd></div>
        <div><dt>Elapsed time</dt><dd aria-label="Elapsed time">{`${elapsedSeconds}s`}</dd></div>
      </dl>
      {!terminal && view.state !== 'unknown' ? totalKnown ? <progress aria-label="Transfer progress" value={view.rowsTransferred} max={Math.max(view.totalRows, 1)} /> : <progress aria-label="Transfer activity" /> : null}
    </section>
  );
}
