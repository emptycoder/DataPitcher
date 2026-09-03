import { useMutation } from '@tanstack/react-query';
import { useMemo, useState, type FormEvent } from 'react';
import { providerLabels } from '../../api/connections';
import { plansApi } from '../../api/plans';
import { describeError } from '../../api/problem';
import { useAuth } from '../../auth/AuthContext';
import { usePermissions } from '../../auth/permissions';
import { navigate, useLocationSearch } from '../../app/router';
import { registryActions, usePlanEntry, useSelectionRegistry } from '../../stores/registryStore';
import { useSourceConnectionId, useTargetConnectionId } from '../../stores/sessionStore';
import { Alert, Badge, Button, Card, CardHeader, Field, PageHeader, Select, TextArea, TextInput, cx, shortId } from '../../ui';
import { Icons } from '../../ui/icons';
import { useToast } from '../../ui/toast';
import { ProviderMark } from '../connections/ConnectionsScreen';
import { useConnections, usePlanReview, useSelections } from '../shared/queries';

export function PlanBuilderScreen({ planId }: Readonly<{ planId: string | null }>) {
  const search = useLocationSearch();
  const { authentication } = useAuth();
  const { hasPermission } = usePermissions();
  const toast = useToast();
  const connections = useConnections();
  const selections = useSelections();
  const selectionNames = useSelectionRegistry();
  const existing = usePlanEntry(planId);
  const review = usePlanReview(planId);
  const sessionSource = useSourceConnectionId();
  const sessionTarget = useTargetConnectionId();

  const [name, setName] = useState(existing?.name ?? '');
  const [note, setNote] = useState(existing?.note ?? '');
  // Null means "not touched yet": the value then falls back to the registry, the server review, or the session pair.
  const [selectionChoice, setSelectionId] = useState<string | null>(search.get('selection'));
  const [sourceChoice, setSourceId] = useState<string | null>(null);
  const [targetChoice, setTargetId] = useState<string | null>(null);
  const selectionId = selectionChoice ?? existing?.selectionId ?? review.data?.selection?.selectionId ?? '';
  const selectionEntry = selectionNames[selectionId] ?? null;
  const sourceId = sourceChoice ?? existing?.sourceConnectionId ?? review.data?.source?.connectionId ?? selectionEntry?.connectionId ?? sessionSource ?? '';
  const targetId = targetChoice ?? existing?.targetConnectionId ?? review.data?.target?.connectionId ?? sessionTarget ?? '';

  const source = connections.data?.find((item) => item.connectionId === sourceId) ?? null;
  const target = connections.data?.find((item) => item.connectionId === targetId) ?? null;

  const warnings = useMemo(() => {
    const list: string[] = [];
    if (sourceId && targetId && sourceId === targetId) list.push('Source and target are the same connection. The transfer would write back into the database it reads from.');
    if (selectionEntry?.connectionId && sourceId && selectionEntry.connectionId !== sourceId) list.push('The selection was authored against a different connection than the chosen source. Sealing requires them to match.');
    if (source && source.providerId !== 'sqlserver') list.push('Sealing currently supports SQL Server sources only.');
    if (target && target.providerId !== 'sqlserver') list.push('Sealing currently supports SQL Server targets only.');
    if (source && target && source.providerId !== target.providerId) list.push('Cross-provider transfers are blocked by default.');
    if (planId && existing?.sealed) list.push('Saving changes to a sealed plan invalidates its seal. You will need to seal it again.');
    return list;
  }, [sourceId, targetId, selectionEntry, source, target, planId, existing]);

  const save = useMutation({
    mutationFn: async () => {
      const id = planId ?? crypto.randomUUID();
      const version = planId ? (review.data?.version ?? null) : null;
      const response = await plansApi.save(
        id,
        {
          displayName: name.trim(),
          operatorNote: note.trim() || null,
          ifMatch: version === null ? '*' : `"${version}"`,
          selectionId: selectionId || null,
          sourceConnectionId: sourceId || null,
          targetConnectionId: targetId || null,
        },
        authentication,
      );
      return { id, response };
    },
    onSuccess: ({ id }) => {
      registryActions.upsertPlan({ planId: id, name: name.trim(), note: note.trim() || null, selectionId: selectionId || null, sourceConnectionId: sourceId || null, targetConnectionId: targetId || null, sealed: false, plannedWrites: null });
      toast.success(planId ? 'Plan updated' : 'Plan created', 'Seal it to compute the transfer set.');
      navigate(`/plans/${id}`);
    },
    onError: (error) => toast.error('Unable to save the plan', describeError(error)),
  });

  const complete = Boolean(name.trim() && selectionId && sourceId && targetId);

  function submit(event: FormEvent) {
    event.preventDefault();
    if (complete) save.mutate();
  }

  return (
    <>
      <PageHeader
        actions={
          <Button onClick={() => navigate(planId ? `/plans/${planId}` : '/plans')} variant="ghost">
            Cancel
          </Button>
        }
        description="Pair a saved selection with the databases it reads from and writes to."
        eyebrow={planId ? 'Edit plan' : 'New plan'}
        title={name || (planId ? 'Edit transfer plan' : 'New transfer plan')}
      />
      <form className="grid gap-5 lg:grid-cols-[1fr_360px]" onSubmit={submit}>
        <div className="grid content-start gap-5">
          <Card>
            <CardHeader icon={<Icons.Clipboard size={16} />} title="Plan" />
            <div className="grid gap-4">
              <Field label="Plan name" required>
                <TextInput onChange={(event) => setName(event.target.value)} placeholder="e.g. Customer 42 to staging" value={name} />
              </Field>
              <Field hint="Shown to reviewers alongside the plan." label="Operator note">
                <TextArea onChange={(event) => setNote(event.target.value)} placeholder="Why this transfer is happening, ticket references, caveats…" rows={3} value={note} />
              </Field>
            </div>
          </Card>

          <Card>
            <CardHeader description="Which root rows to transfer." icon={<Icons.Filter size={16} />} title="Selection" />
            <Field label="Saved selection" required>
              <Select onChange={(event) => setSelectionId(event.target.value)} value={selectionId}>
                <option value="">Choose a saved selection…</option>
                {(selections.data ?? []).map((selection) => (
                  <option key={selection.selectionId} value={selection.selectionId}>
                    {selectionNames[selection.selectionId]?.name || selection.displayName || 'Untitled selection'} · {selectionNames[selection.selectionId]?.rootTable ?? selection.mode} · {shortId(selection.selectionId)}
                  </option>
                ))}
              </Select>
            </Field>
            {selections.data && selections.data.length === 0 ? (
              <Alert className="mt-3" tone="info">
                No saved selections yet.{' '}
                <button className="underline" onClick={() => navigate('/selections/new')} type="button">
                  Build one first.
                </button>
              </Alert>
            ) : null}
          </Card>

          <Card>
            <CardHeader description="Rows are read from the source and written into the target." icon={<Icons.Database size={16} />} title="Databases" />
            <div className="grid gap-4 md:grid-cols-[1fr_auto_1fr] md:items-end">
              <ConnectionSelect label="Source" onChange={setSourceId} value={sourceId} />
              <div className="hidden pb-2.5 text-fg-faint md:block">
                <Icons.ArrowRight size={20} />
              </div>
              <ConnectionSelect emphasis label="Target" onChange={setTargetId} value={targetId} />
            </div>
          </Card>

          {warnings.length > 0 ? (
            <Alert title="Before you continue" tone="warning">
              <ul className="list-disc pl-4">
                {warnings.map((warning) => (
                  <li key={warning}>{warning}</li>
                ))}
              </ul>
            </Alert>
          ) : null}
        </div>

        <div className="grid content-start gap-4">
          <Card>
            <CardHeader icon={<Icons.Eye size={16} />} title="Summary" />
            <div className="grid gap-3">
              <SummaryRow label="Selection" value={selectionEntry?.name || (selectionId ? `Selection ${shortId(selectionId)}` : null)} />
              <SummaryConnection connection={source} label="Source" tone="accent" />
              <SummaryConnection connection={target} label="Target" tone="warning" />
            </div>
            <Button block className="mt-5" disabled={!complete || !hasPermission('Plans.Write')} icon={<Icons.Check size={16} />} loading={save.isPending} type="submit" variant="primary">
              {planId ? 'Save changes' : 'Create plan'}
            </Button>
            <p className="mt-3 text-xs text-fg-faint">Creating a plan does not touch either database. Sealing reads both; only a started transfer writes.</p>
          </Card>
        </div>
      </form>
    </>
  );
}

function ConnectionSelect({ label, value, onChange, emphasis }: Readonly<{ label: string; value: string; onChange: (id: string) => void; emphasis?: boolean }>) {
  const connections = useConnections();
  return (
    <Field label={label} required>
      <select
        className={cx(
          'h-9.5 w-full appearance-none rounded-lg border bg-surface px-3 text-sm text-fg focus:ring-2 focus:ring-accent/25 focus:outline-none',
          emphasis ? 'border-warning' : 'border-border',
        )}
        onChange={(event) => onChange(event.target.value)}
        value={value}
      >
        <option value="">Choose…</option>
        {(connections.data ?? []).map((connection) => (
          <option key={connection.connectionId} value={connection.connectionId}>
            {connection.displayName} · {providerLabels[connection.providerId] ?? connection.providerId} · {connection.health}
          </option>
        ))}
      </select>
    </Field>
  );
}

function SummaryRow({ label, value }: Readonly<{ label: string; value: string | null }>) {
  return (
    <div className="flex items-center justify-between gap-3 rounded-xl border border-border px-3 py-2.5 text-sm">
      <span className="text-fg-muted">{label}</span>
      <span className={value ? 'truncate font-medium text-fg' : 'text-fg-faint'}>{value ?? 'Not chosen'}</span>
    </div>
  );
}

function SummaryConnection({ label, connection, tone }: Readonly<{ label: string; connection: Readonly<{ displayName: string; providerId: string; health: string }> | null; tone: 'accent' | 'warning' }>) {
  return (
    <div className={cx('flex items-center gap-3 rounded-xl border px-3 py-2.5', tone === 'warning' ? 'border-warning/50 bg-warning-soft/40' : 'border-border')}>
      {connection ? <ProviderMark providerId={connection.providerId} size="sm" /> : <span className="size-7 rounded-lg bg-surface-3" />}
      <div className="min-w-0 flex-1">
        <div className="text-[11px] font-semibold tracking-wide text-fg-muted uppercase">{label}</div>
        <div className={cx('truncate text-sm', connection ? 'font-medium text-fg' : 'text-fg-faint')}>{connection?.displayName ?? 'Not chosen'}</div>
      </div>
      {connection ? <Badge dot tone={connection.health === 'Healthy' ? 'success' : 'warning'}>{connection.health}</Badge> : null}
    </div>
  );
}
