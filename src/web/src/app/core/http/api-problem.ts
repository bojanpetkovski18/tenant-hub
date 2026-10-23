import { HttpErrorResponse } from '@angular/common/http';

/** An RFC 7807 problem from the API, reduced to what the UI needs (SPEC 7). */
export interface ApiProblem {
  readonly status: number;
  readonly title: string;
  /** Stable business-rule code, e.g. "owned-organization-limit". */
  readonly code?: string;
  /** Messages per request field, keyed by the JSON property name. */
  readonly fieldErrors: Readonly<Record<string, readonly string[]>>;
  /** `plan-limit-reached`: which limit, its value on the plan, and the usage (SPEC 5.5). */
  readonly limit?: PlanLimitDetail & { readonly plan: string };
  /** `plan-downgrade-blocked`: every limit the usage exceeds on the target plan. */
  readonly blocking?: readonly PlanLimitDetail[];
}

export type LimitName = 'seats' | 'activeClients' | 'openJobs';

export interface PlanLimitDetail {
  readonly limit: LimitName;
  readonly allowed: number;
  readonly usage: number;
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
    limit:
      typeof body['limit'] === 'string' && typeof body['plan'] === 'string'
        ? { plan: body['plan'], ...toLimit(body) }
        : undefined,
    blocking: Array.isArray(body['blocking'])
      ? body['blocking'].filter(isRecord).map(toLimit)
      : undefined,
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

function toLimit(value: Record<string, unknown>): PlanLimitDetail {
  return {
    limit: value['limit'] as LimitName,
    allowed: Number(value['allowed']),
    usage: Number(value['usage']),
  };
}

function isRecord(value: unknown): value is Record<string, unknown> {
  return typeof value === 'object' && value !== null && !Array.isArray(value);
}
