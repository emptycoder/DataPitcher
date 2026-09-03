import { base64UrlEncode } from './jwt';

export type DevelopmentTokenRequest = Readonly<{
  signingKey: string;
  subject: string;
  roles: readonly string[];
  issuer: string;
  audience: string;
  lifetimeMinutes: number;
}>;

export const developmentIssuer = 'https://localhost/datapitcher-dev';
export const developmentAudience = 'datapitcher-api';

/** Mints an HS256 JWT the API's Development authentication scheme accepts. Development use only. */
export async function mintDevelopmentToken(request: DevelopmentTokenRequest, now = Date.now()): Promise<string> {
  if (new TextEncoder().encode(request.signingKey).length < 32) throw new Error('The signing key must be at least 32 bytes.');
  const issuedAt = Math.floor(now / 1000);
  const header = base64UrlEncode(JSON.stringify({ alg: 'HS256', typ: 'JWT' }));
  const payload = base64UrlEncode(
    JSON.stringify({
      iss: request.issuer,
      aud: request.audience,
      sub: request.subject,
      roles: request.roles,
      iat: issuedAt,
      nbf: issuedAt - 5,
      exp: issuedAt + Math.max(1, request.lifetimeMinutes) * 60,
    }),
  );
  const key = await crypto.subtle.importKey('raw', new TextEncoder().encode(request.signingKey), { name: 'HMAC', hash: 'SHA-256' }, false, ['sign']);
  const signature = await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(`${header}.${payload}`));
  return `${header}.${payload}.${base64UrlEncode(new Uint8Array(signature))}`;
}
