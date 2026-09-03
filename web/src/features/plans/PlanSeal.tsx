import { useEffect, useRef, useState, type FormEvent } from 'react';
import { useMutation, useQuery } from '@tanstack/react-query';
import { HttpError } from '../../api/http';
import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { usePermissions } from '../../auth/permissions';
import { Button, Field, InlineError, StatusBadge, TextInput } from '../../ui';
import { navigate } from '../../app/router';
import { getConnections, getOperationStatus, getPlanReview, getSelections, requestErrorMessage, savePlan, sealPlan, startPlan } from './sealApi';

export type PlanSealProps = Readonly<{ planId: string; authentication: AuthenticationAdapter }>;
type SealOutcome = 'idle' | 'pending' | 'sealed' | 'failed' | 'unknown';

export function PlanSeal({ planId, authentication }: PlanSealProps) {
  const { hasPermission } = usePermissions();
  const [displayName, setDisplayName] = useState('Transfer plan');
  const [operatorNote, setOperatorNote] = useState('');
  const [selectionId, setSelectionId] = useState('');
  const [sourceConnectionId, setSourceConnectionId] = useState('');
  const [targetConnectionId, setTargetConnectionId] = useState('');
  const [etag, setEtag] = useState<string | null>(null);
  const [operationId, setOperationId] = useState<string | null>(null);
  const [sealOutcome, setSealOutcome] = useState<SealOutcome>('idle');
  const [operationError, setOperationError] = useState<string | null>(null);
  const [operationFailure, setOperationFailure] = useState<string | null>(null);
  const [confirming, setConfirming] = useState(false);
  const idempotencyKey = useRef<string | null>(null);
  const starting = useRef(false);
  const review = useQuery({ queryKey: ['plan-seal-review', planId], queryFn: ({ signal }) => getPlanReview(planId, authentication, signal), retry: false });
  const selections = useQuery({ queryKey: ['saved-selections'], queryFn: ({ signal }) => getSelections(authentication, signal), initialData: { selections: [] }, retry: false });
  const connections = useQuery({ queryKey: ['connections'], queryFn: ({ signal }) => getConnections(authentication, signal), initialData: [], retry: false });
  const planMissing = review.error instanceof HttpError && review.error.status === 404;
  const plan = review.data;
  const { refetch: refetchPlan } = review;
  const selectedSelectionId = selectionId || plan?.selection?.selectionId || '';
  const selectedSourceId = sourceConnectionId || plan?.source?.connectionId || '';
  const selectedTargetId = targetConnectionId || plan?.target?.connectionId || '';
  const save = useMutation({
    mutationFn: () => savePlan(planId, { displayName, operatorNote: operatorNote || null, ifMatch: etag ?? (plan ? `"${plan.version}"` : '*'), selectionId: selectedSelectionId, sourceConnectionId: selectedSourceId, targetConnectionId: selectedTargetId }, authentication),
    onSuccess: async (response) => { setEtag(response.eTag); setSealOutcome('idle'); setConfirming(false); await refetchPlan(); },
  });
  const seal = useMutation({
    mutationFn: () => sealPlan(planId, authentication),
    onMutate: () => { setOperationId(null); setOperationError(null); setOperationFailure(null); setSealOutcome('pending'); },
    onSuccess: (receipt) => setOperationId(receipt.operationId),
    onError: () => setSealOutcome('failed'),
  });
  const start = useMutation({
    mutationFn: () => startPlan(planId, idempotencyKey.current!, authentication),
    onSuccess: (receipt) => navigate(`/transfer-monitor/${receipt.jobId}`),
    onSettled: () => { starting.current = false; },
  });

  useEffect(() => {
    if (!operationId) return;
    const controller = new AbortController();
    const finish = (outcome: SealOutcome, failureCode: string | null = null) => {
      window.clearInterval(interval);
      controller.abort();
      setOperationFailure(failureCode);
      setSealOutcome(outcome);
    };
    const poll = async () => {
      try {
        const status = await getOperationStatus(operationId, authentication, controller.signal);
        if (controller.signal.aborted) return;
        if (status.failed) return finish('failed', status.failureCode);
        if (status.state.toLowerCase() === 'unknown') return finish('unknown');
        if (!status.finished) return;
        const latest = await refetchPlan();
        if (!controller.signal.aborted) finish(latest.data?.seal.status.toLowerCase() === 'sealed' ? 'sealed' : 'unknown');
      } catch (error) {
        if (!controller.signal.aborted) { setOperationError(requestErrorMessage(error, 'Unable to check sealing status.', 'Sealing status conflicted.')); finish('failed'); }
      }
    };
    const interval = window.setInterval(() => void poll(), 1000);
    void poll();
    return () => { controller.abort(); window.clearInterval(interval); };
  }, [authentication, operationId, refetchPlan]);

  const associated = Boolean(plan?.selection && plan.source && plan.target);
  const sealed = sealOutcome === 'sealed' || (sealOutcome === 'idle' && plan?.seal.status.toLowerCase() === 'sealed');
  const canSeal = associated && hasPermission('Plans.Seal') && !seal.isPending && sealOutcome !== 'pending';
  const canStart = sealed && associated && hasPermission('Transfers.Start') && !start.isPending;
  const canConfirm = sealed && associated && hasPermission('Transfers.Start');
  const saveError = save.isError ? requestErrorMessage(save.error, 'Unable to save this plan.', 'This plan changed. Refresh and try again.') : null;
  const sealError = seal.isError ? requestErrorMessage(seal.error, 'Unable to seal this plan.', 'This plan changed. Refresh and try again.') : operationError;

  function submitPlan(event: FormEvent) {
    event.preventDefault();
    save.mutate();
  }

  function requestStart() {
    setConfirming(true);
  }

  function confirmStart() {
    if (starting.current) return;
    starting.current = true;
    idempotencyKey.current ??= crypto.randomUUID();
    start.mutate();
  }

  return (
    <section aria-label="Plan sealing">
      <h2>Seal transfer plan</h2>
      {review.isError && !planMissing ? <InlineError>{requestErrorMessage(review.error, 'Unable to load this plan.', 'This plan changed. Refresh and try again.')}</InlineError> : null}
      {planMissing ? <p>No saved plan exists yet. Associate it before sealing.</p> : null}
      <form aria-label="Plan association" onSubmit={submitPlan}>
        <Field label="Plan name"><TextInput value={displayName} required onChange={(event) => setDisplayName(event.target.value)} /></Field>
        <Field label="Operator note"><TextInput value={operatorNote} onChange={(event) => setOperatorNote(event.target.value)} /></Field>
        <Field label="Saved selection"><select value={selectedSelectionId} required onChange={(event) => setSelectionId(event.target.value)}><option value="">Choose a saved selection</option>{selections.data.selections.map((selection) => <option key={selection.selectionId} value={selection.selectionId}>{selection.displayName}</option>)}</select></Field>
        <Field label="Source database"><select value={selectedSourceId} required onChange={(event) => setSourceConnectionId(event.target.value)}><option value="">Choose a source database</option>{connections.data.map((connection) => <option key={connection.connectionId} value={connection.connectionId}>{connection.displayName}</option>)}</select></Field>
        <Field label="TARGET database"><select value={selectedTargetId} required onChange={(event) => setTargetConnectionId(event.target.value)}><option value="">Choose the TARGET database</option>{connections.data.map((connection) => <option key={connection.connectionId} value={connection.connectionId}>{connection.displayName}</option>)}</select></Field>
        {saveError ? <InlineError>{saveError}</InlineError> : null}
        <Button type="submit" disabled={!hasPermission('Plans.Write') || save.isPending}>{save.isPending ? 'Saving plan' : 'Save plan'}</Button>
      </form>
      <section aria-label="Transfer destination">
        <p>{`Saved selection: ${plan?.selection?.displayName ?? 'Not associated'}`}</p>
        <p>{`Source database: ${plan?.source?.displayName ?? 'Not associated'}`}</p>
        <p><strong>{`TARGET DATABASE: ${plan?.target?.displayName ?? 'Not associated'}`}</strong></p>
        <p>{`Total planned rows: ${plan?.totals.plannedWrites.toLocaleString('en-US') ?? 'Unknown'}`}</p>
      </section>
      <StatusBadge state={sealOutcome === 'idle' ? plan?.seal.status ?? 'unsealed' : sealOutcome} />
      {sealOutcome === 'pending' ? <p>Sealing in progress.</p> : null}
      {sealed ? <p>Plan is sealed.</p> : null}
      {sealOutcome === 'unknown' ? <InlineError>Seal status is unknown.</InlineError> : null}
      {sealOutcome === 'failed' ? <InlineError>{operationFailure ? `Sealing failed: ${operationFailure}.` : sealError ?? 'Sealing failed.'}</InlineError> : null}
      {!sealed && sealOutcome === 'idle' ? <p>Plan must be sealed before starting a transfer.</p> : null}
      <Button disabled={!canSeal} onClick={() => seal.mutate()}>{seal.isPending ? 'Sealing plan' : 'Seal plan'}</Button>
      {canStart ? <Button onClick={requestStart}>Start transfer</Button> : null}
      {confirming && canConfirm ? <section role="alertdialog" aria-label="Confirm transfer">
        <h3>Confirm transfer to TARGET DATABASE</h3>
        <p>{`Source database: ${plan!.source!.displayName}`}</p>
        <p><strong>{`TARGET DATABASE: ${plan!.target!.displayName}`}</strong></p>
        <p>{`Total planned rows: ${plan!.totals.plannedWrites.toLocaleString('en-US')}`}</p>
        {start.isError ? <InlineError>{requestErrorMessage(start.error, 'Unable to start transfer.', 'The plan must be sealed before starting a transfer.')}</InlineError> : null}
        <Button disabled={start.isPending} onClick={confirmStart}>{start.isPending ? 'Starting transfer' : 'Confirm start transfer'}</Button>
        <Button disabled={start.isPending} onClick={() => setConfirming(false)}>Cancel</Button>
      </section> : null}
    </section>
  );
}
