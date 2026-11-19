import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PagedList } from '../../shared/list/paged-list.store';
import { JobStatus } from '../jobs/jobs.api';

/** The business, as its portal shows it: its name and nothing else (SPEC-V2 2.3). */
export interface PortalOrganization {
  readonly name: string;
  readonly slug: string;
}

export interface PortalContact {
  readonly id: string;
  readonly name: string;
  readonly email: string;
  readonly clientName: string;
}

/** `GET /api/portal/session`: no contact when signed out; no organization when this portal does not exist or is off. */
export interface PortalSession {
  readonly organization: PortalOrganization | null;
  readonly contact: PortalContact | null;
}

export interface PortalVisit {
  readonly id: string;
  readonly label: string;
  readonly title: string;
  readonly scheduledOn: string | null;
  readonly status: JobStatus;
  readonly assigneeName: string | null;
}

export interface PortalVisitDetail extends PortalVisit {
  readonly description: string | null;
  readonly dueOn: string | null;
  readonly completedAt: string | null;
}

export type ApprovalStatus = 'notRequested' | 'pending' | 'approved' | 'declined';

export interface PortalQuote {
  readonly jobId: string;
  readonly label: string;
  readonly title: string;
  readonly scheduledOn: string | null;
  readonly amount: number;
  readonly status: ApprovalStatus;
  readonly sentAt: string | null;
  readonly decidedAt: string | null;
  readonly decidedByName: string | null;
  readonly comment: string | null;
}

export type RequestStatus = 'open' | 'converted' | 'declined';

export interface PortalRequest {
  readonly id: string;
  readonly title: string;
  readonly description: string | null;
  readonly preferredOn: string | null;
  readonly status: RequestStatus;
  readonly createdAt: string;
  readonly declineReason: string | null;
  readonly jobLabel: string | null;
  readonly requestedBy: string;
}

export interface PortalRequestInput {
  readonly title: string;
  readonly description: string | null;
  readonly preferredOn: string | null;
}

const base = '/api/portal';

/** The portal API (SPEC-V2 7, portal-scoped): the client always comes from the session cookie, never from here. */
@Injectable({ providedIn: 'root' })
export class PortalApi {
  private readonly http = inject(HttpClient);

  session(slug: string): Promise<PortalSession> {
    return firstValueFrom(
      this.http.get<PortalSession>(`${base}/session`, { params: { orgSlug: slug } }),
    );
  }

  /** Always succeeds (202) for a well-formed request: the answer says nothing about the address. */
  requestLink(slug: string, email: string): Promise<unknown> {
    return firstValueFrom(this.http.post(`${base}/sign-in/request`, { orgSlug: slug, email }));
  }

  confirm(slug: string, token: string): Promise<PortalSession> {
    return firstValueFrom(
      this.http.post<PortalSession>(`${base}/sign-in/confirm`, { orgSlug: slug, token }),
    );
  }

  demoSignIn(email: string): Promise<PortalSession> {
    return firstValueFrom(this.http.post<PortalSession>(`${base}/demo/sign-in`, { email }));
  }

  signOut(): Promise<unknown> {
    return firstValueFrom(this.http.post(`${base}/sign-out`, {}));
  }

  visits(when: 'upcoming' | 'past', page: number): Promise<PagedList<PortalVisit>> {
    return firstValueFrom(
      this.http.get<PagedList<PortalVisit>>(`${base}/visits`, {
        params: { when, page, pageSize: 20 },
      }),
    );
  }

  visit(id: string): Promise<PortalVisitDetail> {
    return firstValueFrom(
      this.http.get<PortalVisitDetail>(`${base}/visits/${encodeURIComponent(id)}`),
    );
  }

  quotes(): Promise<PortalQuote[]> {
    return firstValueFrom(this.http.get<PortalQuote[]>(`${base}/quotes`));
  }

  decide(jobId: string, approve: boolean, comment: string | null): Promise<PortalQuote> {
    return firstValueFrom(
      this.http.post<PortalQuote>(
        `${base}/quotes/${encodeURIComponent(jobId)}/${approve ? 'approve' : 'decline'}`,
        {
          comment,
        },
      ),
    );
  }

  requests(): Promise<PortalRequest[]> {
    return firstValueFrom(this.http.get<PortalRequest[]>(`${base}/requests`));
  }

  submitRequest(input: PortalRequestInput): Promise<PortalRequest> {
    return firstValueFrom(this.http.post<PortalRequest>(`${base}/requests`, input));
  }
}
