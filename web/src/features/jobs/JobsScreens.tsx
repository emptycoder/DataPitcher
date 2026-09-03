import { useMutation, useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { usePermissions } from '../../auth/permissions';
import { HttpError, requestJson } from '../../api/http';
import { Link, navigate } from '../../app/router';
import { Button, DataTable, Field, InlineError, LoadingIndicator, StatusBadge, TextInput } from '../../ui';

export type JobCommand = 'Pause' | 'Resume' | 'Cancel';
type Job = Readonly<{ jobId: string; planId: string; state: string; rowsTransferred: number; bytesTransferred: number }>;

const commandsByState: Readonly<Record<string, readonly JobCommand[]>> = {
  Draft: ['Cancel'],
  Queued: ['Cancel'],
  Preparing: ['Pause', 'Resume', 'Cancel'],
  Running: ['Pause', 'Resume', 'Cancel'],
  Pausing: ['Resume', 'Cancel'],
  Paused: ['Resume', 'Cancel'],
  Cancelling: [],
  Cancelled: [],
  Verifying: [],
  Succeeded: [],
  Failed: [],
  VerificationFailed: [],
};

export function legalJobCommands(state: string): readonly JobCommand[] {
  return commandsByState[state] ?? [];
}

export function jobStatePresentation(state: string): Readonly<{ badge: string; label: string; failure: boolean; unknown: boolean }> {
  if (!(state in commandsByState)) return { badge: 'Unknown', label: 'Unknown', failure: false, unknown: true };
  if (state === 'VerificationFailed') return { badge: state, label: 'Verification failed', failure: true, unknown: false };
  if (state === 'Failed') return { badge: state, label: 'Failed', failure: true, unknown: false };
  return { badge: state, label: state.replace(/([a-z])([A-Z])/g, '$1 $2'), failure: false, unknown: false };
}

export function requestErrorMessage(error: unknown): string {
  if (!(error instanceof HttpError)) return 'The job service could not be reached.';
  const messages: Readonly<Record<number, string>> = {
    401: 'Sign in to access this job.',
    403: 'You do not have permission to access this job.',
    404: 'This job was not found.',
    409: 'This job changed before the request could be completed.',
  };
  return messages[error.status] ?? (error.status >= 500 ? 'The job service is unavailable. Try again.' : 'The job request failed.');
}

export function JobsListScreen() {
  const [jobId, setJobId] = useState('');

  function openJob(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    const id = jobId.trim();
    if (id) navigate(`/jobs/${id}`);
  }

  return (
    <section aria-label="Transfer jobs">
      <h2>Transfer jobs</h2>
      <InlineError>Job listing is unavailable. The API needs GET /api/jobs to provide job summaries.</InlineError>
      <form aria-label="Open transfer job" onSubmit={openJob}>
        <Field label="Job ID"><TextInput value={jobId} onChange={(event) => setJobId(event.target.value)} required /></Field>
        <Button disabled={!jobId.trim()}>Open job</Button>
      </form>
    </section>
  );
}

export function JobDetailScreen({ jobId, authentication }: Readonly<{ jobId: string; authentication: AuthenticationAdapter }>) {
  const { hasPermission } = usePermissions();
  const job = useQuery({
    queryKey: ['job-detail', jobId],
    queryFn: ({ signal }) => requestJson<Job>(`/api/jobs/${jobId}`, authentication, { signal }),
    retry: false,
  });
  const command = useMutation({
    mutationFn: (kind: JobCommand) => requestJson<unknown>(`/api/jobs/${jobId}/commands`, authentication, { method: 'POST', body: { command: kind } }),
    onSuccess: () => job.refetch(),
  });

  function sendCommand(kind: JobCommand) {
    if (kind !== 'Cancel' || window.confirm('Cancelling a transfer leaves partially copied data. Cancel this transfer?')) command.mutate(kind);
  }

  if (job.isPending) return <LoadingIndicator label="Loading transfer job." />;
  if (!job.data) return <InlineError>{requestErrorMessage(job.error)}</InlineError>;
  const presentation = jobStatePresentation(job.data.state);
  const commands = hasPermission('Transfers.Write') ? legalJobCommands(job.data.state) : [];

  return (
    <section aria-label="Transfer job">
      <h2>Transfer job</h2>
      <StatusBadge state={presentation.badge} />
      <p>{presentation.label}</p>
      {presentation.unknown ? <InlineError>{`Job state is unknown. The server reported "${job.data.state}".`}</InlineError> : null}
      {presentation.failure ? <InlineError>{`${presentation.label}. This transfer did not succeed.`}</InlineError> : null}
      {job.data.state === 'Running' ? <p><Link to={`/transfer-monitor/${jobId}`}>Monitor live transfer</Link></p> : null}
      <DataTable aria-label="Job details">
        <tbody>
          <tr><th scope="row">Plan</th><td><Link to={`/plan-review/${job.data.planId}`}>{job.data.planId}</Link></td></tr>
          <tr><th scope="row">Source</th><td>Not available from the job API.</td></tr>
          <tr><th scope="row">Target</th><td>Not available from the job API.</td></tr>
          <tr><th scope="row">Timing</th><td>Not available from the job API.</td></tr>
          <tr><th scope="row">Rows transferred</th><td>{job.data.rowsTransferred.toLocaleString('en-US')}</td></tr>
          <tr><th scope="row">Bytes transferred</th><td>{job.data.bytesTransferred.toLocaleString('en-US')}</td></tr>
          <tr><th scope="row">Failure detail</th><td>{presentation.failure ? 'The job API did not provide failure detail.' : 'No failure detail is available from the job API.'}</td></tr>
        </tbody>
      </DataTable>
      {command.isError ? <InlineError>{requestErrorMessage(command.error)}</InlineError> : null}
      {commands.map((kind) => <Button key={kind} disabled={command.isPending} onClick={() => sendCommand(kind)}>{command.isPending ? 'Sending command' : `${kind} transfer`}</Button>)}
    </section>
  );
}
