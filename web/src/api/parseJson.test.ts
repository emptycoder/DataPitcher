import { expect, it } from 'vitest';
import { ZodError } from 'zod';
import { EffectivePermissionsResponse } from './generated/permissions.zod';
import { parseJson } from './parseJson';

it('accepts a response matching the generated schema', async () => {
  const response = new Response(JSON.stringify({ principalId: 'operator-1', tenantId: 'tenant-1', permissions: ['Transfers.Start'] }), { status: 200 });
  await expect(parseJson(response, EffectivePermissionsResponse)).resolves.toEqual({ principalId: 'operator-1', tenantId: 'tenant-1', permissions: ['Transfers.Start'] });
});

it('rejects a malformed response before application state can receive it', async () => {
  const response = new Response(JSON.stringify({ permissions: ['Transfers.Start'] }), { status: 200 });
  await expect(parseJson(response, EffectivePermissionsResponse)).rejects.toBeInstanceOf(ZodError);
});

it('rejects an unsuccessful HTTP response before parsing', async () => {
  const response = new Response('', { status: 500 });
  await expect(parseJson(response, EffectivePermissionsResponse)).rejects.toThrow('Request failed: 500');
});
