import { LimitName } from '../../core/http/api-problem';

export type PlanName = 'free' | 'team' | 'business';

/** A plan's limits; null is unlimited. */
export interface PlanLimits {
  readonly seats: number | null;
  readonly activeClients: number | null;
  readonly openJobs: number | null;
  readonly activeAgreements: number | null;
}

export interface PlanUsage {
  readonly seats: number;
  readonly activeClients: number;
  readonly openJobs: number;
  readonly activeAgreements: number;
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
};

export const limitNames: readonly LimitName[] = [
  'seats',
  'activeClients',
  'openJobs',
  'activeAgreements',
];

/** "3 of 3 seats", or "12 active clients, no limit". */
export function usageText(limit: LimitName, used: number, allowed: number | null): string {
  const unit = limitLabels[limit].unit;
  return allowed === null
    ? `${used} ${unit(used)}, no limit`
    : `${used} of ${allowed} ${unit(allowed)}`;
}
