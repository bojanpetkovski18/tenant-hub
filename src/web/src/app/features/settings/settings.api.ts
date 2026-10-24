import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PlanLimits, PlanName, PlanUsage } from '../../shared/plan/plan';

export type { PlanLimits, PlanName, PlanUsage } from '../../shared/plan/plan';
export { limitLabels, limitNames, planLabels } from '../../shared/plan/plan';

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
