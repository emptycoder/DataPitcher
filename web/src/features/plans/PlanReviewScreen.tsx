import { useMutation, useQuery } from '@tanstack/react-query';
import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { fetchPlanReview, startPlanJob, type RequestFunction } from './planReviewApi';
import { planTableStateLabel, startAvailability } from './planReviewModel';

export type PlanReviewScreenProps = Readonly<{
  planId: string | null;
  request: RequestFunction;
  authentication: AuthenticationAdapter;
  onJobStarted: (jobId: string) => void;
}>;

export function PlanReviewScreen({ planId, request, authentication, onJobStarted }: PlanReviewScreenProps) {
  const review = useQuery({
    queryKey: ['plan-review', planId],
    queryFn: ({ signal }) => fetchPlanReview(planId!, request, authentication, signal),
    enabled: planId !== null,
  });
  const start = useMutation({
    mutationFn: () => startPlanJob(planId!, crypto.randomUUID(), request, authentication, new AbortController().signal),
    onSuccess: (receipt) => onJobStarted(receipt.jobId),
  });

  if (!planId) return <p role="status">Choose a transfer plan to review.</p>;
  if (review.isPending) return <p role="status">Loading plan review.</p>;
  if (review.isError || !review.data) return <p role="status">Unable to load plan review.</p>;
  const availability = startAvailability(review.data);

  return (
    <section aria-label="Plan review">
      <h2>Plan review</h2>
      <p>{`${review.data.totals.plannedWrites.toLocaleString('en-US')} planned writes across ${review.data.totals.included.toLocaleString('en-US')} included rows.`}</p>
      <ul>
        {review.data.tables.map((table, index) => (
          <li key={`${table.source.schema}.${table.source.name}-${index}`}>
            <strong>{`${table.source.schema}.${table.source.name}`}</strong>
            <span>{planTableStateLabel(table.state)}</span>
            <span>{`${table.plannedWrites.toLocaleString('en-US')} planned writes`}</span>
            {table.state === 'TargetSatisfied' ? <p>Target-satisfied: this row will not move. Its target may have different non-key values; DataPitcher will not refresh them.</p> : null}
          </li>
        ))}
      </ul>
      {availability.reasons.length > 0 ? <ul role="alert">{availability.reasons.map((reason) => <li key={reason}>{reason}</li>)}</ul> : null}
      {start.isError ? <p role="alert">Unable to start transfer.</p> : null}
      <button type="button" disabled={!availability.enabled || start.isPending} onClick={() => start.mutate()}>{start.isPending ? 'Starting transfer' : 'Start transfer'}</button>
    </section>
  );
}
