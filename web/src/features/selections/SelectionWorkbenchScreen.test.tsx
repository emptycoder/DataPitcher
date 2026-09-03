import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { AppProviders } from '../../app/AppProviders';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { HttpError } from '../../api/http';
import { SelectionWorkbenchScreen, selectionErrorMessage } from './SelectionWorkbenchScreen';

afterEach(() => {
  cleanup();
  vi.unstubAllGlobals();
});

const authentication = createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token');

function renderWorkbench() {
  return render(<AppProviders client={new QueryClient()}><SelectionWorkbenchScreen authentication={authentication} /></AppProviders>);
}

function emptyResponse(input: RequestInfo | URL) {
  return new Response(JSON.stringify(String(input) === '/api/connections' ? [] : { selections: [] }));
}

it('saves raw SQL with snapshot-selected row identity and refreshes the saved list', async () => {
  const created = { selectionId: 'selection-1', displayName: 'Orders to move', version: 1, eTag: '"1"', mode: 'raw', warnings: [] };
  let saved: typeof created[] = [];
  const fetch = vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const url = String(input);
    if (url === '/api/connections') return new Response(JSON.stringify([{ connectionId: 'connection-1', displayName: 'Source', providerId: 'postgres', health: 'Healthy', eTag: '"1"' }]));
    if (url === '/api/connections/connection-1/snapshots') return new Response(JSON.stringify([{ snapshotId: 'snapshot-1', hash: 'schema-1', capturedAtUtc: '2026-09-03T00:00:00Z' }]));
    if (url === '/api/connections/connection-1/snapshots/snapshot-1') return new Response(JSON.stringify({ connectionId: 'connection-1', snapshotId: 'snapshot-1', hash: 'schema-1', capturedAtUtc: '2026-09-03T00:00:00Z', tables: [{ schema: 'sales', name: 'Orders', columns: [{ name: 'tenant_id', storeType: 'uuid', isNullable: false }, { name: 'order_id', storeType: 'uuid', isNullable: false }], primaryKey: { name: 'Orders_PK', columns: ['tenant_id', 'order_id'] } }], foreignKeys: [] }));
    if (url === '/api/selections/save' && init?.method === 'POST') {
      saved = [created];
      return new Response(JSON.stringify(created));
    }
    if (url === '/api/selections') return new Response(JSON.stringify({ selections: saved }));
    throw new Error(`Unexpected request: ${url}`);
  });
  vi.stubGlobal('fetch', fetch);

  renderWorkbench();

  expect(await screen.findByText('No saved selections.')).toBeVisible();
  fireEvent.change(await screen.findByRole('combobox', { name: 'Connection' }), { target: { value: 'connection-1' } });
  await screen.findByRole('option', { name: 'schema-1 (2026-09-03T00:00:00Z)' });
  fireEvent.change(await screen.findByRole('combobox', { name: 'Snapshot' }), { target: { value: 'snapshot-1' } });
  await screen.findByRole('option', { name: 'sales.Orders' });
  const rootTable = await screen.findByRole('combobox', { name: 'Root table' });
  fireEvent.change(rootTable, { target: { value: 'sales.Orders' } });
  expect(screen.getByText('Stable key constraint: Orders_PK')).toBeVisible();
  fireEvent.change(rootTable, { target: { value: '' } });
  fireEvent.change(rootTable, { target: { value: 'sales.Orders' } });
  fireEvent.click(screen.getByRole('button', { name: 'Move order_id earlier' }));
  fireEvent.click(screen.getByRole('button', { name: 'Move order_id later' }));
  fireEvent.click(screen.getByRole('button', { name: 'Move order_id earlier' }));
  fireEvent.change(screen.getByRole('textbox', { name: 'Raw SQL' }), { target: { value: 'select * from sales.orders' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save selection' }));

  expect(await screen.findByText('Selection saved.')).toBeVisible();
  expect(screen.getByText('Orders to move')).toBeVisible();
  const save = fetch.mock.calls.find(([url, init]) => url === '/api/selections/save' && init?.method === 'POST');
  expect(JSON.parse(String(save?.[1]?.body))).toMatchObject({ mode: 'raw', rawSql: 'select * from sales.orders', schemaRevision: 'schema-1', connectionId: 'connection-1', snapshotId: 'snapshot-1', rootSchema: 'sales', rootTable: 'Orders', stableKeyConstraintName: 'Orders_PK', stableKeyColumns: ['order_id', 'tenant_id'] });
});

it('rejects empty raw SQL before saving', async () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => emptyResponse(input)));
  renderWorkbench();

  fireEvent.click(screen.getByRole('button', { name: 'Save selection' }));

  expect(await screen.findByRole('alert')).toHaveTextContent('Enter raw SQL before saving.');
});

it('surfaces a save rejection when the server reports missing row identity', async () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    if (String(input) === '/api/selections/save' && init?.method === 'POST') return new Response(JSON.stringify({ title: 'Selection root table and stable key must be specified.' }), { status: 400 });
    return emptyResponse(input);
  }));
  renderWorkbench();

  fireEvent.change(screen.getByRole('textbox', { name: 'Raw SQL' }), { target: { value: 'select 1' } });
  fireEvent.click(screen.getByRole('button', { name: 'Save selection' }));

  expect(await screen.findByRole('alert')).toHaveTextContent('Choose a snapshot root table and stable key before saving.');
});

it('presents preview as temporarily unavailable', () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => emptyResponse(input)));
  renderWorkbench();

  expect(screen.getByText('Preview is temporarily unavailable.')).toBeVisible();
});

it('presents count as temporarily unavailable', () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => emptyResponse(input)));
  renderWorkbench();

  expect(screen.getByText('Counting seed rows is temporarily unavailable.')).toBeVisible();
});

it('surfaces a saved-list request failure', async () => {
  vi.stubGlobal('fetch', vi.fn(async (input: RequestInfo | URL) => String(input) === '/api/selections'
    ? new Response(JSON.stringify({ title: 'Unavailable' }), { status: 500 })
    : emptyResponse(input)));
  renderWorkbench();

  expect(await screen.findByRole('alert')).toHaveTextContent('The selection service is temporarily unavailable.');
});

it('distinguishes HTTP failures from save requests', () => {
  expect(selectionErrorMessage(new HttpError(400, null))).toBe('Choose a snapshot root table and stable key before saving.');
  expect(selectionErrorMessage(new HttpError(401, null))).toBe('Sign in to save selections.');
  expect(selectionErrorMessage(new HttpError(403, null))).toBe('You do not have permission to save selections.');
  expect(selectionErrorMessage(new HttpError(404, null))).toBe('The selected connection or schema snapshot was not found.');
  expect(selectionErrorMessage(new HttpError(500, null))).toBe('The selection service is temporarily unavailable.');
  expect(selectionErrorMessage(new HttpError(409, null))).toBe('Unable to save selection.');
  expect(selectionErrorMessage(new Error())).toBe('Unable to save selection.');
});
