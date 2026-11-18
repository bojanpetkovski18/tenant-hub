import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { PagedList } from '../../shared/list/paged-list.store';
import { JobPriority, JobStatus } from '../jobs/jobs.api';

export type RecurrenceKind = 'weekly' | 'monthlyByDay' | 'monthlyByWeekday';
export type Weekday =
  'monday' | 'tuesday' | 'wednesday' | 'thursday' | 'friday' | 'saturday' | 'sunday';
export type WeekOfMonth = 'first' | 'second' | 'third' | 'fourth' | 'last';
export type AgreementStatus = 'active' | 'paused' | 'ended';
export type GenerationHold = 'planLimit' | 'clientArchived';

/** When the visits fall (SPEC-V2 5.1); only the fields of the kind matter. */
export interface Recurrence {
  readonly kind: RecurrenceKind;
  readonly interval: number;
  readonly weekdays: readonly Weekday[];
  readonly day: number | null;
  readonly lastDay: boolean;
  readonly week: WeekOfMonth | null;
  readonly weekday: Weekday | null;
}

export interface AgreementListItem {
  readonly id: string;
  readonly title: string;
  readonly clientId: string;
  readonly clientName: string;
  /** The recurrence in words, from the API ("Every 2 weeks on Mon, Thu"). */
  readonly schedule: string;
  readonly status: AgreementStatus;
  readonly pausedReason: GenerationHold | null;
  readonly assigneeName: string | null;
  readonly nextVisitOn: string | null;
}

export interface VisitItem {
  readonly id: string;
  readonly label: string;
  readonly occurrenceOn: string;
  readonly scheduledOn: string | null;
  readonly status: JobStatus;
  readonly assigneeName: string | null;
  readonly changedByHand: boolean;
}

export interface Agreement {
  readonly id: string;
  readonly clientId: string;
  readonly clientName: string;
  readonly title: string;
  readonly description: string | null;
  readonly assigneeUserId: string | null;
  readonly assigneeName: string | null;
  readonly priority: JobPriority;
  readonly quotedAmount: number | null;
  readonly estimatedMinutes: number | null;
  readonly recurrence: Recurrence;
  readonly schedule: string;
  readonly startsOn: string;
  readonly endsOn: string | null;
  readonly maxVisits: number | null;
  readonly status: AgreementStatus;
  readonly pausedReason: GenerationHold | null;
  /** The last date generation has covered; visits exist up to it. */
  readonly generatedThrough: string | null;
  readonly createdAt: string;
  readonly updatedAt: string;
  readonly version: number;
  readonly upcomingVisits: readonly VisitItem[];
  /** Visits someone edited: changes to the agreement no longer reach them. */
  readonly changedByHand: readonly VisitItem[];
  /** Decided by the API from the caller's permissions (D008). */
  readonly canManage: boolean;
}

export interface RecurrenceInput {
  readonly kind: RecurrenceKind;
  readonly interval: number | null;
  readonly weekdays?: readonly Weekday[];
  readonly day?: number | null;
  readonly lastDay?: boolean;
  readonly week?: WeekOfMonth | null;
  readonly weekday?: Weekday | null;
}

export interface AgreementInput {
  readonly clientId?: string | null;
  readonly title: string;
  readonly description: string | null;
  readonly assigneeUserId: string | null;
  readonly priority: JobPriority;
  readonly quotedAmount: number | null;
  readonly estimatedMinutes: number | null;
  readonly recurrence: RecurrenceInput;
  readonly startsOn: string | null;
  readonly endsOn: string | null;
  readonly maxVisits: number | null;
}

export interface PreviewInput {
  readonly recurrence: RecurrenceInput;
  readonly startsOn: string | null;
  readonly endsOn: string | null;
  readonly maxVisits: number | null;
}

export const weekdays: readonly Weekday[] = [
  'monday',
  'tuesday',
  'wednesday',
  'thursday',
  'friday',
  'saturday',
  'sunday',
];

export const weekdayLabels: Readonly<Record<Weekday, string>> = {
  monday: 'Monday',
  tuesday: 'Tuesday',
  wednesday: 'Wednesday',
  thursday: 'Thursday',
  friday: 'Friday',
  saturday: 'Saturday',
  sunday: 'Sunday',
};

export const weekLabels: Readonly<Record<WeekOfMonth, string>> = {
  first: 'First',
  second: 'Second',
  third: 'Third',
  fourth: 'Fourth',
  last: 'Last',
};

export const agreementStatusLabels: Readonly<Record<AgreementStatus, string>> = {
  active: 'Active',
  paused: 'Paused',
  ended: 'Ended',
};

/** What the agreement page says when visits are not being generated although they could be. */
export const holdMessages: Readonly<Record<GenerationHold, string>> = {
  planLimit:
    "Generation paused: the plan's open-jobs limit is reached. New visits are added as soon as there is room.",
  clientArchived: 'Paused because the client was archived. Restore the client, then resume.',
};

export const agreementsUrl = (organizationId: string) =>
  `/api/orgs/${encodeURIComponent(organizationId)}/agreements`;

/** The agreement endpoints (SPEC-V2 7); the organization is always part of the URL. */
@Injectable({ providedIn: 'root' })
export class AgreementsApi {
  private readonly http = inject(HttpClient);

  /** One page of agreements, for example a client's (`clientId`). */
  list(
    organizationId: string,
    params: Record<string, string>,
  ): Promise<PagedList<AgreementListItem>> {
    return firstValueFrom(
      this.http.get<PagedList<AgreementListItem>>(agreementsUrl(organizationId), { params }),
    );
  }

  get(organizationId: string, id: string): Promise<Agreement> {
    return firstValueFrom(this.http.get<Agreement>(`${agreementsUrl(organizationId)}/${id}`));
  }

  create(organizationId: string, input: AgreementInput): Promise<Agreement> {
    return firstValueFrom(this.http.post<Agreement>(agreementsUrl(organizationId), input));
  }

  update(
    organizationId: string,
    id: string,
    input: AgreementInput,
    version: number,
  ): Promise<Agreement> {
    return firstValueFrom(
      this.http.put<Agreement>(`${agreementsUrl(organizationId)}/${id}`, { ...input, version }),
    );
  }

  /** `pause`, `resume` or `end`; each answers with the agreement as it is now. */
  act(organizationId: string, id: string, action: 'pause' | 'resume' | 'end'): Promise<Agreement> {
    return firstValueFrom(
      this.http.post<Agreement>(`${agreementsUrl(organizationId)}/${id}/${action}`, {}),
    );
  }

  /** The next dates of a schedule, without saving (the form's live preview). */
  async preview(organizationId: string, input: PreviewInput): Promise<readonly string[]> {
    const result = await firstValueFrom(
      this.http.post<{ dates: string[] }>(`${agreementsUrl(organizationId)}/preview`, input),
    );
    return result.dates;
  }
}
