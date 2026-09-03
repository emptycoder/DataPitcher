import { afterEach, expect, it, vi } from 'vitest';
import { cleanup, fireEvent, render, screen } from '@testing-library/react';
import { QueryClient } from '@tanstack/react-query';
import { AppProviders } from '../../app/AppProviders';
import { createDevelopmentAuthenticationAdapter } from '../../auth/authAdapter';
import { sessionActions } from '../../stores/sessionStore';
import { ConnectionsScreen } from './ConnectionsScreen';

afterEach(() => {
  cleanup();
  sessionActions.setConnectionIds(null, null);
});

it('shows server-derived health and lets the operator select source and target connections', async () => {
  const connection = { connectionId: '11111111-1111-4111-8111-111111111111', displayName: 'Warehouse', providerId: 'postgresql', health: 'Healthy', eTag: 'etag-1' };
  const request = vi.fn(async (input: RequestInfo | URL, _init?: RequestInit) => new Response(JSON.stringify(String(input).endsWith('/checks')
    ? { operationId: '33333333-3333-4333-8333-333333333333', state: 'queued', statusUri: '/api/operations/33333333-3333-4333-8333-933333333333' }
    : [connection]), { status: String(input).endsWith('/checks') ? 202 : 200 }));

  render(<AppProviders client={new QueryClient()}><ConnectionsScreen request={request} authentication={createDevelopmentAuthenticationAdapter({ subjectId: 'operator-1', tenantId: 'tenant-1' }, 'memory-token')} /></AppProviders>);

  expect(await screen.findByText('Healthy')).toBeVisible();
  expect(screen.getByText(/server re-checks every connection before a transfer starts/i)).toBeVisible();
  fireEvent.click(screen.getByRole('button', { name: 'Use as source' }));
  expect(screen.getByRole('button', { name: 'Use as source' })).toHaveAttribute('aria-pressed', 'true');
  fireEvent.click(screen.getByRole('button', { name: 'Recheck health' }));
  await vi.waitFor(() => expect(request).toHaveBeenCalledWith(`/api/connections/${connection.connectionId}/checks`, expect.objectContaining({ method: 'POST' })));
});
