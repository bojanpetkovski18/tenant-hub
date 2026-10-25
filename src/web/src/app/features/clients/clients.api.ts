import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';

export type ClientStatus = 'active' | 'archived';
export type ClientStatusFilter = ClientStatus | 'all';

export interface ClientListItem {
  readonly id: string;
  readonly name: string;
  readonly contactName: string | null;
  readonly email: string | null;
  readonly phone: string | null;
  readonly status: ClientStatus;
  readonly updatedAt: string;
}

export interface Client extends ClientListItem {
  readonly address: string | null;
  readonly notes: string | null;
  readonly createdAt: string;
  /** Concurrency token: sent back with an update; a newer one on the server means someone else saved first. */
  readonly version: number;
}

export interface ClientInput {
  readonly name: string;
  readonly contactName: string;
  readonly email: string;
  readonly phone: string;
  readonly address: string;
  readonly notes: string;
}

/** A client as a picker shows it (job forms and filters). */
export interface ClientOption {
  readonly id: string;
  readonly name: string;
  readonly status: ClientStatus;
}

export const clientsUrl = (organizationId: string) =>
  `/api/orgs/${encodeURIComponent(organizationId)}/clients`;

/** The client endpoints (SPEC 7); the organization is always part of the URL. */
@Injectable({ providedIn: 'root' })
export class ClientsApi {
  private readonly http = inject(HttpClient);

  /**
   * Every client by name, for pickers: one page of the maximum size (100). Organizations with more clients find
   * the rest through the client pages, which link to their jobs (D029).
   */
  async options(organizationId: string): Promise<ClientOption[]> {
    const page = await firstValueFrom(
      this.http.get<{ items: ClientListItem[] }>(clientsUrl(organizationId), {
        params: { status: 'all', sort: 'name', pageSize: '100' },
      }),
    );
    return page.items.map(({ id, name, status }) => ({ id, name, status }));
  }

  get(organizationId: string, id: string): Promise<Client> {
    return firstValueFrom(this.http.get<Client>(`${clientsUrl(organizationId)}/${id}`));
  }

  create(organizationId: string, input: ClientInput): Promise<Client> {
    return firstValueFrom(this.http.post<Client>(clientsUrl(organizationId), input));
  }

  update(organizationId: string, id: string, input: ClientInput, version: number): Promise<Client> {
    return firstValueFrom(
      this.http.put<Client>(`${clientsUrl(organizationId)}/${id}`, { ...input, version }),
    );
  }

  archive(organizationId: string, id: string): Promise<Client> {
    return firstValueFrom(
      this.http.post<Client>(`${clientsUrl(organizationId)}/${id}/archive`, {}),
    );
  }

  restore(organizationId: string, id: string): Promise<Client> {
    return firstValueFrom(
      this.http.post<Client>(`${clientsUrl(organizationId)}/${id}/restore`, {}),
    );
  }

  delete(organizationId: string, id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${clientsUrl(organizationId)}/${id}`));
  }
}
