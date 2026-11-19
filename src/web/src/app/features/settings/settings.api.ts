import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PlanFeature, PlanLimits, PlanName, PlanUsage } from '../../shared/plan/plan';

export type { PlanFeature, PlanLimits, PlanName, PlanUsage } from '../../shared/plan/plan';
export {
  amountText,
  featureLabels,
  formatBytes,
  limitLabels,
  limitNames,
  planFeatures,
  planLabels,
} from '../../shared/plan/plan';

/** The organization with its usage against its plan, and every plan to compare (US-50). */
export interface OrganizationOverview {
  readonly id: string;
  readonly name: string;
  readonly slug: string;
  readonly plan: PlanName;
  readonly timeZone: string;
  /** A person's working day, which the board compares each day's planned work with (SPEC-V2 4.3). */
  readonly dailyCapacityMinutes: number;
  readonly createdAt: string;
  readonly usage: PlanUsage;
  readonly limits: PlanLimits;
  readonly features: readonly PlanFeature[];
  readonly plans: readonly {
    readonly plan: PlanName;
    readonly limits: PlanLimits;
    readonly features: readonly PlanFeature[];
  }[];
}

const orgUrl = (organizationId: string) => `/api/orgs/${encodeURIComponent(organizationId)}`;

/** Organization settings (SPEC 7): usage, name and time zone, plan ("demo billing") and deletion. */
@Injectable({ providedIn: 'root' })
export class SettingsApi {
  private readonly http = inject(HttpClient);

  get(organizationId: string): Promise<OrganizationOverview> {
    return firstValueFrom(this.http.get<OrganizationOverview>(orgUrl(organizationId)));
  }

  update(
    organizationId: string,
    changes: { name: string; timeZone: string; dailyCapacityMinutes: number },
  ): Promise<OrganizationOverview> {
    return firstValueFrom(this.http.patch<OrganizationOverview>(orgUrl(organizationId), changes));
  }

  /** <c>confirmFeatureLoss</c>: the Owner saw which features in use the downgrade turns off (SPEC-V2 4.8). */
  changePlan(
    organizationId: string,
    plan: PlanName,
    confirmFeatureLoss = false,
  ): Promise<OrganizationOverview> {
    return firstValueFrom(
      this.http.put<OrganizationOverview>(`${orgUrl(organizationId)}/plan`, {
        plan,
        confirmFeatureLoss,
      }),
    );
  }

  delete(organizationId: string, confirmName: string): Promise<void> {
    return firstValueFrom(
      this.http.delete<void>(orgUrl(organizationId), { body: { confirmName } }),
    );
  }
}
