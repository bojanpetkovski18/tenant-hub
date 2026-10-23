import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export interface FieldChange {
  readonly field: string;
  readonly from: string | null;
  readonly to: string | null;
}

export type AuditEntityType = 'client' | 'job' | 'member' | 'invitation';

export interface AuditEntry {
  readonly id: number;
  readonly occurredAt: string;
  readonly actorUserId: string | null;
  readonly actorName: string;
  readonly action: string;
  readonly entityType: AuditEntityType;
  readonly entityId: string;
  readonly summary: string;
  readonly changes: readonly FieldChange[];
  /** The viewer links only to things that still exist (a deleted job has history but no page). */
  readonly entityExists: boolean;
}

/** Newest first; `nextCursor` asks for the next older page, null on the last one (keyset paging, D030). */
export interface AuditPage {
  readonly items: readonly AuditEntry[];
  readonly nextCursor: string | null;
}

export const entityTypeLabels: Readonly<Record<AuditEntityType, string>> = {
  client: 'Client',
  job: 'Job',
  member: 'Member',
  invitation: 'Invitation',
};

/** The actions the API records (D012), grouped for the filter; labels are for people. */
export const auditActions: readonly {
  readonly type: AuditEntityType;
  readonly actions: readonly (readonly [string, string])[];
}[] = [
  {
    type: 'job',
    actions: [
      ['job.created', 'Job created'],
      ['job.updated', 'Job updated'],
      ['job.status_changed', 'Job status changed'],
      ['job.unassigned', 'Job unassigned'],
      ['job.deleted', 'Job deleted'],
    ],
  },
  {
    type: 'client',
    actions: [
      ['client.created', 'Client created'],
      ['client.updated', 'Client updated'],
      ['client.archived', 'Client archived'],
      ['client.restored', 'Client restored'],
      ['client.deleted', 'Client deleted'],
    ],
  },
  {
    type: 'member',
    actions: [
      ['member.role_changed', 'Role changed'],
      ['member.removed', 'Member removed'],
      ['member.left', 'Member left'],
    ],
  },
  {
    type: 'invitation',
    actions: [
      ['invitation.created', 'Invitation sent'],
      ['invitation.resent', 'Invitation resent'],
      ['invitation.revoked', 'Invitation revoked'],
      ['invitation.accepted', 'Invitation accepted'],
    ],
  },
];

export const auditUrl = (organizationId: string) =>
  `/api/orgs/${encodeURIComponent(organizationId)}/audit`;

@Injectable({ providedIn: 'root' })
export class AuditApi {
  private readonly http = inject(HttpClient);

  list(organizationId: string, params: Record<string, string>): Promise<AuditPage> {
    return firstValueFrom(
      this.http.get<AuditPage>(auditUrl(organizationId), {
        params: new HttpParams({ fromObject: params }),
      }),
    );
  }
}
