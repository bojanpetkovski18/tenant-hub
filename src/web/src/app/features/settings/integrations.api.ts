import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type ApiKeyAccess = 'readOnly' | 'readWrite';

export interface ApiKey {
  readonly id: string;
  readonly name: string;
  /** The key's first characters, to tell keys apart; the key itself is shown once. */
  readonly prefix: string;
  readonly access: ApiKeyAccess;
  readonly createdAt: string;
  readonly createdByName: string;
  readonly expiresAt: string | null;
  readonly lastUsedAt: string | null;
  readonly revokedAt: string | null;
  readonly active: boolean;
}

export interface WebhookDelivery {
  readonly id: string;
  readonly eventId: string;
  readonly eventType: string;
  readonly attempt: number;
  readonly statusCode: number | null;
  readonly durationMs: number;
  readonly error: string | null;
  readonly createdAt: string;
  readonly nextAttemptAt: string | null;
  readonly succeeded: boolean;
}

export interface WebhookEndpoint {
  readonly id: string;
  readonly url: string;
  readonly events: readonly string[];
  readonly enabled: boolean;
  readonly createdAt: string;
  readonly disabledAt: string | null;
  readonly failingSince: string | null;
  /** The previous secret still signs too (24 hours after a rotation). */
  readonly rotationOverlap: boolean;
  readonly lastDelivery: WebhookDelivery | null;
}

export interface DeliveryPage {
  readonly items: readonly WebhookDelivery[];
  readonly page: number;
  readonly pageSize: number;
  readonly total: number;
}

/** SPEC-V2 5.8's catalog, in its order, with what each event means to people. */
export const webhookEvents: readonly (readonly [string, string])[] = [
  ['job.created', 'A job is created'],
  ['job.status_changed', 'A job changes status'],
  ['job.assigned', 'A job is assigned'],
  ['quote.approved', 'A client approves a quote'],
  ['quote.declined', 'A client declines a quote'],
  ['request.created', 'A client asks for work'],
  ['visit.confirmed', 'A client confirms a visit'],
  ['visit.problem_reported', 'A client reports a problem with a visit'],
];

export const accessLabels: Readonly<Record<ApiKeyAccess, string>> = {
  readOnly: 'Read-only',
  readWrite: 'Read and write',
};

/** The class of error a delivery failed with, in words. */
export function deliveryOutcome(d: WebhookDelivery): string {
  if (d.succeeded) {
    return `Delivered (${d.statusCode})`;
  }
  switch (d.error) {
    case 'address-not-allowed':
      return 'Refused: the address is not public';
    case 'redirect-not-followed':
      return `Redirected (${d.statusCode}); redirects are not followed`;
    case 'dns-failed':
      return 'The host name does not resolve';
    case 'tls-failed':
      return 'The secure connection failed';
    case 'timeout':
      return 'No answer within 10 seconds';
    case 'connection-failed':
      return 'Could not connect';
    default:
      return d.statusCode ? `Failed (${d.statusCode})` : 'Failed';
  }
}

const orgUrl = (organizationId: string) => `/api/orgs/${encodeURIComponent(organizationId)}`;

/** API keys and webhooks (SPEC-V2 7): a Business feature for Owners and Admins (`integrations.manage`). */
@Injectable({ providedIn: 'root' })
export class IntegrationsApi {
  private readonly http = inject(HttpClient);

  keys(organizationId: string): Promise<ApiKey[]> {
    return firstValueFrom(this.http.get<ApiKey[]>(`${orgUrl(organizationId)}/api-keys`));
  }

  /** The answer is the only time the key itself is seen. */
  createKey(
    organizationId: string,
    input: { name: string; access: ApiKeyAccess; expiresAt: string | null },
  ): Promise<{ apiKey: ApiKey; key: string }> {
    return firstValueFrom(
      this.http.post<{ apiKey: ApiKey; key: string }>(`${orgUrl(organizationId)}/api-keys`, input),
    );
  }

  revokeKey(organizationId: string, id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${orgUrl(organizationId)}/api-keys/${id}`));
  }

  webhooks(organizationId: string): Promise<WebhookEndpoint[]> {
    return firstValueFrom(this.http.get<WebhookEndpoint[]>(`${orgUrl(organizationId)}/webhooks`));
  }

  /** The answer is the only time the signing secret is seen. */
  createWebhook(
    organizationId: string,
    input: { url: string; events: readonly string[] },
  ): Promise<{ endpoint: WebhookEndpoint; secret: string }> {
    return firstValueFrom(
      this.http.post<{ endpoint: WebhookEndpoint; secret: string }>(
        `${orgUrl(organizationId)}/webhooks`,
        input,
      ),
    );
  }

  updateWebhook(
    organizationId: string,
    id: string,
    input: { url: string; events: readonly string[]; enabled: boolean },
  ): Promise<WebhookEndpoint> {
    return firstValueFrom(
      this.http.put<WebhookEndpoint>(`${orgUrl(organizationId)}/webhooks/${id}`, input),
    );
  }

  deleteWebhook(organizationId: string, id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${orgUrl(organizationId)}/webhooks/${id}`));
  }

  rotateSecret(
    organizationId: string,
    id: string,
  ): Promise<{ endpoint: WebhookEndpoint; secret: string }> {
    return firstValueFrom(
      this.http.post<{ endpoint: WebhookEndpoint; secret: string }>(
        `${orgUrl(organizationId)}/webhooks/${id}/rotate-secret`,
        {},
      ),
    );
  }

  sendTest(organizationId: string, id: string): Promise<void> {
    return firstValueFrom(
      this.http.post<void>(`${orgUrl(organizationId)}/webhooks/${id}/test`, {}),
    );
  }

  deliveries(organizationId: string, id: string, page: number): Promise<DeliveryPage> {
    return firstValueFrom(
      this.http.get<DeliveryPage>(`${orgUrl(organizationId)}/webhooks/${id}/deliveries`, {
        params: new HttpParams({ fromObject: { page: String(page), pageSize: '10' } }),
      }),
    );
  }

  redeliver(organizationId: string, id: string, deliveryId: string): Promise<void> {
    return firstValueFrom(
      this.http.post<void>(
        `${orgUrl(organizationId)}/webhooks/${id}/deliveries/${deliveryId}/redeliver`,
        {},
      ),
    );
  }
}

/** A CSV download link: a plain navigation, so the browser streams the file to disk (SPEC-V2 5.8). */
export function exportHref(
  path: string,
  params: Readonly<Record<string, string | readonly string[]>>,
): string {
  const search = new URLSearchParams();
  for (const [key, value] of Object.entries(params)) {
    for (const v of typeof value === 'string' ? [value] : value) {
      search.append(key, v);
    }
  }
  const query = search.toString();
  return query ? `${path}?${query}` : path;
}
