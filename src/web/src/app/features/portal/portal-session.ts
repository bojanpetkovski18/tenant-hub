import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { Injectable, inject, signal } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { catchError, throwError } from 'rxjs';
import { PortalApi, PortalSession } from './portal.api';

/**
 * The portal session for the portal in the URL (D040). Separate from the staff session: a contact is never a staff
 * user and the two cookies never stand in for each other, so the portal keeps its own state and its own sign-in.
 */
@Injectable({ providedIn: 'root' })
export class PortalSessionStore {
  private readonly api = inject(PortalApi);
  private pending: Promise<void> | null = null;
  private loadedFor: string | null = null;
  private generation = 0;

  /** The portal's slug, the business and (when signed in) the contact. */
  readonly slug = signal<string | null>(null);
  readonly session = signal<PortalSession | null>(null);
  readonly failed = signal(false);

  /** Asks once per portal; a page that needs a fresh answer calls {@link refresh}. */
  load(slug: string): Promise<void> {
    if (this.loadedFor === slug && this.pending) {
      return this.pending;
    }
    return this.refresh(slug);
  }

  refresh(slug: string): Promise<void> {
    this.loadedFor = slug;
    this.slug.set(slug);
    this.failed.set(false);
    // Only the latest question may answer: a session asked for before a sign-in (the layout asks as the link opens)
    // would otherwise arrive after it and put "signed out" back.
    const asked = ++this.generation;
    this.pending = this.api
      .session(slug)
      .then((session) => {
        if (asked === this.generation) {
          this.session.set(session);
        }
      })
      .catch(() => {
        if (asked === this.generation) {
          this.failed.set(true);
          this.session.set(null);
        }
      });
    return this.pending;
  }

  /** After a sign-in: the answer already is the session, and any older question no longer counts. */
  signedIn(slug: string, session: PortalSession): void {
    this.generation++;
    this.loadedFor = slug;
    this.slug.set(slug);
    this.session.set(session);
    this.pending = Promise.resolve();
  }

  /** The session ended (signed out, or refused by the API): keep the business, forget the contact. */
  ended(): void {
    const current = this.session();
    if (current) {
      this.session.set({ ...current, contact: null });
    }
  }

  async signOut(): Promise<void> {
    try {
      await this.api.signOut();
    } finally {
      this.ended();
    }
  }
}

/** Calls that do not need a session (asking for and spending a link, the session itself, signing out). */
const sessionFree = [
  '/api/portal/session',
  '/api/portal/sign-in/',
  '/api/portal/demo/',
  '/api/portal/sign-out',
];

/**
 * A 401 from a portal call means the portal session ended (contact removed, client archived, portal off, cookie
 * expired): forget the contact and go to this portal's sign-in page, which says why.
 */
export const portalSessionInterceptor: HttpInterceptorFn = (request, next) => {
  const store = inject(PortalSessionStore);
  const router = inject(Router);

  return next(request).pipe(
    catchError((error: unknown) => {
      if (
        error instanceof HttpErrorResponse &&
        error.status === 401 &&
        request.url.startsWith('/api/portal/') &&
        !sessionFree.some((path) => request.url.startsWith(path))
      ) {
        store.ended();
        const slug = store.slug();
        if (slug) {
          void router.navigate(['/portal', slug, 'sign-in'], { queryParams: { notice: 'ended' } });
        }
      }
      return throwError(() => error);
    }),
  );
};

/** The signed-in portal pages: without a contact for this portal, its sign-in page instead. */
export const portalSessionGuard: CanActivateFn = async (route) => {
  const slug = route.parent?.paramMap.get('slug') ?? route.paramMap.get('slug');
  const store = inject(PortalSessionStore);
  const router = inject(Router);
  if (!slug) {
    return router.createUrlTree(['/']);
  }

  await store.load(slug);
  return store.session()?.contact ? true : router.createUrlTree(['/portal', slug, 'sign-in']);
};

/** "Brightline Cleaning Co." → "BC": the business's initials, for the portal's header. */
export function initials(name: string): string {
  const words = name.split(/\s+/).filter((w) => /^[\p{L}\p{N}]/u.test(w));
  return words
    .slice(0, 2)
    .map((w) => w[0]!.toUpperCase())
    .join('');
}
