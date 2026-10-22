import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { Role, SessionResponse } from '../../core/session/session.models';

/** A member, with what the signed-in user may do to them (computed by the API's rules, SPEC 5.2). */
export interface Member {
  readonly userId: string;
  readonly displayName: string;
  readonly email: string;
  readonly role: Role;
  readonly joinedAt: string;
  readonly isYou: boolean;
  readonly canChangeRole: boolean;
  readonly canRemove: boolean;
  readonly canLeave: boolean;
}

export type InvitationState = 'pending' | 'expired' | 'revoked' | 'accepted';

export interface Invitation {
  readonly id: string;
  readonly email: string;
  readonly role: Role;
  readonly state: InvitationState;
  readonly createdAt: string;
  readonly expiresAt: string;
  readonly invitedBy: string;
}

/** `sent`: emailed. `link` (email off) or `failed`: the inviter passes the link on (D011). */
export type InvitationDelivery = 'sent' | 'link' | 'failed';

export interface InvitationSent {
  readonly invitation: Invitation;
  readonly delivery: InvitationDelivery;
  readonly link: string | null;
}

/** What the accept page shows for a link (SPEC 7); the email is masked. */
export interface InvitationPreview {
  readonly organizationName: string;
  readonly invitedBy: string;
  readonly role: Role;
  readonly maskedEmail: string;
  readonly state: InvitationState;
  readonly expiresAt: string;
  readonly accountExists: boolean;
  /** Null when signed out. */
  readonly signedInEmailMatches: boolean | null;
  /** Set when the signed-in user already belongs to the organization. */
  readonly memberOfOrganizationId: string | null;
}

export interface AcceptedInvitation {
  readonly organizationId: string;
  readonly alreadyMember: boolean;
  readonly session: SessionResponse;
}

const orgUrl = (organizationId: string) => `/api/orgs/${encodeURIComponent(organizationId)}`;

/** Members and invitations (SPEC 7). Invitation tokens go in request bodies, never in URLs (D028). */
@Injectable({ providedIn: 'root' })
export class MembersApi {
  private readonly http = inject(HttpClient);

  members(organizationId: string): Promise<Member[]> {
    return firstValueFrom(this.http.get<Member[]>(`${orgUrl(organizationId)}/members`));
  }

  changeRole(organizationId: string, userId: string, role: Role): Promise<void> {
    return firstValueFrom(
      this.http.patch<void>(`${orgUrl(organizationId)}/members/${userId}`, { role }),
    );
  }

  remove(organizationId: string, userId: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${orgUrl(organizationId)}/members/${userId}`));
  }

  leave(organizationId: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${orgUrl(organizationId)}/members/me`));
  }

  invitations(organizationId: string): Promise<Invitation[]> {
    return firstValueFrom(this.http.get<Invitation[]>(`${orgUrl(organizationId)}/invitations`));
  }

  invite(organizationId: string, email: string, role: Role): Promise<InvitationSent> {
    return firstValueFrom(
      this.http.post<InvitationSent>(`${orgUrl(organizationId)}/invitations`, { email, role }),
    );
  }

  resend(organizationId: string, id: string): Promise<InvitationSent> {
    return firstValueFrom(
      this.http.post<InvitationSent>(`${orgUrl(organizationId)}/invitations/${id}/resend`, {}),
    );
  }

  revoke(organizationId: string, id: string): Promise<void> {
    return firstValueFrom(this.http.delete<void>(`${orgUrl(organizationId)}/invitations/${id}`));
  }

  preview(token: string): Promise<InvitationPreview> {
    return firstValueFrom(this.http.post<InvitationPreview>('/api/invitations/preview', { token }));
  }

  accept(token: string): Promise<AcceptedInvitation> {
    return firstValueFrom(this.http.post<AcceptedInvitation>('/api/invitations/accept', { token }));
  }

  signUp(token: string, displayName: string, password: string): Promise<AcceptedInvitation> {
    return firstValueFrom(
      this.http.post<AcceptedInvitation>('/api/invitations/sign-up', {
        token,
        displayName,
        password,
      }),
    );
  }
}
