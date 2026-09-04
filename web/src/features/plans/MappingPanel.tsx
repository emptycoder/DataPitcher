import { useMutation, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { queryKeys } from '../../api/keys';
import { plansApi, type MappingProblem, type PlanMapping, type PlanMappingColumn, type PlanMappingTable, type PlanTableMappingInput } from '../../api/plans';
import { describeError } from '../../api/problem';
import { useAuth } from '../../auth/AuthContext';
import { usePermissions } from '../../auth/permissions';
import { Alert, Badge, Button, Card, EmptyState, Select, Skeleton, cx } from '../../ui';
import { Icons } from '../../ui/icons';
import { useToast } from '../../ui/toast';
import { usePlanMapping } from '../shared/queries';

/** Sentinel option value for "do not transfer this column"; target column names never collide with it. */
const NONE = '\u0000none';

type Choice = string | null;
type Overrides = Readonly<Record<string, Readonly<Record<string, Choice>>>>;

function tableKey(address: Readonly<{ schema: string; name: string }>) {
  return `${address.schema}.${address.name}`;
}

/** The overrides the API already stores, as the editor's starting state. */
export function storedOverrides(mapping: PlanMapping): Overrides {
  const result: Record<string, Record<string, Choice>> = {};
  for (const table of mapping.tables) {
    const chosen: Record<string, Choice> = {};
    for (const column of table.columns) {
      if (column.origin === 'override' || column.origin === 'excluded') chosen[column.source] = column.origin === 'excluded' ? null : column.target;
    }
    if (Object.keys(chosen).length > 0) result[tableKey(table.source)] = chosen;
  }
  return result;
}

/** What the API should store: only tables that carry a choice, each with only the columns chosen. */
export function toInput(mapping: PlanMapping, overrides: Overrides): PlanTableMappingInput[] {
  return mapping.tables.flatMap((table) => {
    const chosen = overrides[tableKey(table.source)];
    if (!chosen || Object.keys(chosen).length === 0) return [];
    return [{ source: table.source, target: null, columns: Object.entries(chosen).map(([source, target]) => ({ source, target })) }];
  });
}

export function problemCounts(problems: readonly MappingProblem[]) {
  return { blockers: problems.filter((problem) => problem.isBlocker).length, warnings: problems.filter((problem) => !problem.isBlocker).length };
}

function allProblems(table: PlanMappingTable): MappingProblem[] {
  return [...table.problems, ...table.columns.flatMap((column) => column.problems), ...table.targetOnlyColumns.flatMap((column) => column.problems)];
}

export function MappingPanel({ planId, sealed }: Readonly<{ planId: string; sealed: boolean }>) {
  const { authentication } = useAuth();
  const { hasPermission } = usePermissions();
  const queryClient = useQueryClient();
  const toast = useToast();
  const mapping = usePlanMapping(planId);
  // Null until the user changes something: the editor then shows what the API stores, and a fresh answer replaces it.
  const [edits, setEdits] = useState<Overrides | null>(null);
  const [expanded, setExpanded] = useState<string | null>(null);
  const stored = useMemo(() => (mapping.data ? storedOverrides(mapping.data) : {}), [mapping.data]);
  const overrides = edits ?? stored;
  const dirty = edits !== null;

  const save = useMutation({
    mutationFn: (input: readonly PlanTableMappingInput[]) =>
      plansApi.save(planId, { displayName: null, operatorNote: null, ifMatch: mapping.data?.eTag ?? '', selectionId: null, sourceConnectionId: null, targetConnectionId: null, mappings: input }, authentication),
    onSuccess: async () => {
      setEdits(null);
      await Promise.all([
        queryClient.invalidateQueries({ queryKey: queryKeys.planMapping(planId) }),
        queryClient.invalidateQueries({ queryKey: queryKeys.planReview(planId) }),
        queryClient.invalidateQueries({ queryKey: queryKeys.plan(planId) }),
      ]);
      toast.success('Mapping saved', sealed ? 'The plan must be sealed again before it can start.' : 'Seal the plan to apply it.');
    },
    onError: (error) => toast.error('Unable to save the mapping', describeError(error)),
  });

  const counts = useMemo(() => problemCounts(mapping.data ? [...mapping.data.problems, ...mapping.data.tables.flatMap(allProblems)] : []), [mapping.data]);

  if (mapping.isPending) return <Skeleton className="h-40" />;
  if (mapping.isError) {
    return (
      <Alert title="Unable to load the column mapping" tone="danger">
        {describeError(mapping.error)}
      </Alert>
    );
  }
  const data = mapping.data;
  if (data.tables.length === 0) {
    return (
      <Card padded={false}>
        <EmptyState
          description={data.problems[0]?.message ?? 'Associate a selection with a schema snapshot; the mapping is derived from its root table.'}
          icon={<Icons.Table size={22} />}
          title="Nothing to map yet"
        />
      </Card>
    );
  }
  const canEdit = hasPermission('Plans.Write');

  function choose(table: PlanMappingTable, column: PlanMappingColumn, value: string) {
    const key = tableKey(table.source);
    const next: Record<string, Choice> = { ...overrides[key] };
    const defaultTarget = table.targetColumns.find((candidate) => candidate.toLowerCase() === column.source.toLowerCase()) ?? null;
    const chosen: Choice = value === NONE ? null : value;
    if (chosen === defaultTarget) delete next[column.source];
    else next[column.source] = chosen;
    setEdits({ ...overrides, [key]: next });
  }

  function reset(table: PlanMappingTable) {
    const next = { ...overrides };
    delete next[tableKey(table.source)];
    setEdits(next);
  }

  return (
    <div className="space-y-4">
      {data.problems.length > 0 ? (
        <Alert title="Mapping is unchecked" tone="warning">
          <ul className="list-disc pl-4">
            {data.problems.map((problem, index) => (
              <li key={`${problem.code}:${index}`}>{problem.message}</li>
            ))}
          </ul>
        </Alert>
      ) : null}
      <Card padded={false}>
        <div className="flex flex-wrap items-center justify-between gap-3 border-b border-border px-4 py-3">
          <div>
            <div className="text-[13px] font-semibold text-fg">Column mapping</div>
            <div className="text-xs text-fg-muted">Prefilled by name from the source and target schemas. Change only what differs; problems are raised here, before the transfer.</div>
          </div>
          <div className="flex items-center gap-2">
            {counts.blockers > 0 ? <Badge tone="danger">{counts.blockers} blocking</Badge> : null}
            {counts.warnings > 0 ? <Badge tone="warning">{counts.warnings} warnings</Badge> : null}
            {counts.blockers === 0 && counts.warnings === 0 ? <Badge tone="success">No problems</Badge> : null}
            {canEdit ? (
              <Button disabled={!dirty || save.isPending} icon={<Icons.Lock size={15} />} loading={save.isPending} onClick={() => save.mutate(toInput(data, overrides))} variant="primary">
                Save mapping
              </Button>
            ) : null}
          </div>
        </div>
        <ul className="divide-y divide-border">
          {data.tables.map((table) => {
            const key = tableKey(table.source);
            const open = expanded === key;
            const tableCounts = problemCounts(allProblems(table));
            const chosen = overrides[key] ?? {};
            const changed = Object.keys(chosen).length;
            return (
              <li key={key}>
                <button className="grid w-full grid-cols-[1fr_auto] items-center gap-3 px-4 py-3 text-left hover:bg-surface-2" onClick={() => setExpanded(open ? null : key)} type="button">
                  <span className="min-w-0">
                    <span className="flex flex-wrap items-center gap-2">
                      <span className="font-mono text-[13px] font-semibold text-fg">{key}</span>
                      {table.isRoot ? <Badge className="!h-5 !px-1.5 !text-[10px]" tone="accent">Root</Badge> : null}
                      {key !== tableKey(table.target) ? <span className="text-xs text-fg-muted">→ {tableKey(table.target)}</span> : null}
                      {!table.targetExists ? <Badge className="!h-5 !px-1.5 !text-[10px]" tone="neutral">unchecked</Badge> : null}
                      {changed > 0 ? <Badge className="!h-5 !px-1.5 !text-[10px]" tone="info">{changed} customised</Badge> : null}
                    </span>
                    <span className="mt-1 block text-xs text-fg-faint">
                      {table.columns.length} source columns · {table.columns.filter((column) => column.target !== null).length} written
                      {table.targetOnlyColumns.length > 0 ? ` · ${table.targetOnlyColumns.length} target-only` : ''}
                    </span>
                  </span>
                  <span className="flex items-center gap-2">
                    {tableCounts.blockers > 0 ? <Badge tone="danger">{tableCounts.blockers}</Badge> : null}
                    {tableCounts.warnings > 0 ? <Badge tone="warning">{tableCounts.warnings}</Badge> : null}
                  </span>
                </button>
                {open ? (
                  <div className="border-t border-border bg-surface-2 px-4 py-3">
                    {table.problems.length > 0 ? (
                      <ul className="mb-3 space-y-1 text-xs">
                        {table.problems.map((problem, index) => (
                          <ProblemLine key={`${problem.code}:${index}`} problem={problem} />
                        ))}
                      </ul>
                    ) : null}
                    <div className="overflow-x-auto">
                      <table className="w-full text-[12.5px]">
                        <thead>
                          <tr className="text-left text-xs text-fg-muted">
                            <th className="py-1 pr-3 font-medium">Source column</th>
                            <th className="py-1 pr-3 font-medium">Target column</th>
                            <th className="py-1 font-medium">Problems</th>
                          </tr>
                        </thead>
                        <tbody>
                          {table.columns.map((column) => {
                            const value = column.source in chosen ? (chosen[column.source] ?? NONE) : (column.target ?? NONE);
                            const locked = !canEdit || column.isKey;
                            return (
                              <tr className="border-t border-border/60 align-top" key={column.source}>
                                <td className="py-1.5 pr-3">
                                  <span className="font-mono text-fg">{column.source}</span>
                                  <span className="ml-2 text-xs text-fg-faint">{column.sourceType}{column.sourceNullable ? ' null' : ''}</span>
                                  {column.isKey ? <Badge className="ml-2 !h-5 !px-1.5 !text-[10px]" tone="accent">key</Badge> : null}
                                  {column.isForeignKey ? <Badge className="ml-2 !h-5 !px-1.5 !text-[10px]" tone="info">FK</Badge> : null}
                                </td>
                                <td className="py-1.5 pr-3">
                                  <Select aria-label={`Target column for ${column.source}`} className="min-w-48" disabled={locked} onChange={(event) => choose(table, column, event.target.value)} value={value}>
                                    <option value={NONE}>— do not transfer —</option>
                                    {(value !== NONE && !table.targetColumns.includes(value) ? [value, ...table.targetColumns] : table.targetColumns).map((candidate) => (
                                      <option key={candidate} value={candidate}>
                                        {candidate}
                                      </option>
                                    ))}
                                  </Select>
                                  {column.targetType ? <span className="mt-1 block text-xs text-fg-faint">{column.targetType}{column.targetNullable ? ' null' : ''}</span> : null}
                                </td>
                                <td className="py-1.5">
                                  {column.problems.length === 0 ? (
                                    <span className={cx('text-xs', column.origin === 'default' ? 'text-fg-faint' : 'text-fg-muted')}>{column.origin === 'default' ? 'matched by name' : column.origin}</span>
                                  ) : (
                                    <ul className="space-y-1 text-xs">
                                      {column.problems.map((problem, index) => (
                                        <ProblemLine key={`${problem.code}:${index}`} problem={problem} />
                                      ))}
                                    </ul>
                                  )}
                                </td>
                              </tr>
                            );
                          })}
                        </tbody>
                      </table>
                    </div>
                    {table.targetOnlyColumns.length > 0 ? (
                      <div className="mt-3">
                        <div className="mb-1 text-xs font-semibold text-fg-muted">Target columns without a source ({table.targetOnlyColumns.length})</div>
                        <ul className="flex flex-wrap gap-1.5">
                          {table.targetOnlyColumns.map((column) => (
                            <li className={cx('rounded-md px-2 py-0.5 font-mono text-[11.5px]', column.problems.length > 0 ? 'bg-warning/10 text-fg' : 'bg-surface text-fg-muted')} key={column.name} title={column.problems[0]?.message}>
                              {column.name}
                              <span className="text-fg-faint"> {column.type}{column.isNullable ? ' null' : ''}</span>
                            </li>
                          ))}
                        </ul>
                      </div>
                    ) : null}
                    {canEdit && changed > 0 ? (
                      <div className="mt-3">
                        <Button icon={<Icons.Refresh size={14} />} onClick={() => reset(table)}>
                          Reset this table to defaults
                        </Button>
                      </div>
                    ) : null}
                  </div>
                ) : null}
              </li>
            );
          })}
        </ul>
      </Card>
    </div>
  );
}

function ProblemLine({ problem }: Readonly<{ problem: MappingProblem }>) {
  return (
    <li className={cx('flex items-start gap-1.5', problem.isBlocker ? 'text-danger' : 'text-warning')}>
      <Icons.Alert className="mt-0.5 shrink-0" size={12} />
      <span>{problem.message}</span>
    </li>
  );
}
