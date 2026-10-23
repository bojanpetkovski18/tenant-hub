import { AuditEntityType, auditActions } from './audit.api';

/**
 * The audit log's query (US-40) as the URL holds it: the filters plus the keyset cursor (`before`), so a reload or a
 * shared link shows the same page. Unknown values are dropped, the rule every list follows.
 */
export interface AuditQuery {
  readonly actor: string;
  readonly action: string;
  readonly entityType: AuditEntityType | '';
  readonly entityId: string;
  readonly from: string;
  readonly to: string;
  readonly before: string;
}

export interface RawAuditQuery {
  readonly actor?: string;
  readonly action?: string;
  readonly entityType?: string;
  readonly entityId?: string;
  readonly from?: string;
  readonly to?: string;
  readonly before?: string;
}

const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const isoDate = /^\d{4}-\d{2}-\d{2}$/;
const cursor = /^\d+-\d+$/;
const knownActions = new Set(
  auditActions.flatMap((group) => group.actions.map(([action]) => action)),
);
const knownTypes = new Set<string>(auditActions.map((group) => group.type));

export function parseAuditQuery(raw: RawAuditQuery): AuditQuery {
  return {
    actor: uuid.test(raw.actor ?? '') ? raw.actor! : '',
    action: knownActions.has(raw.action ?? '') ? raw.action! : '',
    entityType: knownTypes.has(raw.entityType ?? '') ? (raw.entityType as AuditEntityType) : '',
    entityId: uuid.test(raw.entityId ?? '') ? raw.entityId! : '',
    from: isoDate.test(raw.from ?? '') ? raw.from! : '',
    to: isoDate.test(raw.to ?? '') ? raw.to! : '',
    before: cursor.test(raw.before ?? '') ? raw.before! : '',
  };
}

/** The API's parameters: only what is set. */
export function auditApiParams(query: AuditQuery, pageSize: number): Record<string, string> {
  const params: Record<string, string> = { pageSize: String(pageSize) };
  for (const key of [
    'actor',
    'action',
    'entityType',
    'entityId',
    'from',
    'to',
    'before',
  ] as const) {
    if (query[key]) {
      params[key] = query[key];
    }
  }
  return params;
}

/**
 * Changes for the router. Any filter change starts again from the newest entries (the cursor belongs to the old
 * filter); empty values leave the URL.
 */
export function auditUrlChanges(changes: Partial<AuditQuery>): Record<string, string | null> {
  const result: Record<string, string | null> = {};
  for (const [key, value] of Object.entries(changes)) {
    result[key] = (value as string) || null;
  }
  if (!('before' in changes)) {
    result['before'] = null;
  }
  return result;
}

export function activeAuditFilterCount(query: AuditQuery): number {
  return [
    query.actor,
    query.action,
    query.entityType,
    query.entityId,
    query.from || query.to,
  ].filter(Boolean).length;
}
