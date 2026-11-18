import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PagedList } from '../../shared/list/paged-list.store';
import { Job, JobInput } from '../jobs/jobs.api';

export type RequestStatus = 'open' | 'converted' | 'declined';

export interface WorkRequest {
  readonly id: string;
  readonly clientId: string;
  readonly clientName: string;
  readonly contactName: string;
  readonly title: string;
  readonly description: string | null;
  readonly preferredOn: string | null;
  readonly status: RequestStatus;
  readonly createdAt: string;
  readonly jobId: string | null;
  readonly jobLabel: string | null;
  readonly declineReason: string | null;
}

const requestsUrl = (organizationId: string) =>
  `/api/orgs/${encodeURIComponent(organizationId)}/requests`;

/** The staff Requests inbox (SPEC-V2 5.5, 7): <c>portal.manage</c>, and part of the portal feature. */
@Injectable({ providedIn: 'root' })
export class RequestsApi {
  private readonly http = inject(HttpClient);

  list(organizationId: string, params: Record<string, string>): Promise<PagedList<WorkRequest>> {
    return firstValueFrom(
      this.http.get<PagedList<WorkRequest>>(`${requestsUrl(organizationId)}/`, { params }),
    );
  }

  openCount(organizationId: string): Promise<{ open: number }> {
    return firstValueFrom(
      this.http.get<{ open: number }>(`${requestsUrl(organizationId)}/open-count`),
    );
  }

  /** Creates the job from the form and converts the request, together; the job is for the request's client. */
  convert(organizationId: string, id: string, input: JobInput): Promise<Job> {
    return firstValueFrom(
      this.http.post<Job>(
        `${requestsUrl(organizationId)}/${encodeURIComponent(id)}/convert`,
        input,
      ),
    );
  }

  decline(organizationId: string, id: string, reason: string): Promise<WorkRequest> {
    return firstValueFrom(
      this.http.post<WorkRequest>(
        `${requestsUrl(organizationId)}/${encodeURIComponent(id)}/decline`,
        { reason },
      ),
    );
  }
}

/** The sidebar's open-request count, refreshed when the shell opens an organization and after each decision. */
@Injectable({ providedIn: 'root' })
export class RequestsCount {
  private readonly api = inject(RequestsApi);

  readonly open = signal(0);

  async refresh(organizationId: string): Promise<void> {
    try {
      this.open.set((await this.api.openCount(organizationId)).open);
    } catch {
      this.open.set(0);
    }
  }
}
