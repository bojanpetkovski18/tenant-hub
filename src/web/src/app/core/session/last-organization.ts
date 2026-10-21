const storageKey = 'th-last-org';

/**
 * The slug of the organization used last in this browser (US-03). Only a convenience: storage may be unavailable
 * (privacy modes), and the slug is always checked against the user's memberships before use.
 */
export function readLastOrganization(): string | null {
  try {
    return localStorage.getItem(storageKey);
  } catch {
    return null;
  }
}

export function rememberLastOrganization(slug: string): void {
  try {
    localStorage.setItem(storageKey, slug);
  } catch {
    // Not remembered; acceptable.
  }
}
