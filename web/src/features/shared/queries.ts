import { useQuery } from '@tanstack/react-query';
import { connectionsApi } from '../../api/connections';
import { isActive, jobsApi } from '../../api/jobs';
import { queryKeys } from '../../api/keys';
import { plansApi } from '../../api/plans';
import { selectionsApi } from '../../api/selections';
import { useAuth } from '../../auth/AuthContext';

export function useConnections() {
  const { authentication } = useAuth();
  return useQuery({ queryKey: queryKeys.connections, queryFn: ({ signal }) => connectionsApi.list(authentication, signal) });
}

export function useProviders() {
  return useQuery({ queryKey: queryKeys.providers, queryFn: ({ signal }) => connectionsApi.providers(signal), staleTime: Infinity });
}

/** Stored settings of a connection minus the password; only fetched while an edit dialog is open. */
export function useConnectionDetails(connectionId: string | null) {
  const { authentication } = useAuth();
  return useQuery({
    queryKey: queryKeys.connectionDetails(connectionId ?? ''),
    queryFn: ({ signal }) => connectionsApi.details(connectionId!, authentication, signal),
    enabled: connectionId !== null && connectionId !== '',
    retry: false,
    staleTime: 0,
    gcTime: 0,
  });
}

export function useSnapshots(connectionId: string | null) {
  const { authentication } = useAuth();
  return useQuery({
    queryKey: queryKeys.snapshots(connectionId ?? ''),
    queryFn: ({ signal }) => connectionsApi.snapshots(connectionId!, authentication, signal),
    enabled: connectionId !== null && connectionId !== '',
  });
}

export function useSnapshot(connectionId: string | null, snapshotId: string | null) {
  const { authentication } = useAuth();
  return useQuery({
    queryKey: queryKeys.snapshot(connectionId ?? '', snapshotId ?? ''),
    queryFn: ({ signal }) => connectionsApi.snapshot(connectionId!, snapshotId!, authentication, signal),
    enabled: Boolean(connectionId && snapshotId),
    staleTime: 5 * 60_000,
  });
}

export function useSelections() {
  const { authentication } = useAuth();
  return useQuery({ queryKey: queryKeys.selections, queryFn: ({ signal }) => selectionsApi.list(authentication, signal) });
}

/** A saved selection read back for editing. */
export function useSelection(selectionId: string | null) {
  const { authentication } = useAuth();
  return useQuery({
    queryKey: queryKeys.selection(selectionId ?? ''),
    queryFn: ({ signal }) => selectionsApi.get(selectionId!, authentication, signal),
    enabled: Boolean(selectionId),
    retry: false,
  });
}

/** The editable plan record (name, note, associations) straight from the API. */
export function usePlan(planId: string | null) {
  const { authentication } = useAuth();
  return useQuery({
    queryKey: queryKeys.plan(planId ?? ''),
    queryFn: ({ signal }) => plansApi.get(planId!, authentication, signal),
    enabled: Boolean(planId),
    retry: false,
  });
}

export function usePlanReview(planId: string | null, options: Readonly<{ enabled?: boolean; refetchIntervalMs?: number }> = {}) {
  const { authentication } = useAuth();
  return useQuery({
    queryKey: queryKeys.planReview(planId ?? ''),
    queryFn: ({ signal }) => plansApi.review(planId!, authentication, signal),
    enabled: Boolean(planId) && (options.enabled ?? true),
    retry: false,
    refetchInterval: options.refetchIntervalMs,
  });
}

export function useJobs(options: Readonly<{ live?: boolean }> = {}) {
  const { authentication } = useAuth();
  return useQuery({
    queryKey: queryKeys.jobs,
    queryFn: ({ signal }) => jobsApi.list(authentication, signal),
    refetchInterval: (query) => {
      if (options.live === false) return false;
      const jobs = query.state.data;
      return jobs?.some((job) => isActive(job.state)) ? 2_000 : 15_000;
    },
  });
}

export function useJob(jobId: string | null, options: Readonly<{ refetchIntervalMs?: number | false }> = {}) {
  const { authentication } = useAuth();
  return useQuery({
    queryKey: queryKeys.job(jobId ?? ''),
    queryFn: ({ signal }) => jobsApi.get(jobId!, authentication, signal),
    enabled: Boolean(jobId),
    retry: false,
    refetchInterval: options.refetchIntervalMs ?? false,
  });
}
