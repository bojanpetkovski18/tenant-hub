import { HttpClient, HttpParams } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Job, JobPriority, JobStatus } from '../jobs/jobs.api';

export interface BoardCard {
  readonly id: string;
  readonly number: number;
  readonly label: string;
  readonly title: string;
  readonly clientId: string;
  readonly clientName: string;
  readonly assigneeUserId: string | null;
  readonly scheduledOn: string | null;
  readonly dueOn: string | null;
  readonly status: JobStatus;
  readonly priority: JobPriority;
  readonly estimatedMinutes: number | null;
  readonly isVisit: boolean;
  /** Sent back with a move; a newer one on the server means someone else moved it first (409). */
  readonly version: number;
}

export interface BoardPerson {
  readonly userId: string;
  readonly name: string;
  /** False for a former member who still has work this week. */
  readonly active: boolean;
}

export interface BoardLoad {
  readonly assigneeUserId: string | null;
  readonly day: string;
  readonly minutes: number;
  readonly jobs: number;
}

/** One week of the board (SPEC-V2 4.3), as `GET /board` returns it. */
export interface BoardView {
  readonly weekStart: string;
  readonly today: string;
  readonly dailyCapacityMinutes: number;
  readonly people: readonly BoardPerson[];
  readonly cards: readonly BoardCard[];
  readonly unscheduled: readonly BoardCard[];
  readonly unscheduledTotal: number;
  readonly load: readonly BoardLoad[];
  /** Whether the caller may move cards (`jobs.manage`), decided by the API (D008). */
  readonly canPlan: boolean;
}

export interface BoardMove {
  readonly assigneeUserId: string | null;
  readonly scheduledOn: string;
  readonly version: number;
  readonly moveDueDate?: boolean;
}

/** "8 h", "7 h 30 min", "45 min", as the API words durations. */
export function hoursText(minutes: number): string {
  const h = Math.floor(minutes / 60);
  const m = minutes % 60;
  return h === 0 ? `${m} min` : m === 0 ? `${h} h` : `${h} h ${m} min`;
}

/** The ISO date `days` after `date` (dates only, no time zone involved). */
export function addDays(date: string, days: number): string {
  const [y, m, d] = date.split('-').map(Number);
  const result = new Date(Date.UTC(y!, m! - 1, d! + days));
  return result.toISOString().slice(0, 10);
}

/** "Tuesday 18 November": how moves are announced and days are named. */
export function dayText(date: string): string {
  return new Intl.DateTimeFormat('en-GB', {
    weekday: 'long',
    day: 'numeric',
    month: 'long',
    timeZone: 'UTC',
  }).format(new Date(`${date}T00:00:00Z`));
}

@Injectable({ providedIn: 'root' })
export class BoardApi {
  private readonly http = inject(HttpClient);

  get(
    organizationId: string,
    params: Record<string, string | readonly string[]>,
  ): Promise<BoardView> {
    return firstValueFrom(
      this.http.get<BoardView>(`/api/orgs/${encodeURIComponent(organizationId)}/board`, {
        params: new HttpParams({ fromObject: params }),
      }),
    );
  }

  /** A board move (`POST /jobs/{id}/schedule`): assignee and day in one step, with the version shown. */
  move(organizationId: string, jobId: string, move: BoardMove): Promise<Job> {
    return firstValueFrom(
      this.http.post<Job>(
        `/api/orgs/${encodeURIComponent(organizationId)}/jobs/${jobId}/schedule`,
        move,
      ),
    );
  }
}
