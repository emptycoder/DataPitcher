export type JwtClaims = Readonly<{
  sub?: string;
  iss?: string;
  aud?: string | readonly string[];
  exp?: number;
  iat?: number;
  roles?: readonly string[] | string;
  tid?: string;
  name?: string;
  [key: string]: unknown;
}>;

export function base64UrlEncode(input: string | Uint8Array): string {
  const bytes = typeof input === 'string' ? new TextEncoder().encode(input) : input;
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

export function base64UrlDecode(input: string): string {
  const padded = input.replace(/-/g, '+').replace(/_/g, '/') + '='.repeat((4 - (input.length % 4)) % 4);
  const binary = atob(padded);
  const bytes = Uint8Array.from(binary, (char) => char.charCodeAt(0));
  return new TextDecoder().decode(bytes);
}

export function decodeJwt(token: string): JwtClaims | null {
  const parts = token.split('.');
  if (parts.length !== 3 || !parts[1]) return null;
  try {
    const parsed: unknown = JSON.parse(base64UrlDecode(parts[1]));
    return parsed && typeof parsed === 'object' ? (parsed as JwtClaims) : null;
  } catch {
    return null;
  }
}

export function rolesFromClaims(claims: JwtClaims | null): readonly string[] {
  if (!claims) return [];
  const roles = claims.roles;
  if (Array.isArray(roles)) return roles.filter((role): role is string => typeof role === 'string');
  return typeof roles === 'string' ? [roles] : [];
}

export function isExpired(claims: JwtClaims | null, now = Date.now()): boolean {
  return typeof claims?.exp === 'number' && claims.exp * 1000 <= now;
}
