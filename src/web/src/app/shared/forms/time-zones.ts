/** IANA time zones the browser knows, for the organization's time zone (SPEC 5.1). The server re-validates. */
export function timeZoneOptions(): readonly string[] {
  const zones =
    typeof Intl.supportedValuesOf === 'function' ? Intl.supportedValuesOf('timeZone') : [];
  return zones.includes('UTC') ? zones : [...zones, 'UTC'];
}

/** The browser's own time zone, the default for a new organization. */
export function browserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}
