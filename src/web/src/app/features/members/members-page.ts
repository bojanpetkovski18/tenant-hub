import { DatePipe } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  computed,
  effect,
  inject,
  input,
  signal,
} from '@angular/core';
import { Router, RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { Role, roleLabels } from '../../core/session/session.models';
import { SessionStore } from '../../core/session/session.store';
import { Confirm } from '../../shared/ui/confirm-dialog';
import { EmptyState, ErrorState } from '../../shared/ui/states';
import { MemberDialogs } from './member-dialogs';
import { Invitation, InvitationSent, Member, MembersApi } from './members.api';

type Tab = 'members' | 'pending';

/**
 * US-10 and US-12: the organization's members and pending invitations. What each row offers comes from the API
 * (per-member flags and the session's grantable roles), so the page disables exactly what the server would refuse.
 * The tab lives in the URL (`?tab=pending`), like the list filters.
 */
@Component({
  selector: 'app-members-page',
  imports: [DatePipe, RouterLink, EmptyState, ErrorState],
  template: `
    <div class="space-y-5">
      <div class="flex flex-wrap items-center justify-between gap-3">
        <h1 class="text-2xl font-semibold tracking-tight">Members</h1>
        @if (canManage()) {
          <button type="button" class="th-button" (click)="invite()">Invite</button>
        }
      </div>

      @if (actionError(); as message) {
        <div class="th-alert-error" role="alert">{{ message }}</div>
      }
      @if (actionDone(); as message) {
        <div class="th-alert-success" role="status">{{ message }}</div>
      }

      @if (canManage()) {
        <nav aria-label="Member lists" class="flex gap-1 border-b border-border">
          <a
            routerLink="."
            [queryParams]="{ tab: null }"
            class="-mb-px border-b-2 px-3 py-2 text-sm font-medium"
            [class]="
              activeTab() === 'members'
                ? 'border-accent text-fg'
                : 'border-transparent text-muted hover:text-fg'
            "
            [attr.aria-current]="activeTab() === 'members' ? 'page' : null"
            >Members</a
          >
          <a
            routerLink="."
            [queryParams]="{ tab: 'pending' }"
            class="-mb-px border-b-2 px-3 py-2 text-sm font-medium"
            [class]="
              activeTab() === 'pending'
                ? 'border-accent text-fg'
                : 'border-transparent text-muted hover:text-fg'
            "
            [attr.aria-current]="activeTab() === 'pending' ? 'page' : null"
            >Pending invitations</a
          >
        </nav>
      }

      @if (activeTab() === 'members') {
        @if (loading() && members().length === 0) {
          <div class="space-y-3" aria-busy="true">
            @for (row of [1, 2, 3]; track row) {
              <div class="h-16 animate-pulse rounded-lg bg-surface-2"></div>
            }
          </div>
        } @else if (loadError(); as error) {
          <app-error-state [message]="error.title" (retry)="load()" />
        } @else {
          <ul
            class="divide-y divide-border rounded-lg border border-border bg-surface"
            aria-label="Members"
          >
            @for (member of members(); track member.userId) {
              <li class="flex flex-wrap items-center gap-x-4 gap-y-2 p-4">
                <div class="min-w-0 flex-1 basis-48">
                  <p class="truncate font-medium">
                    {{ member.displayName }}
                    @if (member.isYou) {
                      <span class="text-sm font-normal text-muted">(you)</span>
                    }
                  </p>
                  <p class="break-all text-sm text-muted">{{ member.email }}</p>
                </div>
                <p class="text-sm text-muted">Joined {{ member.joinedAt | date: 'mediumDate' }}</p>
                <div class="flex flex-wrap items-center gap-2">
                  @if (member.canChangeRole) {
                    <label class="sr-only" [for]="'role-' + member.userId"
                      >Role for {{ member.displayName }}</label
                    >
                    <select
                      #roleSelect
                      class="th-input w-auto"
                      [id]="'role-' + member.userId"
                      [disabled]="busy()"
                      (change)="changeRole(member, roleSelect)"
                    >
                      @for (role of rolesFor(member); track role) {
                        <option [value]="role" [selected]="role === member.role">
                          {{ roleLabels[role] }}
                        </option>
                      }
                    </select>
                  } @else {
                    <span class="th-badge">{{ roleLabels[member.role] }}</span>
                  }
                  @if (member.canRemove) {
                    <button
                      type="button"
                      class="th-button-secondary text-danger"
                      [disabled]="busy()"
                      (click)="remove(member)"
                    >
                      Remove<span class="sr-only"> {{ member.displayName }}</span>
                    </button>
                  }
                  @if (member.isYou) {
                    <button
                      type="button"
                      class="th-button-secondary"
                      [disabled]="busy() || !member.canLeave"
                      [attr.aria-describedby]="member.canLeave ? null : 'last-owner-hint'"
                      (click)="leave()"
                    >
                      Leave organization
                    </button>
                  }
                </div>
                @if (member.isYou && !member.canLeave) {
                  <p id="last-owner-hint" class="th-hint basis-full">
                    You are the last Owner. Make someone else an Owner before you leave.
                  </p>
                }
              </li>
            }
          </ul>
        }
      } @else {
        @if (loading() && invitations().length === 0) {
          <div class="space-y-3" aria-busy="true">
            @for (row of [1, 2]; track row) {
              <div class="h-16 animate-pulse rounded-lg bg-surface-2"></div>
            }
          </div>
        } @else if (loadError(); as error) {
          <app-error-state [message]="error.title" (retry)="load()" />
        } @else if (invitations().length === 0) {
          <app-empty-state
            title="No pending invitations"
            message="Invite teammates by email; their invitations show here until they accept."
          >
            <button type="button" class="th-button" (click)="invite()">Invite</button>
          </app-empty-state>
        } @else {
          <ul
            class="divide-y divide-border rounded-lg border border-border bg-surface"
            aria-label="Pending invitations"
          >
            @for (invitation of invitations(); track invitation.id) {
              <li class="flex flex-wrap items-center gap-x-4 gap-y-2 p-4">
                <div class="min-w-0 flex-1 basis-48">
                  <p class="break-all font-medium">{{ invitation.email }}</p>
                  <p class="text-sm text-muted">
                    {{ roleLabels[invitation.role] }} · invited by {{ invitation.invitedBy }}
                  </p>
                </div>
                <p class="text-sm" [class.text-danger]="invitation.state === 'expired'">
                  @if (invitation.state === 'expired') {
                    Expired {{ invitation.expiresAt | date: 'mediumDate' }}
                  } @else {
                    <span class="text-muted"
                      >Expires {{ invitation.expiresAt | date: 'mediumDate' }}</span
                    >
                  }
                </p>
                <div class="flex flex-wrap gap-2">
                  <button
                    type="button"
                    class="th-button-secondary"
                    [disabled]="busy()"
                    (click)="resend(invitation)"
                  >
                    Resend<span class="sr-only"> to {{ invitation.email }}</span>
                  </button>
                  <button
                    type="button"
                    class="th-button-secondary text-danger"
                    [disabled]="busy()"
                    (click)="revoke(invitation)"
                  >
                    Revoke<span class="sr-only"> the invitation to {{ invitation.email }}</span>
                  </button>
                </div>
              </li>
            }
          </ul>
        }
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class MembersPage {
  readonly slug = input.required<string>();
  readonly tab = input<string | null>(null);

  private readonly api = inject(MembersApi);
  private readonly session = inject(SessionStore);
  private readonly confirm = inject(Confirm);
  private readonly dialogs = inject(MemberDialogs);
  private readonly router = inject(Router);

  protected readonly roleLabels = roleLabels;
  private readonly membership = computed(() => this.session.membershipBySlug(this.slug()));
  protected readonly canManage = computed(() => this.session.can(this.slug(), 'members.manage'));
  protected readonly activeTab = computed<Tab>(() =>
    this.tab() === 'pending' && this.canManage() ? 'pending' : 'members',
  );

  protected readonly members = signal<Member[]>([]);
  protected readonly invitations = signal<Invitation[]>([]);
  protected readonly loading = signal(true);
  protected readonly loadError = signal<ApiProblem | null>(null);
  protected readonly busy = signal(false);
  protected readonly actionError = signal<string | null>(null);
  protected readonly actionDone = signal<string | null>(null);

  constructor() {
    effect(() => {
      if (this.membership()) {
        this.activeTab();
        void this.load();
      }
    });
  }

  /** The member's current role plus what the user may grant (always includes the current one). */
  protected rolesFor(member: Member): Role[] {
    const grantable = this.membership()?.grantableRoles ?? [];
    return (['owner', 'admin', 'member', 'viewer'] as const).filter(
      (role) => role === member.role || grantable.includes(role),
    );
  }

  protected async load(): Promise<void> {
    const membership = this.membership();
    if (!membership) {
      return;
    }
    this.loading.set(true);
    this.loadError.set(null);
    try {
      if (this.activeTab() === 'pending') {
        this.invitations.set(await this.api.invitations(membership.organizationId));
      } else {
        this.members.set(await this.api.members(membership.organizationId));
      }
    } catch (error) {
      this.loadError.set(toApiProblem(error));
    } finally {
      this.loading.set(false);
    }
  }

  protected async invite(): Promise<void> {
    const membership = this.membership();
    if (!membership) {
      return;
    }
    const sent = await this.dialogs.invite({
      organizationId: membership.organizationId,
      roles: membership.invitableRoles,
    });
    if (sent) {
      await this.delivered(sent, `Invitation sent to ${sent.invitation.email}.`);
    }
  }

  protected async changeRole(member: Member, select: HTMLSelectElement): Promise<void> {
    const role = select.value as Role;
    const confirmed = await this.confirm.ask({
      title: `Make ${member.displayName} ${article(role)} ${roleLabels[role]}?`,
      message: roleConsequences[role],
      confirmLabel: 'Change role',
    });
    if (!confirmed) {
      select.value = member.role;
      return;
    }
    await this.run(
      () => this.api.changeRole(this.membership()!.organizationId, member.userId, role),
      `${member.displayName} is now ${article(role)} ${roleLabels[role]}.`,
    );
    select.value = this.members().find((m) => m.userId === member.userId)?.role ?? member.role;
  }

  protected async remove(member: Member): Promise<void> {
    const confirmed = await this.confirm.ask({
      title: `Remove ${member.displayName}?`,
      message:
        'They lose access to this organization right away. Their open jobs become unassigned; history keeps their name.',
      confirmLabel: 'Remove member',
      danger: true,
    });
    if (confirmed) {
      await this.run(
        () => this.api.remove(this.membership()!.organizationId, member.userId),
        `${member.displayName} was removed.`,
      );
    }
  }

  protected async leave(): Promise<void> {
    const membership = this.membership();
    if (!membership) {
      return;
    }
    const confirmed = await this.confirm.ask({
      title: `Leave ${membership.name}?`,
      message: 'You lose access right away. To come back, someone has to invite you again.',
      confirmLabel: 'Leave organization',
      danger: true,
    });
    if (!confirmed) {
      return;
    }
    this.busy.set(true);
    this.actionError.set(null);
    try {
      await this.api.leave(membership.organizationId);
      await this.session.load();
      await this.router.navigate(['/orgs'], { queryParams: { notice: 'left' } });
    } catch (error) {
      this.actionError.set(toApiProblem(error).title);
      this.busy.set(false);
    }
  }

  protected async resend(invitation: Invitation): Promise<void> {
    this.busy.set(true);
    this.actionError.set(null);
    this.actionDone.set(null);
    try {
      const sent = await this.api.resend(this.membership()!.organizationId, invitation.id);
      this.busy.set(false);
      await this.delivered(
        sent,
        `Invitation sent again to ${invitation.email}. The old link no longer works.`,
      );
    } catch (error) {
      this.actionError.set(toApiProblem(error).title);
      this.busy.set(false);
    }
  }

  protected async revoke(invitation: Invitation): Promise<void> {
    const confirmed = await this.confirm.ask({
      title: `Revoke the invitation to ${invitation.email}?`,
      message: 'The link stops working. You can invite them again later.',
      confirmLabel: 'Revoke invitation',
      danger: true,
    });
    if (confirmed) {
      await this.run(
        () => this.api.revoke(this.membership()!.organizationId, invitation.id),
        `The invitation to ${invitation.email} was revoked.`,
      );
    }
  }

  /** After a send: the link to pass on if it was not emailed, then the list as it is now. */
  private async delivered(sent: InvitationSent, done: string): Promise<void> {
    if (sent.delivery !== 'sent' && sent.link) {
      await this.dialogs.copyLink(sent);
      this.actionDone.set(`Invitation created for ${sent.invitation.email}.`);
    } else {
      this.actionDone.set(done);
    }
    await this.load();
  }

  private async run(action: () => Promise<void>, done: string): Promise<void> {
    this.busy.set(true);
    this.actionError.set(null);
    this.actionDone.set(null);
    try {
      await action();
      this.actionDone.set(done);
    } catch (error) {
      this.actionError.set(toApiProblem(error).title);
    } finally {
      this.busy.set(false);
    }
    await this.load();
  }
}

const roleConsequences: Readonly<Record<Role, string>> = {
  owner:
    'Owners can do everything, including billing, deleting the organization and managing other Owners.',
  admin: 'Admins manage members, clients and all jobs, but not billing or Owners.',
  member: 'Members work on clients and their own jobs.',
  viewer: 'Viewers can see clients and jobs but change nothing.',
};

function article(role: Role): string {
  return role === 'owner' || role === 'admin' ? 'an' : 'a';
}
