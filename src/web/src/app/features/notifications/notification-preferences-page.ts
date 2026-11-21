import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { RouterLink } from '@angular/router';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';
import { ErrorState } from '../../shared/ui/states';
import { NotificationPreference, NotificationsApi } from './notifications.api';

/**
 * US-101: which notifications also come by email. Every notification appears in the app; these choices only add the
 * email. They are the person's own, the same in every organization.
 */
@Component({
  selector: 'app-notification-preferences-page',
  imports: [RouterLink, ErrorState],
  template: `
    <div class="mx-auto max-w-xl space-y-6">
      <div>
        <a routerLink="/account" class="text-sm text-muted hover:text-fg">← Account</a>
        <h1 class="mt-2 text-2xl font-semibold tracking-tight">Notifications</h1>
        <p class="mt-1 text-sm text-muted">
          Everything appears under the bell in the app. Choose what should also come by email; this
          applies in every organization you belong to.
        </p>
      </div>

      @if (loadError(); as error) {
        <app-error-state [message]="error.title" (retry)="load()" />
      } @else if (preferences(); as list) {
        <form class="th-card" (submit)="$event.preventDefault(); save()" novalidate>
          <fieldset>
            <legend class="text-lg font-semibold">Also by email</legend>
            <ul class="mt-3 space-y-3">
              @for (p of list; track p.type) {
                <li>
                  <label class="flex items-start gap-3 text-sm">
                    <input
                      type="checkbox"
                      class="mt-0.5 size-4 accent-accent"
                      [checked]="p.email"
                      (change)="set(p, $any($event.target).checked)"
                    />
                    <span>{{ p.label }}</span>
                  </label>
                </li>
              }
            </ul>
          </fieldset>
          @if (saveError(); as message) {
            <div class="th-alert-error mt-4" role="alert">{{ message }}</div>
          }
          @if (saved()) {
            <div class="th-alert-success mt-4" role="status">Saved.</div>
          }
          <button type="submit" class="th-button mt-5" [disabled]="saving()">
            {{ saving() ? 'Saving…' : 'Save' }}
          </button>
        </form>
      } @else {
        <div class="th-card space-y-3" aria-busy="true">
          @for (row of [1, 2, 3, 4]; track row) {
            <div class="h-5 animate-pulse rounded bg-surface-2"></div>
          }
        </div>
      }
    </div>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NotificationPreferencesPage {
  private readonly api = inject(NotificationsApi);

  protected readonly preferences = signal<NotificationPreference[] | null>(null);
  protected readonly loadError = signal<ApiProblem | null>(null);
  protected readonly saveError = signal<string | null>(null);
  protected readonly saving = signal(false);
  protected readonly saved = signal(false);

  constructor() {
    void this.load();
  }

  async load(): Promise<void> {
    this.loadError.set(null);
    try {
      this.preferences.set(await this.api.preferences());
    } catch (error) {
      this.loadError.set(toApiProblem(error));
    }
  }

  protected set(preference: NotificationPreference, email: boolean): void {
    this.saved.set(false);
    this.preferences.update((list) =>
      (list ?? []).map((p) => (p.type === preference.type ? { ...p, email } : p)),
    );
  }

  protected async save(): Promise<void> {
    this.saving.set(true);
    this.saveError.set(null);
    this.saved.set(false);
    try {
      this.preferences.set(
        await this.api.savePreferences(
          (this.preferences() ?? []).map(({ type, email }) => ({ type, email })),
        ),
      );
      this.saved.set(true);
    } catch (error) {
      this.saveError.set(toApiProblem(error).title);
    } finally {
      this.saving.set(false);
    }
  }
}
