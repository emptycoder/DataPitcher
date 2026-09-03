import { HttpError } from './http';

export type ProblemDetails = Readonly<{
    title?: string;
    detail?: string;
    code?: string;
    status?: number;
    correlationId?: string;
    errors?: Readonly<Record<string, unknown>>;
}>;

export function problemOf(error: unknown): ProblemDetails | null {
    if (!(error instanceof HttpError) || !error.problem || typeof error.problem !== 'object') return null;
    return error.problem as ProblemDetails;
}

const statusMessages: Readonly<Record<number, string>> = {
    400: 'The request was not valid.',
    401: 'Your session is not authenticated. Sign in again.',
    403: 'You do not have permission to do that.',
    404: 'That resource was not found.',
    409: 'The resource changed or is in a conflicting state.',
    422: 'The request could not be processed.',
    502: 'A database connection could not be used.',
    503: 'The service is temporarily unavailable.',
    504: 'The database query timed out.',
};

/** Human-readable message for any error thrown by the transport layer. */
export function describeError(error: unknown, fallback = 'Something went wrong.'): string {
    if (error instanceof HttpError) {
        const problem = problemOf(error);
        const title = problem?.title && problem.title !== 'Internal error' ? problem.title : null;
        const detail =
            problem?.detail && problem.detail !== 'The operation could not be completed.' ? problem.detail : null;
        if (title && detail && title !== detail) return `${title}. ${detail}`;
        if (title) return title;
        if (detail) return detail;
        if (error.status === 403 && !problem)
            return 'Something other than the DataPitcher API answered with 403 (on macOS this is usually AirPlay Receiver on port 5000). Make sure the API is running on the port the dev server proxies to.';
        return (
            statusMessages[error.status] ??
            (error.status >= 500 ? 'The server could not complete the operation.' : fallback)
        );
    }
    if (error instanceof DOMException && error.name === 'AbortError') return 'The request was cancelled.';
    if (error instanceof TypeError) return 'The API could not be reached. Is the server running?';
    if (error instanceof Error && error.message) return error.message;
    return fallback;
}

export function errorCode(error: unknown): string | null {
    return problemOf(error)?.code ?? null;
}

export function isNotWired(error: unknown): boolean {
    return error instanceof HttpError && error.status === 500 && errorCode(error) === 'internal_error';
}
