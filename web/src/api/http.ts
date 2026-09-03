import type { AuthenticationAdapter } from '../auth/authAdapter';

export type HttpProblem = unknown;

export class HttpError extends Error {
    readonly status: number;
    readonly problem: HttpProblem | null;

    constructor(status: number, problem: HttpProblem | null) {
        super(`Request failed with status ${status}.`);
        this.name = 'HttpError';
        this.status = status;
        this.problem = problem;
    }
}

export type JsonRequest = Readonly<Omit<RequestInit, 'body' | 'headers'>> &
    Readonly<{
        body?: unknown;
        headers?: HeadersInit;
    }>;

async function readProblem(response: Response): Promise<HttpProblem | null> {
    try {
        return await response.json();
    } catch {
        return null;
    }
}

export async function requestJson<T>(
    url: string,
    authentication: AuthenticationAdapter,
    options: JsonRequest = {},
): Promise<T> {
    const token = await authentication.getAccessToken();
    if (!token) throw new HttpError(401, { detail: 'Not authenticated.' });

    const { body, headers: suppliedHeaders, ...init } = options;
    const headers = new Headers(suppliedHeaders);
    headers.set('Authorization', `Bearer ${token}`);
    if (body !== undefined) headers.set('Content-Type', 'application/json');

    const response = await fetch(url, {
        ...init,
        headers,
        body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (!response.ok) throw new HttpError(response.status, await readProblem(response));
    if (response.status === 204 || response.headers.get('content-length') === '0') return undefined as T;
    return response.json() as Promise<T>;
}
