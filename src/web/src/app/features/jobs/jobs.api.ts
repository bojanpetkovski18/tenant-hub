import { HttpClient, HttpEventType, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable, filter, firstValueFrom, map } from 'rxjs';
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
  readonly agreementId: string | null;
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
  readonly estimatedMinutes: number | null;
  /** Set for visits of an agreement (SPEC-V2 4.2): its id and title, and the occurrence the visit is. */
  readonly agreementId: string | null;
  readonly agreementTitle: string | null;
  readonly occurrenceOn: string | null;
  /** Someone edited this visit, so changes to its agreement no longer reach it. */
  readonly changedByHand: boolean;
  readonly isOverdue: boolean;
  readonly createdAt: string;
  readonly createdByName: string;
  readonly updatedAt: string;
  readonly completedAt: string | null;
  readonly version: number;
  readonly canEdit: boolean;
  readonly canAssign: boolean;
  readonly canDelete: boolean;
  readonly canSkip: boolean;
  readonly transitions: readonly JobTransitionOption[];
  /** The quote's approval by the client (SPEC-V2 5.4), and whether the caller can send it now. */
  readonly quote?: JobQuote;
  /** The client's sign-off on the completed visit (SPEC-V2 5.6). */
  readonly clientFeedback?: JobFeedback | null;
}

export type FeedbackKind = 'confirmed' | 'problemReported';

export interface JobFeedback {
  readonly kind: FeedbackKind;
  readonly text: string | null;
  readonly at: string;
  readonly byName: string | null;
}

export interface ChecklistItem {
  readonly id: string;
  readonly position: number;
  readonly text: string;
  readonly required: boolean;
  readonly done: boolean;
  readonly doneAt: string | null;
  readonly doneByName: string | null;
}

/** The checklist with what the caller may do (SPEC-V2 5.6): `readOnlyReason` explains a completed job's. */
export interface Checklist {
  readonly items: readonly ChecklistItem[];
  readonly canEdit: boolean;
  readonly readOnlyReason: string | null;
  readonly maxItems: number;
}

export interface ChecklistChange {
  readonly text?: string;
  readonly required?: boolean;
  readonly done?: boolean;
  readonly position?: number;
}

export interface Photo {
  readonly id: string;
  readonly contentType: string;
  readonly width: number;
  readonly height: number;
  readonly bytes: number;
  readonly createdAt: string;
  readonly createdByName: string;
}

export interface Photos {
  readonly items: readonly Photo[];
  readonly canAdd: boolean;
  readonly canRemove: boolean;
  readonly cannotAdd: string | null;
  readonly maxPhotos: number;
}

/** An upload on its way: a share sent (0 to 100), then the stored photo. */
export type UploadEvent =
  | { readonly kind: 'progress'; readonly percent: number }
  | { readonly kind: 'done'; readonly photo: Photo };

export interface JobInput {
  readonly title: string;
  readonly description: string | null;
  readonly clientId: string | null;
  readonly assigneeUserId: string | null;
  readonly priority: JobPriority;
  readonly scheduledOn: string | null;
  readonly dueOn: string | null;
  readonly quotedAmount: number | null;
  readonly estimatedMinutes: number | null;
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

  /** Skips one visit of an agreement (SPEC-V2 5.1): cancelled as "Skipped", never generated again. */
  skip(organizationId: string, id: string): Promise<Job> {
    return firstValueFrom(this.http.post<Job>(`${jobsUrl(organizationId)}/${id}/skip`, {}));
  }

  /** Sends the quote to the client's portal contacts for approval (SPEC-V2 5.4). */
  sendQuote(organizationId: string, id: string): Promise<Job> {
    return firstValueFrom(this.http.post<Job>(`${jobsUrl(organizationId)}/${id}/quote`, {}));
  }

  checklist(organizationId: string, id: string): Promise<Checklist> {
    return firstValueFrom(this.http.get<Checklist>(`${jobsUrl(organizationId)}/${id}/checklist`));
  }

  addChecklistItem(
    organizationId: string,
    id: string,
    text: string,
    required: boolean,
  ): Promise<Checklist> {
    return firstValueFrom(
      this.http.post<Checklist>(`${jobsUrl(organizationId)}/${id}/checklist`, { text, required }),
    );
  }

  changeChecklistItem(
    organizationId: string,
    id: string,
    itemId: string,
    change: ChecklistChange,
  ): Promise<Checklist> {
    return firstValueFrom(
      this.http.put<Checklist>(`${jobsUrl(organizationId)}/${id}/checklist/${itemId}`, change),
    );
  }

  removeChecklistItem(organizationId: string, id: string, itemId: string): Promise<Checklist> {
    return firstValueFrom(
      this.http.delete<Checklist>(`${jobsUrl(organizationId)}/${id}/checklist/${itemId}`),
    );
  }

  photos(organizationId: string, id: string): Promise<Photos> {
    return firstValueFrom(this.http.get<Photos>(`${jobsUrl(organizationId)}/${id}/photos`));
  }

  /** Where a photo (or its thumbnail) is served; same origin, so the session cookie goes along. */
  photoUrl(organizationId: string, id: string, photoId: string, thumbnail = false): string {
    return `${jobsUrl(organizationId)}/${id}/photos/${photoId}${thumbnail ? '/thumbnail' : ''}`;
  }

  /** Uploads one photo as multipart form data, reporting progress (SPEC-V2 5.6). */
  uploadPhoto(organizationId: string, id: string, file: Blob): Observable<UploadEvent> {
    const form = new FormData();
    form.append('file', file);
    return this.http
      .post<Photo>(`${jobsUrl(organizationId)}/${id}/photos`, form, {
        reportProgress: true,
        observe: 'events',
      })
      .pipe(
        filter(
          (event) =>
            event.type === HttpEventType.UploadProgress || event.type === HttpEventType.Response,
        ),
        map((event): UploadEvent =>
          event.type === HttpEventType.Response
            ? { kind: 'done', photo: event.body! }
            : {
                kind: 'progress',
                percent: event.total ? Math.round((100 * event.loaded) / event.total) : 0,
              },
        ),
      );
  }

  removePhoto(organizationId: string, id: string, photoId: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(`${jobsUrl(organizationId)}/${id}/photos/${photoId}`),
    );
  }
}

export type ApprovalStatus = 'notRequested' | 'pending' | 'approved' | 'declined';

export interface JobQuote {
  readonly status: ApprovalStatus;
  readonly sentAt: string | null;
  readonly sentByName: string | null;
  readonly approvedAmount: number | null;
  readonly decidedAt: string | null;
  readonly decidedByName: string | null;
  readonly comment: string | null;
  readonly canSend: boolean;
  /** Why it cannot be sent now, for those who could; null for everyone else. */
  readonly cannotSend: string | null;
}
