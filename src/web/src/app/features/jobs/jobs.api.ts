import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PagedList } from '../../shared/list/paged-list.store';

export type JobStatus = 'new' | 'scheduled' | 'inProgress' | 'completed' | 'cancelled';
export type JobPriority = 'low' | 'normal' | 'high' | 'urgent';

export const jobStatuses: readonly JobStatus[] = [
  'new',
  'scheduled',
  'inProgress',
  'completed',
  'cancelled',
];
export const jobPriorities: readonly JobPriority[] = ['urgent', 'high', 'normal', 'low'];

export const statusLabels: Readonly<Record<JobStatus, string>> = {
  new: 'New',
  scheduled: 'Scheduled',
  inProgress: 'In progress',
  completed: 'Completed',
  cancelled: 'Cancelled',
};

export const priorityLabels: Readonly<Record<JobPriority, string>> = {
  low: 'Low',
  normal: 'Normal',
  high: 'High',
  urgent: 'Urgent',
};

/** What the button for a move says; the API decides which moves exist for the caller. */
export const transitionLabels: Readonly<Record<JobStatus, string>> = {
  new: 'Back to new',
  scheduled: 'Schedule',
  inProgress: 'Start',
  completed: 'Complete',
  cancelled: 'Cancel job',
};

export interface JobListItem {
  readonly id: string;
  readonly number: number;
  readonly label: string;
  readonly title: string;
  readonly clientId: string;
  readonly clientName: string;
  readonly assigneeUserId: string | null;
  readonly assigneeName: string | null;
  readonly status: JobStatus;
  readonly priority: JobPriority;
  readonly scheduledOn: string | null;
  readonly dueOn: string | null;
  readonly isOverdue: boolean;
  readonly updatedAt: string;
}

/** A move the caller may make; `blockedReason` says what is still missing (the button is then disabled). */
export interface JobTransitionOption {
  readonly to: JobStatus;
  readonly needsReason: boolean;
  readonly blockedReason: string | null;
}

/** One job with what the caller may do to it, decided by the API (D008): the SPA never derives these. */
export interface Job {
  readonly id: string;
  readonly number: number;
  readonly label: string;
  readonly title: string;
  readonly description: string | null;
  readonly clientId: string;
  readonly clientName: string;
  readonly assigneeUserId: string | null;
  readonly assigneeName: string | null;
  readonly status: JobStatus;
  readonly priority: JobPriority;
  readonly scheduledOn: string | null;
  readonly dueOn: string | null;
  readonly quotedAmount: number | null;
  readonly isOverdue: boolean;
  readonly createdAt: string;
  readonly createdByName: string;
  readonly updatedAt: string;
  readonly completedAt: string | null;
  readonly version: number;
  readonly canEdit: boolean;
  readonly canAssign: boolean;
  readonly canDelete: boolean;
  readonly transitions: readonly JobTransitionOption[];
}

export interface JobInput {
  readonly title: string;
  readonly description: string | null;
  readonly clientId: string | null;
  readonly assigneeUserId: string | null;
  readonly priority: JobPriority;
  readonly scheduledOn: string | null;
  readonly dueOn: string | null;
  readonly quotedAmount: number | null;
}

export interface FieldChange {
  readonly field: string;
  readonly from: string | null;
  readonly to: string | null;
}

export interface JobHistoryEntry {
  readonly id: number;
  readonly occurredAt: string;
  readonly actorName: string;
  readonly action: string;
  readonly summary: string;
  readonly changes: readonly FieldChange[];
}

export const jobsUrl = (organizationId: string) =>
  `/api/orgs/${encodeURIComponent(organizationId)}/jobs`;

/** The job endpoints (SPEC 7); the organization is always part of the URL. */
@Injectable({ providedIn: 'root' })
export class JobsApi {
  private readonly http = inject(HttpClient);

  get(organizationId: string, id: string): Promise<Job> {
    return firstValueFrom(this.http.get<Job>(`${jobsUrl(organizationId)}/${id}`));
  }

  /** A small list for a section of another page (a client's jobs, a count). */
  list(
    organizationId: string,
    params: Record<string, string | readonly string[]>,
  ): Promise<PagedList<JobListItem>> {
    return firstValueFrom(
      this.http.get<PagedList<JobListItem>>(jobsUrl(organizationId), {
        params: new HttpParams({ fromObject: params }),
      }),
    );
  }

  history(organizationId: string, id: string): Promise<JobHistoryEntry[]> {
    return firstValueFrom(
      this.http.get<JobHistoryEntry[]>(`${jobsUrl(organizationId)}/${id}/history`),
    );
  }

  create(organizationId: string, input: JobInput): Promise<Job> {
    return firstValueFrom(this.http.post<Job>(jobsUrl(organizationId), input));
  }

  update(organizationId: string, id: string, input: JobInput, version: number): Promise<Job> {
    return firstValueFrom(
      this.http.put<Job>(`${jobsUrl(organizationId)}/${id}`, { ...input, version }),
    );
  }

  transition(organizationId: string, id: string, to: JobStatus, reason?: string): Promise<Job> {
    return firstValueFrom(
      this.http.post<Job>(`${jobsUrl(organizationId)}/${id}/transitions`, { to, reason }),
    );
  }

  delete(organizationId: string, id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${jobsUrl(organizationId)}/${id}`));
  }
}
