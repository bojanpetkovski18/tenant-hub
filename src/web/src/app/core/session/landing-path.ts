import { SessionUser } from './session.models';

/**
 * Where to go after signing in (US-02): a safe return URL, else the last organization used in this browser if
 * the user still belongs to it, else their only organization, else the organization picker.
 */
export function landingPath(
  user: SessionUser,
  returnUrl: string | null,
  lastSlug: string | null,
): string {
  if (returnUrl && isSafeReturnUrl(returnUrl)) {
    return returnUrl;
  }

  const memberships = user.memberships;
  const last = memberships.find((m) => m.slug === lastSlug);
  if (last) {
    return organizationHome(last.slug);
  }

  const only = memberships.length === 1 ? memberships[0] : undefined;
  return only ? organizationHome(only.slug) : '/orgs';
}

export function organizationHome(slug: string): string {
  return `/o/${encodeURIComponent(slug)}/dashboard`;
}

/** Only paths inside this app: "/o/x/jobs" yes; "//evil.example", "https://…" or "/\evil" no (open redirect). */
export function isSafeReturnUrl(url: string): boolean {
  return /^\/(?![/\\])/.test(url) && !url.startsWith('/sign-in') && !url.startsWith('/sign-up');
}
