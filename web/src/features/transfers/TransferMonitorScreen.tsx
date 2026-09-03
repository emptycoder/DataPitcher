import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useEffect, useState } from 'react';
import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { createTransferMonitor, fetchTransferJob, type TransferMonitorRequest, type TransferMonitorScheduler, type TransferMonitorState } from './transferMonitor';
import { presentationForJob, tableProgressLabel } from './transferMonitorModel';

export type TransferMonitorScreenProps = Readonly<{
  jobId: string | null;
  request: TransferMonitorRequest;
  authentication: AuthenticationAdapter;
  clock: () => number;
  scheduler: TransferMonitorScheduler;
}>;

export function TransferMonitorScreen({ jobId, request, authentication, clock, scheduler }: TransferMonitorScreenProps) {
  const queryClient = useQueryClient();
  const [streamState, setStreamState] = useState<TransferMonitorState>('connecting');
  const jobQuery = useQuery({
    queryKey: ['job', jobId],
    queryFn: ({ signal }) => fetchTransferJob(jobId!, request, authentication, signal),
    enabled: jobId !== null,
  });

  useEffect(() => {
    if (!jobId || !jobQuery.isSuccess || !jobQuery.data) return;
    const monitor = createTransferMonitor({
      job: jobQuery.data,
      request,
      authentication,
      cache: { set: (job) => queryClient.setQueryData(['job', jobId], job) },
      clock,
      scheduler,
      onState: setStreamState,
    });
    void monitor.start();
    return monitor.stop;
  }, [authentication, clock, jobId, jobQuery.isSuccess, queryClient, request, scheduler]);

  if (!jobId) return <p role="status">Choose a transfer job to monitor.</p>;
  if (jobQuery.isPending) return <p role="status">Loading transfer job.</p>;
  if (jobQuery.isError || !jobQuery.data) return <p role="status">Unable to load transfer job.</p>;
  const presentation = presentationForJob(jobQuery.data);

  return (
    <section aria-label="Transfer monitor">
      <h2>Transfer monitor</h2>
      <p role="status" data-success={presentation.successful}>{presentation.label}</p>
      <output aria-label="Stream state">{streamState}</output>
      <dl>
        <div><dt>Rows transferred</dt><dd aria-label="Rows transferred">{jobQuery.data.rowsTransferred.toLocaleString('en-US')}</dd></div>
        <div><dt>Bytes transferred</dt><dd aria-label="Bytes transferred">{jobQuery.data.bytesTransferred.toLocaleString('en-US')}</dd></div>
        <div><dt>Throughput</dt><dd aria-label="Throughput">{`${Math.round(jobQuery.data.bytesPerSecond ?? 0).toLocaleString('en-US')} bytes/s`}</dd></div>
      </dl>
      <h3>Per-table progress</h3>
      {jobQuery.data.tableProgress.length === 0 ? <p>No per-table progress has been reported.</p> : <ul>{jobQuery.data.tableProgress.map((progress) => <li key={progress.table}><progress value={progress.rowsTransferred} max={progress.totalRows ?? (progress.rowsTransferred || 1)} />{tableProgressLabel(progress)}</li>)}</ul>}
    </section>
  );
}
