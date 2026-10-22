export type Role = 'owner' | 'admin' | 'member' | 'viewer';

export const roleLabels: Readonly<Record<Role, string>> = {
  owner: 'Owner',
  admin: 'Admin',
  member: 'Member',
  viewer: 'Viewer',
};

/** Permission names from the API (SPEC 2). The SPA never derives them from the role (D008). */
export type Permission =
  | 'org.read'
  | 'org.manage'
  | 'org.delete'
  | 'billing.manage'
  | 'members.manage'
  | 'clients.read'
  | 'clients.write'
  | 'clients.delete'
  | 'jobs.read'
  | 'jobs.create'
  | 'jobs.manage'
  | 'jobs.work'
  | 'audit.read';

export interface Membership {
  readonly organizationId: string;
  readonly slug: string;
  readonly name: string;
  readonly role: Role;
  readonly permissions: readonly Permission[];
  /** Roles this member may give others, and invite with; from the API's rules, never derived here. */
  readonly grantableRoles: readonly Role[];
  readonly invitableRoles: readonly Role[];
}

export interface Organization {
  readonly id: string;
  readonly name: string;
  readonly slug: string;
  readonly plan: 'free' | 'team' | 'business';
  readonly timeZone: string;
  readonly createdAt: string;
}

export interface SessionUser {
  readonly id: string;
  readonly email: string;
  readonly displayName: string;
  readonly memberships: readonly Membership[];
}

export interface SessionResponse {
  readonly user: SessionUser | null;
}

export interface SignUpRequest {
  readonly displayName: string;
  readonly email: string;
  readonly password: string;
  readonly organizationName: string;
  readonly timeZone: string;
}
