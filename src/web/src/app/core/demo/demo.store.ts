import { HttpClient } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Role } from '../session/session.models';
import { SessionStore } from '../session/session.store';

export interface DemoAccount {
  readonly role: Role;
  readonly displayName: string;
  readonly email: string;
}

/** The public demo (SPEC 4.1): the accounts behind "Try as …" and the reset schedule for the banner. */
export interface DemoInfo {
  readonly resetIntervalHours: number;
  readonly nextResetAt: string | null;
  readonly accounts: readonly DemoAccount[];
}

/** What each role can do, in one line, for the sign-in buttons (the API's role map, SPEC 2, in words). */
export const demoRoleHints: Readonly<Record<Role, string>> = {
  owner: 'Everything, including the plan and deleting the organization.',
  admin: 'Runs the team and the work: members, clients, jobs, settings and the audit log.',
  member: 'Adds clients and jobs and works on the jobs assigned to them.',
  viewer: 'Sees clients and jobs but changes nothing.',
};

/** "every hour", "every 6 hours". */
export function resetIntervalText(hours: number): string {
  return hours === 1 ? 'every hour' : `every ${hours} hours`;
}

/**
 * The demo's accounts and schedule, asked once and only when the session says this is the public demo: elsewhere
 * the endpoint answers 404, and the browser would log that on every page.
 */
@Injectable({ providedIn: 'root' })
export class DemoStore {
  private readonly http = inject(HttpClient);
  private readonly session = inject(SessionStore);
  private pending: Promise<void> | null = null;

  readonly info = signal<DemoInfo | null>(null);
  readonly loaded = signal(false);

  load(): Promise<void> {
    if (!this.session.demoMode()) {
      this.loaded.set(true);
      return Promise.resolve();
    }
    this.pending ??= this.fetch();
    return this.pending;
  }

  private async fetch(): Promise<void> {
    try {
      this.info.set(await firstValueFrom(this.http.get<DemoInfo>('/api/demo/accounts')));
    } catch {
      this.info.set(null);
    } finally {
      this.loaded.set(true);
    }
  }
}
