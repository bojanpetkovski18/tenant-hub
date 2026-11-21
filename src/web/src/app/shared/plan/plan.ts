import { LimitName } from '../../core/http/api-problem';

export type PlanName = 'free' | 'team' | 'business';

/** What a plan includes beyond its limits (SPEC-V2 4.8). */
export type PlanFeature = 'portal' | 'integrations';

export const featureLabels: Readonly<Record<PlanFeature, string>> = {
  portal: 'Client portal',
  integrations: 'API keys and webhooks',
};

export const planFeatures: readonly PlanFeature[] = ['portal', 'integrations'];

/** A plan's limits; null is unlimited. */
export interface PlanLimits {
  readonly seats: number | null;
  readonly activeClients: number | null;
  readonly openJobs: number | null;
  readonly activeAgreements: number | null;
  /** Photo storage in bytes (SPEC-V2 4.8). */
  readonly storage: number | null;
}

export interface PlanUsage {
  readonly seats: number;
  readonly activeClients: number;
  readonly openJobs: number;
  readonly activeAgreements: number;
  readonly storage: number;
}

export const planLabels: Readonly<Record<PlanName, string>> = {
  free: 'Free',
  team: 'Team',
  business: 'Business',
};

export const limitLabels: Readonly<
  Record<LimitName, { readonly title: string; readonly unit: (n: number) => string }>
> = {
  seats: { title: 'Seats', unit: (n) => (n === 1 ? 'seat' : 'seats') },
  activeClients: {
    title: 'Active clients',
    unit: (n) => (n === 1 ? 'active client' : 'active clients'),
  },
  openJobs: { title: 'Open jobs', unit: (n) => (n === 1 ? 'open job' : 'open jobs') },
  activeAgreements: {
    title: 'Active agreements',
    unit: (n) => (n === 1 ? 'active agreement' : 'active agreements'),
  },
  storage: { title: 'Photo storage', unit: () => '' },
};

export const limitNames: readonly LimitName[] = [
  'seats',
  'activeClients',
  'openJobs',
  'activeAgreements',
  'storage',
];

/** Bytes as the API's messages say them (binary units, at most one decimal): "100 MB", "4.2 GB". */
export function formatBytes(bytes: number): string {
  const units: readonly [number, string][] = [
    [1024 ** 3, 'GB'],
    [1024 ** 2, 'MB'],
    [1024, 'KB'],
  ];
  for (const [size, unit] of units) {
    if (bytes >= size) {
      return `${Math.round((bytes / size) * 10) / 10} ${unit}`;
    }
  }
  return bytes === 1 ? '1 byte' : `${bytes} bytes`;
}

/** A limit's amount in its own unit: a count, or bytes for storage. */
export function amountText(limit: LimitName, amount: number): string {
  return limit === 'storage' ? formatBytes(amount) : String(amount);
}

/** "3 of 3 seats", "12 active clients, no limit", or "4.2 MB of 5 GB". */
export function usageText(limit: LimitName, used: number, allowed: number | null): string {
  if (limit === 'storage') {
    return allowed === null
      ? `${formatBytes(used)}, no limit`
      : `${formatBytes(used)} of ${formatBytes(allowed)}`;
  }
  const unit = limitLabels[limit].unit;
  return allowed === null
    ? `${used} ${unit(used)}, no limit`
    : `${used} of ${allowed} ${unit(allowed)}`;
}
