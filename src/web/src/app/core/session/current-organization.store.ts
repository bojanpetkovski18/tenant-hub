import { HttpClient } from '@angular/common/http';
import { inject } from '@angular/core';
import { patchState, signalStore, withMethods, withState } from '@ngrx/signals';
import { firstValueFrom } from 'rxjs';
import { Organization } from './session.models';

interface CurrentOrganizationState {
  readonly organization: Organization | null;
  readonly loading: boolean;
}

/**
 * The organization the app shell is showing, loaded from the tenant API when the shell opens it. The request is
 * also the server-side membership check: if access was lost, it fails and the tenant-access interceptor reacts.
 */
export const CurrentOrganizationStore = signalStore(
  { providedIn: 'root' },
  withState<CurrentOrganizationState>({ organization: null, loading: false }),
  withMethods((store, http = inject(HttpClient)) => ({
    async load(organizationId: string): Promise<void> {
      if (store.organization()?.id === organizationId) {
        return;
      }
      patchState(store, { organization: null, loading: true });
      try {
        const organization = await firstValueFrom(
          http.get<Organization>(`/api/orgs/${encodeURIComponent(organizationId)}`),
        );
        patchState(store, { organization });
      } catch {
        // Handled by the interceptors (session ended, access lost); the shell keeps working from the session.
      } finally {
        patchState(store, { loading: false });
      }
    },
  })),
);
