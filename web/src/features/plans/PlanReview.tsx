import { useState } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { usePermissions } from '../../auth/permissions';
import { Button, DataTable, Field, InlineError, LoadingIndicator, StatusBadge, TextInput } from '../../ui';
import { getInclusionPath, getPlanReview, type InclusionPath, type PlanReview as PlanReviewData } from './plansApi';

export type PlanReviewProps = Readonly<{ planId: string; authentication: AuthenticationAdapter }>;

function count(value: number | null | undefined, sealed: boolean) {
  return sealed && typeof value === 'number' ? value.toLocaleString('en-US') : 'Unknown';
}

function address(value: Readonly<{ schema: string; name: string }>) {
  return `${value.schema}.${value.name}`;
}

function InclusionPathLookup({ planId, table, authentication }: Readonly<{ planId: string; table: string; authentication: AuthenticationAdapter }>) {
  const [stableKey, setStableKey] = useState('');
  const inclusion = useMutation({ mutationFn: () => getInclusionPath(planId, table, stableKey, authentication) });

  return (
    <form aria-label={`Inclusion path for ${table}`} onSubmit={(event) => { event.preventDefault(); inclusion.mutate(); }}>
      <Field label={`Stable key for ${table}`}><TextInput value={stableKey} required onChange={(event) => setStableKey(event.target.value)} /></Field>
      <Button type="submit" disabled={!stableKey || inclusion.isPending}>{inclusion.isPending ? 'Loading explanation' : 'Explain inclusion'}</Button>
      {inclusion.isError ? <InlineError>Inclusion paths are unavailable from this server.</InlineError> : null}
      {inclusion.data ? <InclusionPathDetails path={inclusion.data} /> : null}
    </form>
  );
}

function InclusionPathDetails({ path }: Readonly<{ path: InclusionPath }>) {
  return <section aria-label={`Why ${path.table} is included`}><p>{`Root selection: ${path.rootSelection}`}</p><ol>{path.steps.map((step) => <li key={`${step.relationship}-${step.from}-${step.to}`}>{step.reason}</li>)}</ol></section>;
}

export function PlanReview({ planId, authentication }: PlanReviewProps) {
  const { isVerified, hasPermission } = usePermissions();
  const canReview = !isVerified || hasPermission('Plans.Read');
  const review = useQuery({
    queryKey: ['plan-review', planId],
    queryFn: ({ signal }) => getPlanReview(planId, authentication, signal),
    enabled: canReview,
    retry: false,
  });

  if (!canReview) return <InlineError>You do not have permission to review this plan.</InlineError>;
  if (review.isPending) return <LoadingIndicator label="Loading plan review" />;
  if (review.isError) return <InlineError>Unable to load plan review.</InlineError>;

  return <ReviewManifest review={review.data} planId={planId} authentication={authentication} />;
}

function ReviewManifest({ review, planId, authentication }: Readonly<{ review: PlanReviewData; planId: string; authentication: AuthenticationAdapter }>) {
  const sealed = review.seal.status === 'sealed';

  return (
    <section aria-label="Plan review">
      <h2>Plan review</h2>
      <p><strong>Source database:</strong> unavailable in this review payload.</p>
      <p><strong>Target database:</strong> unavailable in this review payload.</p>
      <p>{`Canonical hash: ${review.canonicalHash || 'Unknown'}`}</p>
      <StatusBadge state={review.seal.status} />
      {!sealed ? <InlineError>Unsealed — do not transfer.</InlineError> : null}
      {review.seal.invalidationReasons.map((reason) => <p key={reason.code}>{reason.message}</p>)}
      <section aria-label="Overall totals">
        <h3>Overall totals</h3>
        <dl>
          <div><dt>Included rows</dt><dd>{count(review.totals.included, sealed)}</dd></div>
          <div><dt>Planned writes</dt><dd>{count(review.totals.plannedWrites, sealed)}</dd></div>
          <div><dt>Inserts</dt><dd>{count(review.totals.inserts, sealed)}</dd></div>
          <div><dt>Updates</dt><dd>{count(review.totals.updates, sealed)}</dd></div>
          <div><dt>Estimated bytes</dt><dd>{count(review.totals.estimatedBytes, sealed)}</dd></div>
        </dl>
      </section>
      {review.tables.length === 0 ? <p>No tables were included in this plan.</p> : null}
      <DataTable>
        <caption>Per-table manifest</caption>
        <thead><tr><th>Source</th><th>Target</th><th>State</th><th>Included</th><th>Planned writes</th><th>Inclusion path</th></tr></thead>
        <tbody>{review.tables.map((table) => <tr key={`${address(table.source)}-${table.transferOrder}`}>
          <td>{address(table.source)}</td><td>{address(table.target)}</td><td>{table.state}</td><td>{count(table.included, sealed)}</td><td>{count(table.plannedWrites, sealed)}</td>
          <td><InclusionPathLookup planId={planId} table={address(table.source)} authentication={authentication} /></td>
        </tr>)}</tbody>
      </DataTable>
    </section>
  );
}
