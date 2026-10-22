import { HttpClient } from '@angular/common/http';
import { computed, inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';
import {
  Membership,
  Permission,
  SessionResponse,
  SessionUser,
  SignUpRequest,
} from './session.models';

interface SessionState {
  /** False until the first /api/auth/session answer; guards wait for it (app initializer). */
  readonly loaded: boolean;
  readonly user: SessionUser | null;
}

/**
 * Who is signed in and which organizations they belong to (D014). The cookie is HttpOnly, so the session is
 * whatever the API says; every mutation returns the fresh session, and the store just holds it.
 */
export const SessionStore = signalStore(
  { providedIn: 'root' },
  withState<SessionState>({ loaded: false, user: null }),
  withComputed(({ user }) => ({
    isSignedIn: computed(() => user() !== null),
    memberships: computed(() => user()?.memberships ?? []),
  })),
  withMethods((store, http = inject(HttpClient)) => {
    const setUser = (user: SessionUser | null) => patchState(store, { loaded: true, user });

    return {
      async load(): Promise<void> {
        try {
          setUser((await firstValueFrom(http.get<SessionResponse>('/api/auth/session'))).user);
        } catch {
          // The app still works as a guest; protected pages will ask the user to sign in.
          setUser(null);
        }
      },

      async signIn(email: string, password: string): Promise<SessionUser> {
        const session = await firstValueFrom(
          http.post<SessionResponse>('/api/auth/sign-in', { email, password }),
        );
        setUser(session.user);
        return session.user!;
      },

      async signUp(request: SignUpRequest): Promise<SessionUser> {
        const session = await firstValueFrom(
          http.post<SessionResponse>('/api/auth/sign-up', request),
        );
        setUser(session.user);
        return session.user!;
      },

      async signOut(): Promise<void> {
        try {
          await firstValueFrom(http.post<void>('/api/auth/sign-out', null));
        } finally {
          // Even if the server call fails, this browser shows no account data any more.
          setUser(null);
        }
      },

      async createOrganization(name: string, timeZone: string): Promise<Membership> {
        const membership = await firstValueFrom(
          http.post<Membership>('/api/orgs', { name, timeZone }),
        );
        const user = store.user();
        if (user) {
          const memberships = [...user.memberships, membership].sort((a, b) =>
            a.name.localeCompare(b.name),
          );
          setUser({ ...user, memberships });
        }
        return membership;
      },

      async updateProfile(displayName: string): Promise<void> {
        setUser(
          (await firstValueFrom(http.patch<SessionResponse>('/api/me', { displayName }))).user,
        );
      },

      async changePassword(currentPassword: string, newPassword: string): Promise<void> {
        await firstValueFrom(http.post<void>('/api/me/password', { currentPassword, newPassword }));
      },

      /** A session the API returned from another call (accepting an invitation signs in and joins). */
      apply(session: SessionResponse): void {
        setUser(session.user);
      },

      /** The server no longer recognizes the session (401): forget it locally. */
      clear(): void {
        setUser(null);
      },

      membershipBySlug(slug: string): Membership | undefined {
        return store.user()?.memberships.find((m) => m.slug === slug);
      },

      /** Whether the user may do this in the organization; the server checks again on every request. */
      can(slug: string, permission: Permission): boolean {
        return (
          store
            .user()
            ?.memberships.find((m) => m.slug === slug)
            ?.permissions.includes(permission) ?? false
        );
      },
    };
  }),
);
