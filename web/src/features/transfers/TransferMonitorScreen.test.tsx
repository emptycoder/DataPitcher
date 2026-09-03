import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, render, screen } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { AppProviders } from '../../app/AppProviders';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { TransferMonitorScreen } from './TransferMonitorScreen';

afterEach(cleanup);

it('renders verification failure as failure after live progress arrives', async () => {
  const job = { jobId: '22222222-2222-4222-8222-222222222222', planId: '11111111-1111-4111-8111-111111111111', state: 'running', rowsTransferred: 3, bytesTransferred: 1024 };
  const request = vi.fn()
    .mockResolvedValueOnce(new Response(JSON.stringify(job), { status: 200 }))
    .mockResolvedValueOnce(new Response(new ReadableStream({ start(controller) { controller.enqueue(new TextEncoder().encode('id: 1\ndata: {"State":"verificationfailed","RowsTransferred":3,"BytesTransferred":1024}\n\n')); controller.close(); } }), { status: 200 }));
  const scheduler = { setTimeout: vi.fn(() => 1), clearTimeout: vi.fn() };

  render(<AppProviders client={new QueryClient()}><TransferMonitorScreen jobId={job.jobId} request={request} authentication={createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token')} clock={() => 1_000} scheduler={scheduler} /></AppProviders>);

  expect(await screen.findByText('Verification failed')).toBeVisible();
  expect(screen.queryByText('Transfer succeeded')).toBeNull();
  expect(screen.getByLabelText('Rows transferred')).toHaveTextContent('3');
  expect(screen.getByRole('heading', { name: 'Per-table progress' })).toBeVisible();
});
