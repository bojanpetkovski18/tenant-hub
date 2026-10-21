import { HttpErrorResponse } from '@angular/common/http';

/** An RFC 7807 problem from the API, reduced to what the UI needs (SPEC 7). */
export interface ApiProblem {
  readonly status: number;
  readonly title: string;
  /** Stable business-rule code, e.g. "owned-organization-limit". */
  readonly code?: string;
  /** Messages per request field, keyed by the JSON property name. */
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;
}

export function toApiProblem(error: unknown): ApiProblem {
  if (!(error instanceof HttpErrorResponse)) {
    return { status: 0, title: 'Something went wrong. Please try again.', fieldErrors: {} };
  }

  if (error.status === 0) {
    return {
      status: 0,
      title: 'Cannot reach the server. Check your connection and try again.',
      fieldErrors: {},
    };
  }

  const body = isRecord(error.error) ? error.error : {};
  return {
    status: error.status,
    title: typeof body['title'] === 'string' ? body['title'] : fallbackTitle(error.status),
    code: typeof body['code'] === 'string' ? body['code'] : undefined,
    fieldErrors: isRecord(body['errors']) ? toFieldErrors(body['errors']) : {},
  };
}

function fallbackTitle(status: number): string {
  if (status === 429) {
    return 'Too many attempts. Please wait a minute and try again.';
  }
  return status >= 500
    ? 'Something went wrong on our side. Please try again.'
    : 'The request could not be completed.';
}

function toFieldErrors(errors: Record<string, unknown>): Record<string, readonly string[]> {
  const result: Record<string, readonly string[]> = {};
  for (const [field, messages] of Object.entries(errors)) {
    if (Array.isArray(messages)) {
      result[field] = messages.filter((m): m is string => typeof m === 'string');
    }
  }
  return result;
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
