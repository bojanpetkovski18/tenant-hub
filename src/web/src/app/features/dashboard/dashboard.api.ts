import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PlanLimits, PlanName, PlanUsage } from '../../shared/plan/plan';
import { AuditEntry } from '../audit/audit.api';
import { JobStatus } from '../jobs/jobs.api';

export interface DashboardTiles {
  readonly openJobs: number;
  readonly overdueJobs: number;
  readonly completedThisMonth: number;
  readonly activeClients: number;
}

export interface StatusCount {
  readonly status: JobStatus;
  readonly count: number;
}

export interface UpcomingJob {
  readonly id: string;
  readonly label: string;
  readonly title: string;
  readonly clientName: string;
  readonly scheduledOn: string;
  readonly status: JobStatus;
  readonly isOverdue: boolean;
}

/**
 * The dashboard in one response (US-60). `today` and `monthStart` are the organization's dates, so the tiles link to
 * lists that count the same jobs. `recentActivity` is null without `audit.read`.
 */
export interface Dashboard {
  readonly today: string;
  readonly monthStart: string;
  readonly timeZone: string;
  readonly tiles: DashboardTiles;
  readonly jobsByStatus: readonly StatusCount[];
  readonly myNextSevenDays: readonly UpcomingJob[];
  readonly plan: PlanName;
  readonly usage: PlanUsage;
  readonly limits: PlanLimits;
  readonly recentActivity: readonly AuditEntry[] | null;
}

@Injectable({ providedIn: 'root' })
export class DashboardApi {
  private readonly http = inject(HttpClient);

  get(organizationId: string): Promise<Dashboard> {
    return firstValueFrom(
      this.http.get<Dashboard>(`/api/orgs/${encodeURIComponent(organizationId)}/dashboard`),
    );
  }
}
