export type Role = 'owner' | 'admin' | 'member' | 'viewer';

export const roleLabels: Readonly<Record<Role, string>> = {
  owner: 'Owner',
  admin: 'Admin',
  member: 'Member',
  viewer: 'Viewer',
};

export interface Membership {
  readonly organizationId: string;
  readonly slug: string;
  readonly name: string;
  readonly role: Role;
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
