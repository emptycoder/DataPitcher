import { developmentAudience, developmentIssuer } from './devToken';

/** Development sign-in settings remembered for the current browser tab (sessionStorage), never the token itself. */
export type DevelopmentSignIn = Readonly<{
    signingKey: string;
    subject: string;
    roles: readonly string[];
    issuer: string;
    audience: string;
    lifetimeMinutes: number;
}>;

const key = 'datapitcher.dev-sign-in';

/** Matches the key scripts/dev.sh starts the API with. Local development only. */
export const localDevelopmentSigningKey = 'local-development-signing-key-0123456789abcdef';

export const defaultDevelopmentSignIn: DevelopmentSignIn = {
    signingKey: localDevelopmentSigningKey,
    subject: 'development-operator',
    roles: ['Administrator'],
    issuer: developmentIssuer,
    audience: developmentAudience,
    lifetimeMinutes: 480,
};

export function loadRememberedSignIn(): DevelopmentSignIn | null {
    try {
        const raw = window.sessionStorage.getItem(key);
        if (!raw) return null;
        const parsed: unknown = JSON.parse(raw);
        if (!parsed || typeof parsed !== 'object') return null;
        return { ...defaultDevelopmentSignIn, ...(parsed as Partial<DevelopmentSignIn>) };
    } catch {
        return null;
    }
}

export function rememberSignIn(settings: DevelopmentSignIn | null) {
    try {
        if (settings) window.sessionStorage.setItem(key, JSON.stringify(settings));
        else window.sessionStorage.removeItem(key);
    } catch {
        /* storage unavailable */
    }
}
