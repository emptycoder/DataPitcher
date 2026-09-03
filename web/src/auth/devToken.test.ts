import { describe, expect, it } from 'vitest';
import { developmentAudience, developmentIssuer, mintDevelopmentToken } from './devToken';
import { decodeJwt, isExpired, rolesFromClaims } from './jwt';
import { permissionsForRoles } from './roles';

describe('development tokens', () => {
  it('mints an HS256 token the API can validate and the client can decode', async () => {
    const token = await mintDevelopmentToken({
      signingKey: 'local-development-signing-key-0123456789abcdef',
      subject: 'operator',
      roles: ['Planner'],
      issuer: developmentIssuer,
      audience: developmentAudience,
      lifetimeMinutes: 60,
    });
    expect(token.split('.')).toHaveLength(3);
    const claims = decodeJwt(token);
    expect(claims?.sub).toBe('operator');
    expect(claims?.iss).toBe(developmentIssuer);
    expect(rolesFromClaims(claims)).toEqual(['Planner']);
    expect(isExpired(claims)).toBe(false);
  });

  it('rejects short signing keys', async () => {
    await expect(
      mintDevelopmentToken({ signingKey: 'short', subject: 'x', roles: [], issuer: developmentIssuer, audience: developmentAudience, lifetimeMinutes: 1 }),
    ).rejects.toThrow('at least 32 bytes');
  });

  it('derives permissions from role bundles like the API', () => {
    expect(permissionsForRoles(['Viewer']).has('Plans.Write')).toBe(false);
    expect(permissionsForRoles(['Planner']).has('Plans.Seal')).toBe(true);
    expect(permissionsForRoles(['Operator']).has('Transfers.Start')).toBe(true);
    expect(permissionsForRoles(['Administrator']).size).toBe(20);
    expect(permissionsForRoles(['Nobody']).size).toBe(0);
  });
});
