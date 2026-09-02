import { expect, it } from 'vitest';
import { render, screen } from '@testing-library/react';
import { createDevelopmentAuthenticationAdapter } from './authAdapter';
import { ProtectedAction } from './ProtectedAction';

it('keeps a development token in the adapter closure until sign-out', async () => {
  const adapter = createDevelopmentAuthenticationAdapter(
    { subjectId: 'operator-1', tenantId: 'tenant-1' }, 'development-token',
  );
  await expect(adapter.getAccessToken()).resolves.toBe('development-token');
  await adapter.signOut();
  await expect(adapter.getPrincipal()).resolves.toBeNull();
  await expect(adapter.getAccessToken()).resolves.toBeNull();
});

it('never touches browser storage or the URL with its token', async () => {
  const originalStorage = Object.getOwnPropertyDescriptor(window, 'localStorage');
  Object.defineProperty(window, 'localStorage', {
    configurable: true,
    get: () => { throw new Error('Authentication must not access localStorage.'); },
  });
  try {
    const adapter = createDevelopmentAuthenticationAdapter(
      { subjectId: 'operator-1', tenantId: 'tenant-1' }, 'development-token',
    );
    await expect(adapter.getAccessToken()).resolves.toBe('development-token');
    expect(window.location.href).not.toContain('development-token');
  } finally {
    if (originalStorage) Object.defineProperty(window, 'localStorage', originalStorage);
  }
});

it('hides denied actions and disables permitted actions with unmet prerequisites', () => {
  const { rerender } = render(
    <ProtectedAction requiredPermission="Transfers.Start" grantedPermissions={new Set()} prerequisiteMet={false} reason="Plan must be sealed">
      Start transfer
    </ProtectedAction>,
  );
  expect(screen.queryByRole('button', { name: 'Start transfer' })).not.toBeInTheDocument();
  rerender(
    <ProtectedAction requiredPermission="Transfers.Start" grantedPermissions={new Set(['Transfers.Start'])} prerequisiteMet={false} reason="Plan must be sealed">
      Start transfer
    </ProtectedAction>,
  );
  expect(screen.getByRole('button', { name: 'Start transfer' })).toBeDisabled();
  expect(screen.getByText('Plan must be sealed')).toBeVisible();
  rerender(
    <ProtectedAction requiredPermission="Transfers.Start" grantedPermissions={new Set(['Transfers.Start'])} prerequisiteMet={true} reason="Plan must be sealed">
      Start transfer
    </ProtectedAction>,
  );
  expect(screen.getByRole('button', { name: 'Start transfer' })).toBeEnabled();
});
