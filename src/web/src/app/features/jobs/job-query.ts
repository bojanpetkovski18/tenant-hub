import { JobPriority, JobStatus, jobPriorities, jobStatuses } from './jobs.api';

/**
 * The jobs list's query (US-33) as the URL holds it. Multi-value filters are comma-joined in the SPA's URL
 * (`status=new,scheduled`), one parameter per filter; the API gets them as repeated parameters. Unknown values are
 * dropped and fall back to the defaults, the same rule the API applies, so a hand-edited link still shows a list.
 */
export interface JobQuery {
  readonly q: string;
  readonly statuses: readonly JobStatus[];
  readonly priorities: readonly JobPriority[];
  /** A user id, `me`, `none`, or '' for anyone. */
  readonly assignee: string;
  readonly clientId: string;
  readonly overdue: boolean;
  readonly dueFrom: string;
  readonly dueTo: string;
  /** Completed on or after this day (the organization's day): the dashboard's "completed this month". */
  readonly completedFrom: string;
  readonly sort: string;
  readonly page: number;
}

/** The raw query parameters, as the router binds them to inputs. */
export interface RawJobQuery {
  readonly q?: string;
  readonly status?: string;
  readonly priority?: string;
  readonly assignee?: string;
  readonly clientId?: string;
  readonly overdue?: string;
  readonly dueFrom?: string;
  readonly dueTo?: string;
  readonly completedFrom?: string;
  readonly sort?: string;
  readonly page?: string;
}

export const defaultJobSort = '-number';
export const jobSorts: readonly string[] = [
  'number',
  '-number',
  'due',
  '-due',
  'updated',
  '-updated',
  'priority',
  '-priority',
  'title',
  '-title',
];

const isoDate = /^\d{4}-\d{2}-\d{2}$/;
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;

function listOf<T extends string>(raw: string | undefined, allowed: readonly T[]): T[] {
  const values = (raw ?? '').split(',').map((v) => v.trim());
  // Kept in the canonical order, without duplicates, so equal filters give equal URLs.
  return allowed.filter((value) => values.includes(value));
}

export function parseJobQuery(raw: RawJobQuery): JobQuery {
  const assignee = raw.assignee?.trim() ?? '';
  return {
    q: raw.q?.trim() ?? '',
    statuses: listOf(raw.status, jobStatuses),
    priorities: listOf(raw.priority, jobPriorities),
    assignee: assignee === 'me' || assignee === 'none' || uuid.test(assignee) ? assignee : '',
    clientId: uuid.test(raw.clientId ?? '') ? raw.clientId! : '',
    overdue: raw.overdue === 'true',
    dueFrom: isoDate.test(raw.dueFrom ?? '') ? raw.dueFrom! : '',
    dueTo: isoDate.test(raw.dueTo ?? '') ? raw.dueTo! : '',
    completedFrom: isoDate.test(raw.completedFrom ?? '') ? raw.completedFrom! : '',
    sort: jobSorts.includes(raw.sort ?? '') ? raw.sort! : defaultJobSort,
    page: Math.max(1, Number.parseInt(raw.page ?? '1', 10) || 1),
  };
}

/** The API's parameters for this query: repeated status and priority, nothing for an empty filter. */
export function jobApiParams(
  query: JobQuery,
  pageSize: number,
): Record<string, string | readonly string[]> {
  const params: Record<string, string | readonly string[]> = {
    sort: query.sort,
    page: String(query.page),
    pageSize: String(pageSize),
  };
  if (query.q) params['q'] = query.q;
  if (query.statuses.length > 0) params['status'] = query.statuses;
  if (query.priorities.length > 0) params['priority'] = query.priorities;
  if (query.assignee) params['assignee'] = query.assignee;
  if (query.clientId) params['clientId'] = query.clientId;
  if (query.overdue) params['overdue'] = 'true';
  if (query.dueFrom) params['dueFrom'] = query.dueFrom;
  if (query.dueTo) params['dueTo'] = query.dueTo;
  if (query.completedFrom) params['completedFrom'] = query.completedFrom;
  return params;
}

/** Changes for the router: defaults and empty values become null, so they leave the URL. */
export function jobUrlChanges(changes: Partial<JobQuery>): Record<string, string | null> {
  const result: Record<string, string | null> = {};
  for (const [key, value] of Object.entries(changes) as [keyof JobQuery, unknown][]) {
    switch (key) {
      case 'statuses':
        result['status'] = (value as readonly string[]).join(',') || null;
        break;
      case 'priorities':
        result['priority'] = (value as readonly string[]).join(',') || null;
        break;
      case 'overdue':
        result['overdue'] = value ? 'true' : null;
        break;
      case 'sort':
        result['sort'] = value === defaultJobSort ? null : (value as string);
        break;
      case 'page':
        result['page'] = value === 1 ? null : String(value);
        break;
      default:
        result[key] = (value as string) || null;
    }
  }
  return result;
}

/** How many filters (not search, sort or page) are set; shown on the narrow-screen "Filters" button. */
export function activeFilterCount(query: JobQuery): number {
  return [
    query.statuses.length > 0,
    query.priorities.length > 0,
    !!query.assignee,
    !!query.clientId,
    query.overdue,
    !!query.dueFrom || !!query.dueTo,
    !!query.completedFrom,
  ].filter(Boolean).length;
}
