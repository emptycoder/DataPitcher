import { useMutation, useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { HttpError } from '../../api/http';
import type { AuthenticationAdapter } from '../../auth/authAdapter';
import { usePermissions } from '../../auth/permissions';
import { sessionActions, useSourceConnectionId, useTargetConnectionId } from '../../stores/sessionStore';
import { Button, DataTable, Field, InlineError, LoadingIndicator, StatusBadge, TextInput } from '../../ui';
import { createConnection, fetchConnections, queueConnectionCheck, queueSchemaScan, type Connection, type CreateConnectionRequest, type RequestFunction } from './connectionsApi';

export type ConnectionsScreenProps = Readonly<{ request: RequestFunction; authentication: AuthenticationAdapter }>;

const connectionsQueryKey = ['connections'] as const;

type FormErrors = Readonly<{ displayName?: string; credentialId?: string }>;

function requestErrorMessage(error: unknown, fallback: string) {
  if (!(error instanceof HttpError)) return fallback;
  if (error.status === 401) return 'Sign in to manage connections.';
  if (error.status === 403) return 'You do not have permission to manage connections.';
  if (error.status === 404) return 'The connection was not found. Refresh and try again.';
  if (error.status >= 500) return 'Connection service is unavailable. Try again.';
  return fallback;
}

function serverFieldError(error: unknown, field: string) {
  if (!(error instanceof HttpError) || error.status !== 400) return undefined;
  const message = (error.problem as { errors?: Record<string, unknown> } | null)?.errors?.[field];
  return Array.isArray(message) && typeof message[0] === 'string' ? message[0] : undefined;
}

export function ConnectionsScreen({ request, authentication }: ConnectionsScreenProps) {
  const queryClient = useQueryClient();
  const { hasPermission } = usePermissions();
  const connections = useQuery({ queryKey: connectionsQueryKey, queryFn: ({ signal }) => fetchConnections(request, authentication, signal) });
  const sourceConnectionId = useSourceConnectionId();
  const targetConnectionId = useTargetConnectionId();
  const [displayName, setDisplayName] = useState('');
  const [credentialId, setCredentialId] = useState('');
  const [formErrors, setFormErrors] = useState<FormErrors>({});
  const canWriteConnections = hasPermission('Connections.Write');
  const canScanSchema = hasPermission('Schema.Write');
  const create = useMutation({
    mutationFn: (body: CreateConnectionRequest) => createConnection(body, request, authentication, new AbortController().signal),
    onSuccess: async () => {
      await queryClient.invalidateQueries({ queryKey: connectionsQueryKey });
      setDisplayName('');
      setCredentialId('');
      setFormErrors({});
    },
    onError: (error) => setFormErrors({ displayName: serverFieldError(error, 'DisplayName'), credentialId: serverFieldError(error, 'CredentialId') }),
  });
  const check = useMutation({
    mutationFn: (connectionId: string) => queueConnectionCheck(connectionId, request, authentication, new AbortController().signal),
    onSuccess: () => queryClient.invalidateQueries({ queryKey: connectionsQueryKey }),
  });
  const scan = useMutation({
    mutationFn: (connectionId: string) => queueSchemaScan(connectionId, request, authentication, new AbortController().signal),
  });

  function onCreate(event: FormEvent) {
    event.preventDefault();
    const errors = {
      displayName: displayName.trim() ? undefined : 'Display name is required.',
      credentialId: credentialId.trim() ? undefined : 'Credential ID is required.',
    };
    setFormErrors(errors);
    if (errors.displayName || errors.credentialId) return;
    create.mutate({ displayName, providerId: 'sqlserver', credentialId, ifMatch: '*' });
  }

  if (connections.isPending) return <LoadingIndicator label="Loading connections." />;
  if (connections.isError) return <section aria-label="Connections"><InlineError>{requestErrorMessage(connections.error, 'Unable to load connections.')}</InlineError></section>;
  const checkedConnection = check.isSuccess ? connections.data.find((connection) => connection.connectionId === check.variables) : undefined;
  const hasFieldErrors = formErrors.displayName !== undefined || formErrors.credentialId !== undefined;

  return (
    <section aria-label="Connections">
      <h2>Connections</h2>
      <DataTable>
        <caption>Registered connections</caption>
        <thead><tr><th scope="col">Name</th><th scope="col">Provider</th><th scope="col">Health</th><th scope="col">Actions</th></tr></thead>
        <tbody>
          {connections.data.map((connection) => (
            <ConnectionRow
              key={connection.connectionId}
              connection={connection}
              isSource={connection.connectionId === sourceConnectionId}
              isTarget={connection.connectionId === targetConnectionId}
              canWriteConnections={canWriteConnections}
              canScanSchema={canScanSchema}
              checking={check.isPending}
              scanning={scan.isPending}
              onSelectSource={() => sessionActions.setConnectionIds(connection.connectionId, targetConnectionId)}
              onSelectTarget={() => sessionActions.setConnectionIds(sourceConnectionId, connection.connectionId)}
              onCheckHealth={() => check.mutate(connection.connectionId)}
              onScanSchema={() => scan.mutate(connection.connectionId)}
            />
          ))}
        </tbody>
      </DataTable>
      {connections.data.length === 0 ? <p>No connections registered.</p> : null}
      {checkedConnection ? <p role="status">{`Health check result: ${checkedConnection.health}.`}</p> : null}
      {scan.isSuccess ? <p role="status">Schema scan queued. It is running in the background.</p> : null}
      {check.isError ? <InlineError>{requestErrorMessage(check.error, 'Unable to check connection health.')}</InlineError> : null}
      {scan.isError ? <InlineError>{requestErrorMessage(scan.error, 'Unable to start schema scan.')}</InlineError> : null}
      <form aria-label="Add connection" onSubmit={onCreate}>
        <h3>Add connection</h3>
        <Field label="Display name"><TextInput value={displayName} onChange={(event) => setDisplayName(event.target.value)} aria-invalid={formErrors.displayName !== undefined} /></Field>
        {formErrors.displayName ? <InlineError>{formErrors.displayName}</InlineError> : null}
        <Field label="Provider"><TextInput value="SQL Server" readOnly /></Field>
        <Field label="Credential ID"><TextInput type="password" value={credentialId} onChange={(event) => setCredentialId(event.target.value)} aria-invalid={formErrors.credentialId !== undefined} /></Field>
        {formErrors.credentialId ? <InlineError>{formErrors.credentialId}</InlineError> : null}
        {create.isError && !hasFieldErrors ? <InlineError>{requestErrorMessage(create.error, 'Unable to add connection.')}</InlineError> : null}
        <Button type="submit" disabled={create.isPending || !canWriteConnections}>Add connection</Button>
      </form>
    </section>
  );
}

type ConnectionRowProps = Readonly<{
  connection: Connection;
  isSource: boolean;
  isTarget: boolean;
  canWriteConnections: boolean;
  canScanSchema: boolean;
  checking: boolean;
  scanning: boolean;
  onSelectSource: () => void;
  onSelectTarget: () => void;
  onCheckHealth: () => void;
  onScanSchema: () => void;
}>;

function ConnectionRow({ connection, isSource, isTarget, canWriteConnections, canScanSchema, checking, scanning, onSelectSource, onSelectTarget, onCheckHealth, onScanSchema }: ConnectionRowProps) {
  return (
    <tr>
      <td>{connection.displayName}</td>
      <td>{connection.providerId}</td>
      <td><StatusBadge state={connection.health} /></td>
      <td>
        <Button aria-pressed={isSource} disabled={!canWriteConnections} onClick={onSelectSource}>Use as source</Button>
        <Button aria-pressed={isTarget} disabled={!canWriteConnections} onClick={onSelectTarget}>Use as target</Button>
        <Button disabled={checking || !canWriteConnections} onClick={onCheckHealth}>Check health</Button>
        <Button disabled={scanning || !canScanSchema} onClick={onScanSchema}>Scan schema</Button>
      </td>
    </tr>
  );
}
