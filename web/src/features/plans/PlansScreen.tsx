import { useMemo, useState, type FormEvent } from 'react';
import { formatNumber, formatRelative } from '../../api/format';
import { usePermissions } from '../../auth/permissions';
import { Link, navigate } from '../../app/router';
import { registryActions, usePlanRegistry, useSelectionRegistry } from '../../stores/registryStore';
import { Badge, Button, Card, CardHeader, DataTable, EmptyState, IconButton, PageHeader, TextInput, shortId } from '../../ui';
import { Icons } from '../../ui/icons';
import { useConnections } from '../shared/queries';

const guidPattern = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

export function PlansScreen() {
  const plans = usePlanRegistry();
  const selections = useSelectionRegistry();
  const connections = useConnections();
  const { hasPermission } = usePermissions();
  const [openId, setOpenId] = useState('');
  const list = useMemo(() => Object.values(plans).toSorted((a, b) => b.updatedAt.localeCompare(a.updatedAt)), [plans]);
  const nameOf = (connectionId: string | null) => connections.data?.find((item) => item.connectionId === connectionId)?.displayName ?? null;

  function openById(event: FormEvent) {
    event.preventDefault();
    const id = openId.trim();
    if (!guidPattern.test(id)) return;
    navigate(`/plans/${id}`);
  }

  return (
    <>
      <PageHeader
        actions={
          hasPermission('Plans.Write') ? (
            <Button icon={<Icons.Plus size={16} />} onClick={() => navigate('/plans/new')} variant="primary">
              New plan
            </Button>
          ) : null
        }
        description="A plan pairs a selection with a source and a target. Sealing computes the exact dependency closure and freezes it for review."
        title="Transfer plans"
      />

      <div className="grid gap-5 lg:grid-cols-[1fr_320px]">
        {list.length === 0 ? (
          <Card padded={false}>
            <EmptyState
              action={
                hasPermission('Plans.Write') ? (
                  <Button icon={<Icons.Plus size={16} />} onClick={() => navigate('/plans/new')} variant="primary">
                    Create a plan
                  </Button>
                ) : undefined
              }
              description="Plans you create in this browser are listed here. Open any other plan by its identifier."
              icon={<Icons.Clipboard size={22} />}
              title="No plans yet"
            />
          </Card>
        ) : (
          <Card padded={false}>
            <DataTable>
              <thead>
                <tr>
                  <th>Plan</th>
                  <th>Selection</th>
                  <th>Source → Target</th>
                  <th>Status</th>
                  <th>Rows</th>
                  <th>Updated</th>
                  <th />
                </tr>
              </thead>
              <tbody>
                {list.map((plan) => (
                  <tr className="hover:bg-surface-2" key={plan.planId}>
                    <td>
                      <Link className="font-semibold text-fg hover:text-accent" to={`/plans/${plan.planId}`}>
                        {plan.name || `Plan ${shortId(plan.planId)}`}
                      </Link>
                      <div className="font-mono text-[11px] text-fg-faint">{shortId(plan.planId)}</div>
                    </td>
                    <td className="text-fg-muted">{plan.selectionId ? selections[plan.selectionId]?.name || `Selection ${shortId(plan.selectionId)}` : '—'}</td>
                    <td className="text-fg-muted">
                      {nameOf(plan.sourceConnectionId) ?? '—'} <span className="text-fg-faint">→</span> <span className="font-medium text-fg">{nameOf(plan.targetConnectionId) ?? '—'}</span>
                    </td>
                    <td>
                      <Badge dot tone={plan.sealed ? 'success' : 'warning'}>
                        {plan.sealed ? 'Sealed' : 'Draft'}
                      </Badge>
                    </td>
                    <td className="tnum text-fg-muted">{plan.plannedWrites === null ? '—' : formatNumber(plan.plannedWrites)}</td>
                    <td className="text-fg-muted">{formatRelative(plan.updatedAt)}</td>
                    <td className="text-right">
                      <IconButton label="Forget this plan on this device" onClick={() => registryActions.forgetPlan(plan.planId)} size="sm">
                        <Icons.X size={14} />
                      </IconButton>
                    </td>
                  </tr>
                ))}
              </tbody>
            </DataTable>
          </Card>
        )}

        <Card className="h-fit">
          <CardHeader description="Plans created elsewhere are not listed by the API, but can be opened directly." icon={<Icons.Search size={16} />} title="Open a plan by ID" />
          <form className="flex gap-2" onSubmit={openById}>
            <TextInput className="font-mono" onChange={(event) => setOpenId(event.target.value)} placeholder="00000000-0000-0000-0000-000000000000" value={openId} />
            <Button disabled={!guidPattern.test(openId.trim())} type="submit">
              Open
            </Button>
          </form>
        </Card>
      </div>
    </>
  );
}
