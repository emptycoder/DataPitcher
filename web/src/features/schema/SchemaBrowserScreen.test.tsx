import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { AppProviders } from '../../app/AppProviders';
import { routes } from '../../app/routes';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { PermissionsProvider } from '../../auth/permissions';
import { SchemaBrowserScreen } from './SchemaBrowserScreen';

const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token');
const connection = { connectionId: '11111111-1111-4111-8111-111111111111', displayName: 'Warehouse', providerId: 'sqlserver', health: 'Healthy', eTag: 'etag-1' };
const snapshotId = '22222222-2222-4222-8222-222222222222';
const summary = { snapshotId, hash: 'snapshot-hash', capturedAtUtc: '2026-09-03T10:00:00Z' };
const snapshot = {
  connectionId: connection.connectionId,
  snapshotId,
  hash: summary.hash,
  capturedAtUtc: summary.capturedAtUtc,
  tables: [
    { schema: 'sales', name: 'Orders', columns: [{ name: 'Id', storeType: 'bigint', isNullable: false }, { name: 'CustomerId', storeType: 'uniqueidentifier', isNullable: true }], primaryKey: { name: 'PK_Orders', columns: ['Id'] } },
    { schema: 'sales', name: 'Customers', columns: [{ name: 'Id', storeType: 'uniqueidentifier', isNullable: false }], primaryKey: { name: 'PK_Customers', columns: ['Id'] } },
    { schema: 'audit', name: 'Entries', columns: [{ name: 'Event', storeType: 'nvarchar(100)', isNullable: false }], primaryKey: null },
  ],
  foreignKeys: [
    { name: 'FK_Orders_Customers', childTable: { schema: 'sales', name: 'Orders' }, parentTable: { schema: 'sales', name: 'Customers' }, childColumns: ['CustomerId'], parentColumns: ['Id'], isEnforced: true, isTrusted: false },
    { name: 'FK_Orders_Entries', childTable: { schema: 'sales', name: 'Orders' }, parentTable: { schema: 'audit', name: 'Entries' }, childColumns: ['Id'], parentColumns: ['OrderId'], isEnforced: false, isTrusted: true },
  ],
};

function response(body: unknown, status = 200) {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
}

function renderSchemaBrowser(request: typeof fetch, withPermissions = false) {
  vi.stubGlobal('fetch', request);
  const content = <SchemaBrowserScreen authentication={authentication} />;
  return render(<AppProviders client={new QueryClient({ defaultOptions: { queries: { retry: false } } })}>{withPermissions ? <PermissionsProvider authentication={authentication}>{content}</PermissionsProvider> : content}</AppProviders>);
}

function chooseConnection() {
  fireEvent.change(screen.getByLabelText('Connection'), { target: { value: connection.connectionId } });
}

function chooseSnapshot() {
  fireEvent.change(screen.getByLabelText('Snapshot'), { target: { value: snapshotId } });
}

afterEach(() => {
  cleanup();
  vi.restoreAllMocks();
  vi.unstubAllGlobals();
});

it('schema browser shows loading while connections are loading', async () => {
  let resolve!: (value: Response) => void;
  const request = vi.fn(() => new Promise<Response>((done) => { resolve = done; }));
  renderSchemaBrowser(request);

  expect(screen.getByRole('status')).toHaveTextContent('Loading connections.');
  await waitFor(() => expect(request).toHaveBeenCalledOnce());
  resolve(response([]));
  expect(await screen.findByText('No connections are available.')).toBeVisible();
});

it('schema browser tells the user to run a schema scan when no snapshots exist', async () => {
  let resolve!: (value: Response) => void;
  renderSchemaBrowser(vi.fn((input) => String(input).endsWith('/snapshots') ? new Promise<Response>((done) => { resolve = done; }) : Promise.resolve(response([connection]))));

  await screen.findByLabelText('Connection');
  chooseConnection();
  expect(await screen.findByText('Loading snapshots.')).toBeVisible();
  resolve(response([]));
  expect(await screen.findByText('No schema snapshots exist for this connection. Run a schema scan, then return here.')).toBeVisible();
  expect(screen.getByRole('link', { name: 'Run a schema scan from Connections.' })).toHaveAttribute('href', '/connections');
});

it.each([
  [401, 'Sign in to browse the schema.'],
  [403, 'You do not have permission to browse this schema.'],
  [404, 'The requested connection or schema snapshot was not found.'],
  [500, 'The schema service is unavailable. Try again.'],
  [400, 'Unable to load the schema.'],
])('schema browser identifies a %i schema response', async (status, message) => {
  renderSchemaBrowser(vi.fn(() => Promise.resolve(response({ detail: 'offline' }, status))));

  expect(await screen.findByRole('alert')).toHaveTextContent(message);
});

it('schema browser reports an unavailable snapshot list', async () => {
  renderSchemaBrowser(vi.fn((input) => Promise.resolve(String(input).endsWith('/snapshots') ? response({}, 500) : response([connection]))));

  await screen.findByLabelText('Connection');
  chooseConnection();
  expect(await screen.findByRole('alert')).toHaveTextContent('The schema service is unavailable. Try again.');
});

it('schema browser reports a missing selected snapshot', async () => {
  renderSchemaBrowser(vi.fn((input) => {
    const url = String(input);
    return Promise.resolve(url.endsWith(`/snapshots/${snapshotId}`) ? response({}, 404) : url.endsWith('/snapshots') ? response([summary]) : response([connection]));
  }));

  await screen.findByLabelText('Connection');
  chooseConnection();
  await screen.findByLabelText('Snapshot');
  chooseSnapshot();
  expect(await screen.findByRole('alert')).toHaveTextContent('The requested connection or schema snapshot was not found.');
});

it('schema browser reports an unexpected loading failure', async () => {
  renderSchemaBrowser(vi.fn(() => Promise.reject(new Error('offline'))));

  expect(await screen.findByRole('alert')).toHaveTextContent('Unable to load the schema.');
});

it('schema browser identifies a snapshot without tables', async () => {
  renderSchemaBrowser(vi.fn((input) => {
    const url = String(input);
    return Promise.resolve(url.endsWith(`/snapshots/${snapshotId}`) ? response({ ...snapshot, tables: [] }) : url.endsWith('/snapshots') ? response([summary]) : response([connection]));
  }));

  await screen.findByLabelText('Connection');
  chooseConnection();
  await screen.findByLabelText('Snapshot');
  chooseSnapshot();
  expect(await screen.findByText('This snapshot has no tables.')).toBeVisible();
});

it('schema browser selects tables, shows foreign key caveats, and filters without locale rules', async () => {
  renderSchemaBrowser(vi.fn((input) => {
    const url = String(input);
    return Promise.resolve(url.endsWith(`/snapshots/${snapshotId}`) ? response(snapshot) : url.endsWith('/snapshots') ? response([summary]) : response([connection]));
  }));

  await screen.findByLabelText('Connection');
  chooseConnection();
  await screen.findByLabelText('Snapshot');
  chooseSnapshot();
  fireEvent.click(await screen.findByRole('button', { name: 'sales.Orders' }));

  expect(screen.getByText('PK_Orders (Id)')).toBeVisible();
  expect(screen.getByText('FK_Orders_Customers')).toBeVisible();
  expect(screen.getByText('sales.Customers (CustomerId → Id)')).toBeVisible();
  expect(screen.getByText('Enforced')).toBeVisible();
  expect(screen.getByText('Not trusted')).toBeVisible();
  expect(screen.getByText('Not enforced')).toBeVisible();
  expect(screen.getByText('Trusted')).toBeVisible();
  expect(screen.getByRole('cell', { name: 'bigint' })).toBeVisible();
  expect(screen.getByRole('cell', { name: 'Not nullable' })).toBeVisible();

  fireEvent.change(screen.getByRole('searchbox', { name: 'Filter tables' }), { target: { value: 'orders' } });
  expect(screen.getByRole('button', { name: 'sales.Orders' })).toBeVisible();
  expect(screen.queryByRole('button', { name: 'sales.Customers' })).toBeNull();
  fireEvent.change(screen.getByRole('searchbox', { name: 'Filter tables' }), { target: { value: 'none' } });
  expect(screen.getByText('No tables match "none".')).toBeVisible();
  fireEvent.change(screen.getByRole('searchbox', { name: 'Filter tables' }), { target: { value: '' } });
  fireEvent.click(screen.getByRole('button', { name: 'sales.Customers' }));
  expect(screen.getByText('No foreign keys from this table.')).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'audit.Entries' }));
  expect(screen.getByText('No primary key.')).toBeVisible();
});

it('schema browser stops verified users without schema permission before loading', async () => {
  const request = vi.fn((input) => Promise.resolve(String(input).endsWith('/effective-permissions') ? response({ principalId: 'operator-1', tenantId: 'tenant-1', permissions: [] }) : response([connection])));
  renderSchemaBrowser(request, true);

  expect(await screen.findByRole('alert')).toHaveTextContent('You do not have permission to browse schemas.');
});

it('schema browser allows verified readers', async () => {
  const request = vi.fn((input) => Promise.resolve(String(input).endsWith('/effective-permissions') ? response({ principalId: 'operator-1', tenantId: 'tenant-1', permissions: ['Schema.Read'] }) : response([connection])));
  renderSchemaBrowser(request, true);

  expect(await screen.findByLabelText('Connection')).toBeVisible();
});

it('schema browser registers its route', () => {
  expect(routes).toContainEqual(expect.objectContaining({ path: '/schema-browser', label: 'Schema browser' }));
});
