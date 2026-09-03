import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { sessionActions, useSourceConnectionId, useTargetConnectionId } from '../../stores/sessionStore';
import { createConnection, fetchConnections, queueConnectionCheck, type Connection, type CreateConnectionRequest, type RequestFunction } from './connectionsApi';

export type ConnectionsScreenProps = Readonly<{ request: RequestFunction; authentication: AuthenticationAdapter }>;

const connectionsQueryKey = ['connections'] as const;

export function ConnectionsScreen({ request, authentication }: ConnectionsScreenProps) {
  const queryClient = useQueryClient();
  const connections = useQuery({ queryKey: connectionsQueryKey, queryFn: ({ signal }) => fetchConnections(request, authentication, signal) });
  const sourceConnectionId = useSourceConnectionId();
  const targetConnectionId = useTargetConnectionId();
  const [displayName, setDisplayName] = useState('');
  const [providerId, setProviderId] = useState('postgresql');
  const [credentialId, setCredentialId] = useState('');
  const create = useMutation({
    mutationFn: (body: CreateConnectionRequest) => createConnection(body, request, authentication, new AbortController().signal),
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: connectionsQueryKey }); setDisplayName(''); setCredentialId(''); },
  });
  const check = useMutation({
    mutationFn: (connectionId: string) => queueConnectionCheck(connectionId, request, authentication, new AbortController().signal),
    onSuccess: () => { void queryClient.invalidateQueries({ queryKey: connectionsQueryKey }); },
  });

  function onCreate(event: FormEvent) {
    event.preventDefault();
    create.mutate({ displayName, providerId, credentialId, ifMatch: '*' });
  }

  if (connections.isPending) return <p role="status">Loading connections.</p>;
  if (connections.isError) return <p role="status">Unable to load connections.</p>;

  return (
    <section aria-label="Connections">
      <p>Health shown here is client-observed and advisory only; the server re-checks every connection before a transfer starts.</p>
      <ul>
        {connections.data.map((connection) => (
          <ConnectionRow
            key={connection.connectionId}
            connection={connection}
            isSource={connection.connectionId === sourceConnectionId}
            isTarget={connection.connectionId === targetConnectionId}
            onSelectSource={() => sessionActions.setConnectionIds(connection.connectionId, targetConnectionId)}
            onSelectTarget={() => sessionActions.setConnectionIds(sourceConnectionId, connection.connectionId)}
            onCheckHealth={() => check.mutate(connection.connectionId)}
          />
        ))}
      </ul>
      <form aria-label="Add connection" onSubmit={onCreate}>
        <label>
          Display name
          <input value={displayName} onChange={(event) => setDisplayName(event.target.value)} required />
        </label>
        <label>
          Provider
          <select value={providerId} onChange={(event) => setProviderId(event.target.value)}>
            <option value="postgresql">PostgreSQL</option>
            <option value="sqlserver">SQL Server</option>
          </select>
        </label>
        <label>
          Credential id
          <input value={credentialId} onChange={(event) => setCredentialId(event.target.value)} required />
        </label>
        <button type="submit" disabled={create.isPending}>Add connection</button>
      </form>
    </section>
  );
}

type ConnectionRowProps = Readonly<{
  connection: Connection;
  isSource: boolean;
  isTarget: boolean;
  onSelectSource: () => void;
  onSelectTarget: () => void;
  onCheckHealth: () => void;
}>;

function ConnectionRow({ connection, isSource, isTarget, onSelectSource, onSelectTarget, onCheckHealth }: ConnectionRowProps) {
  return (
    <li>
      <span>{connection.displayName}</span>
      <span>{connection.providerId}</span>
      <span>{connection.health}</span>
      <button type="button" aria-pressed={isSource} onClick={onSelectSource}>Use as source</button>
      <button type="button" aria-pressed={isTarget} onClick={onSelectTarget}>Use as target</button>
      <button type="button" onClick={onCheckHealth}>Recheck health</button>
    </li>
  );
}
