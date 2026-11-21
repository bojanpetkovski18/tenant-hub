import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type NotificationType =
  | 'assigned'
  | 'visitChanged'
  | 'quoteDecided'
  | 'workRequested'
  | 'problemReported'
  | 'generationPaused'
  | 'overdueDigest';

export type NotificationLink = 'job' | 'request' | 'agreement' | 'overdueJobs';

export interface AppNotification {
  readonly id: string;
  readonly type: NotificationType;
  readonly text: string;
  readonly link: NotificationLink;
  readonly linkId: string | null;
  readonly createdAt: string;
  readonly readAt: string | null;
}

/** A page of the caller's notifications, newest first, with the unread count (the bell's number). */
export interface NotificationList {
  readonly items: readonly AppNotification[];
  readonly page: number;
  readonly pageSize: number;
  readonly total: number;
  readonly unread: number;
}

export interface NotificationPreference {
  readonly type: NotificationType;
  readonly label: string;
  readonly email: boolean;
}

/** Where a notification leads in the app (the same place the email links to). */
export function notificationRoute(
  slug: string,
  n: AppNotification,
): { readonly commands: readonly string[]; readonly query?: Record<string, string> } {
  switch (n.link) {
    case 'job':
      return { commands: ['/o', slug, 'jobs', n.linkId ?? ''] };
    case 'request':
      return { commands: ['/o', slug, 'requests'] };
    case 'agreement':
      return { commands: ['/o', slug, 'agreements', n.linkId ?? ''] };
    default:
      return { commands: ['/o', slug, 'jobs'], query: { overdue: 'true' } };
  }
}

const url = (organizationId: string) =>
  `/api/orgs/${encodeURIComponent(organizationId)}/notifications`;

/** Notifications in an organization (SPEC-V2 7), and the person's email choices, which are the same everywhere. */
@Injectable({ providedIn: 'root' })
export class NotificationsApi {
  private readonly http = inject(HttpClient);

  list(organizationId: string, pageSize = 10): Promise<NotificationList> {
    return firstValueFrom(
      this.http.get<NotificationList>(url(organizationId), {
        params: new HttpParams({ fromObject: { pageSize } }),
      }),
    );
  }

  /** Marks the given ones, or all, as read; answers with what is still unread. */
  markRead(
    organizationId: string,
    ids: readonly string[] | 'all',
  ): Promise<{ readonly unread: number }> {
    return firstValueFrom(
      this.http.post<{ readonly unread: number }>(
        `${url(organizationId)}/read`,
        ids === 'all' ? { all: true } : { ids },
      ),
    );
  }

  preferences(): Promise<NotificationPreference[]> {
    return firstValueFrom(
      this.http.get<NotificationPreference[]>('/api/me/notification-preferences'),
    );
  }

  savePreferences(
    preferences: readonly { readonly type: NotificationType; readonly email: boolean }[],
  ): Promise<NotificationPreference[]> {
    return firstValueFrom(
      this.http.put<NotificationPreference[]>('/api/me/notification-preferences', { preferences }),
    );
  }
}
