import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { LimitName } from '../../core/http/api-problem';

export type PlanName = 'free' | 'team' | 'business';

/** A plan's limits; null is unlimited. */
export interface PlanLimits {
  readonly seats: number | null;
  readonly activeClients: number | null;
  readonly openJobs: number | null;
}

export interface PlanUsage {
  readonly seats: number;
  readonly activeClients: number;
  readonly openJobs: number;
}

/** The organization with its usage against its plan, and every plan to compare (US-50). */
export interface OrganizationOverview {
  readonly id: string;
  readonly name: string;
  readonly slug: string;
  readonly plan: PlanName;
  readonly timeZone: string;
  readonly createdAt: string;
  readonly usage: PlanUsage;
  readonly limits: PlanLimits;
  readonly plans: readonly { readonly plan: PlanName; readonly limits: PlanLimits }[];
}

export const planLabels: Readonly<Record<PlanName, string>> = {
  free: 'Free',
  team: 'Team',
  business: 'Business',
};

export const limitLabels: Readonly<
  Record<LimitName, { readonly title: string; readonly unit: (n: number) => string }>
> = {
  seats: { title: 'Seats', unit: (n) => (n === 1 ? 'seat' : 'seats') },
  activeClients: {
    title: 'Active clients',
    unit: (n) => (n === 1 ? 'active client' : 'active clients'),
  },
  openJobs: { title: 'Open jobs', unit: (n) => (n === 1 ? 'open job' : 'open jobs') },
};

export const limitNames: readonly LimitName[] = ['seats', 'activeClients', 'openJobs'];

const orgUrl = (organizationId: string) => `/api/orgs/${encodeURIComponent(organizationId)}`;

/** Organization settings (SPEC 7): usage, name and time zone, plan ("demo billing") and deletion. */
@Injectable({ providedIn: 'root' })
export class SettingsApi {
  private readonly http = inject(HttpClient);

  get(organizationId: string): Promise<OrganizationOverview> {
    return firstValueFrom(this.http.get<OrganizationOverview>(orgUrl(organizationId)));
  }

  update(organizationId: string, name: string, timeZone: string): Promise<OrganizationOverview> {
    return firstValueFrom(
      this.http.patch<OrganizationOverview>(orgUrl(organizationId), { name, timeZone }),
    );
  }

  changePlan(organizationId: string, plan: PlanName): Promise<OrganizationOverview> {
    return firstValueFrom(
      this.http.put<OrganizationOverview>(`${orgUrl(organizationId)}/plan`, { plan }),
    );
  }

  delete(organizationId: string, confirmName: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(orgUrl(organizationId), { body: { confirmName } }),
    );
  }
}
