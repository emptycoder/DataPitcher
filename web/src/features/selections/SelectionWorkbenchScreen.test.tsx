import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { AppProviders } from '../../app/AppProviders';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { SelectionWorkbenchScreen } from './SelectionWorkbenchScreen';

afterEach(cleanup);

it('loads the schema and mounts the selection workbench with a selectable root table', async () => {
  const request = vi.fn(async (_input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify({
    schemaRevision: 'schema-1',
    foreignKeys: [],
    tables: [
      { tableId: 'sales.orders', schemaName: 'sales', tableName: 'Orders', approximateRowCount: 4, stableKeyColumns: ['id'], columns: [{ name: 'id', valueKind: 'int' }] },
      { tableId: 'sales.customers', schemaName: 'sales', tableName: 'Customers', approximateRowCount: 3, stableKeyColumns: ['id'], columns: [{ name: 'id', valueKind: 'int' }] },
    ],
  }), { status: 200 }));

  render(<AppProviders client={new QueryClient()}><SelectionWorkbenchScreen request={request} authentication={createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token')} /></AppProviders>);

  expect(await screen.findByRole('region', { name: 'Selection editor' })).toBeVisible();
  fireEvent.click(screen.getAllByRole('button', { name: 'Select root' })[1]!);
  expect(screen.getByRole('heading', { name: 'sales.Customers' })).toBeVisible();
});
