import { CdkConnectedOverlay, CdkOverlayOrigin, ConnectedPosition } from '@angular/cdk/overlay';
import { DatePipe, DOCUMENT } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DestroyRef,
  ElementRef,
  Injector,
  afterNextRender,
  effect,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router } from '@angular/router';
import { filter } from 'rxjs';
import {
  AppNotification,
  NotificationList,
  NotificationsApi,
  notificationRoute,
} from '../../features/notifications/notifications.api';

/** How often the count is asked for while the tab is visible (SPEC-V2 5.7); no websockets. */
export const unreadRefreshMs = 60_000;

/**
 * US-100: the bell in the shell. The count comes from the API on every navigation and every minute while the tab is
 * visible; a hidden tab asks nothing. The panel (a CDK overlay, D023) lists the latest notifications: following one
 * marks it read; "Mark all as read" clears the count. Escape or a click outside closes it and focus returns to the bell.
 */
@Component({
  selector: 'app-notification-bell',
  imports: [CdkOverlayOrigin, CdkConnectedOverlay, DatePipe],
  template: `
    <button
      #bell
      type="button"
      cdkOverlayOrigin
      #origin="cdkOverlayOrigin"
      class="relative flex size-10 items-center justify-center rounded-full text-muted hover:bg-surface-2 hover:text-fg"
      aria-controls="notification-panel"
      [attr.aria-expanded]="open()"
      [attr.aria-label]="
        unread() === 0 ? 'Notifications' : 'Notifications, ' + unread() + ' unread'
      "
      (click)="toggle()"
    >
      <svg
        class="size-5"
        viewBox="0 0 24 24"
        fill="none"
        stroke="currentColor"
        stroke-width="2"
        aria-hidden="true"
      >
        <path d="M6 8a6 6 0 1 1 12 0c0 7 3 9 3 9H3s3-2 3-9M10.3 21a1.9 1.9 0 0 0 3.4 0" />
      </svg>
      @if (unread() > 0) {
        <span
          class="absolute right-1 top-1 min-w-5 rounded-full bg-danger px-1 text-center text-xs font-semibold leading-5 text-white"
          aria-hidden="true"
          >{{ unread() > 99 ? '99+' : unread() }}</span
        >
      }
    </button>
    <p class="sr-only" aria-live="polite" aria-atomic="true">{{ announcement() }}</p>

    <ng-template
      cdkConnectedOverlay
      [cdkConnectedOverlayOrigin]="origin"
      [cdkConnectedOverlayOpen]="open()"
      [cdkConnectedOverlayPositions]="positions"
      [cdkConnectedOverlayHasBackdrop]="true"
      [cdkConnectedOverlayPush]="true"
      [cdkConnectedOverlayViewportMargin]="16"
      cdkConnectedOverlayBackdropClass="cdk-overlay-transparent-backdrop"
      (backdropClick)="close()"
      (detach)="close()"
      (overlayKeydown)="keydown($event)"
    >
      <section
        id="notification-panel"
        class="th-menu w-[min(24rem,calc(100vw-2rem))] p-0"
        aria-labelledby="notification-heading"
      >
        <div class="flex items-center justify-between gap-2 border-b border-border px-4 py-3">
          <h2
            id="notification-heading"
            tabindex="-1"
            class="text-sm font-semibold focus:outline-none"
          >
            Notifications
          </h2>
          @if (unread() > 0) {
            <button
              type="button"
              class="text-sm font-medium text-accent hover:underline"
              (click)="markAll()"
            >
              Mark all as read
            </button>
          }
        </div>
        @if (error()) {
          <div class="px-4 py-6 text-sm">
            <p>The notifications could not be loaded.</p>
            <button type="button" class="th-button-secondary mt-2" (click)="load()">
              Try again
            </button>
          </div>
        } @else if (list(); as l) {
          @if (l.items.length === 0) {
            <p class="px-4 py-6 text-sm text-muted">
              Nothing yet. You will see here what concerns you.
            </p>
          } @else {
            <ul class="max-h-[min(28rem,70vh)] divide-y divide-border overflow-y-auto">
              @for (n of l.items; track n.id) {
                <li>
                  <a
                    class="flex gap-3 px-4 py-3 text-sm hover:bg-surface-2 focus-visible:bg-surface-2"
                    [href]="href(n)"
                    (click)="follow($event, n)"
                  >
                    <span
                      class="mt-1.5 size-2 shrink-0 rounded-full"
                      [class.bg-accent]="!n.readAt"
                      aria-hidden="true"
                    ></span>
                    <span class="min-w-0 flex-1">
                      <span class="block break-words" [class.font-medium]="!n.readAt">{{
                        n.text
                      }}</span>
                      <span class="mt-0.5 block text-xs text-muted">
                        <time [attr.datetime]="n.createdAt">{{
                          n.createdAt | date: 'd MMM, HH:mm'
                        }}</time>
                        @if (!n.readAt) {
                          <span class="sr-only">, unread</span>
                        }
                      </span>
                    </span>
                  </a>
                </li>
              }
            </ul>
            @if (l.total > l.items.length) {
              <p class="border-t border-border px-4 py-2 text-xs text-muted">
                Showing the latest {{ l.items.length }} of {{ l.total }}.
              </p>
            }
          }
        } @else {
          <div class="space-y-2 px-4 py-4" aria-busy="true">
            <div class="h-4 animate-pulse rounded bg-surface-2"></div>
            <div class="h-4 w-2/3 animate-pulse rounded bg-surface-2"></div>
          </div>
        }
      </section>
    </ng-template>
  `,
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class NotificationBell {
  readonly organizationId = input.required<string>();
  readonly slug = input.required<string>();

  private readonly api = inject(NotificationsApi);
  private readonly router = inject(Router);
  private readonly document = inject(DOCUMENT);
  private readonly injector = inject(Injector);
  private readonly bell = viewChild.required<ElementRef<HTMLButtonElement>>('bell');

  protected readonly open = signal(false);
  protected readonly unread = signal(0);
  protected readonly list = signal<NotificationList | null>(null);
  protected readonly error = signal(false);
  protected readonly announcement = signal('');
  protected readonly positions: ConnectedPosition[] = [
    { originX: 'end', originY: 'bottom', overlayX: 'end', overlayY: 'top', offsetY: 8 },
    { originX: 'start', originY: 'bottom', overlayX: 'start', overlayY: 'top', offsetY: 8 },
  ];
  private timer: ReturnType<typeof setInterval> | null = null;

  constructor() {
    effect(() => {
      if (this.organizationId()) {
        void this.refreshCount();
      }
    });

    this.router.events
      .pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => void this.refreshCount());

    const onVisibility = () => {
      if (this.document.visibilityState === 'visible') {
        void this.refreshCount();
        this.startTimer();
      } else {
        this.stopTimer();
      }
    };
    this.document.addEventListener('visibilitychange', onVisibility);
    if (this.document.visibilityState === 'visible') {
      this.startTimer();
    }
    inject(DestroyRef).onDestroy(() => {
      this.document.removeEventListener('visibilitychange', onVisibility);
      this.stopTimer();
    });
  }

  protected toggle(): void {
    if (this.open()) {
      this.close();
      return;
    }
    this.open.set(true);
    void this.load();
    afterNextRender(() => this.document.getElementById('notification-heading')?.focus(), {
      injector: this.injector,
    });
  }

  protected close(): void {
    if (!this.open()) {
      return;
    }
    this.open.set(false);
    this.bell().nativeElement.focus();
  }

  protected keydown(event: KeyboardEvent): void {
    if (event.key === 'Escape') {
      event.preventDefault();
      this.close();
    }
  }

  async load(): Promise<void> {
    this.error.set(false);
    try {
      const list = await this.api.list(this.organizationId());
      this.list.set(list);
      this.unread.set(list.unread);
    } catch {
      this.error.set(true);
    }
  }

  protected href(n: AppNotification): string {
    const route = notificationRoute(this.slug(), n);
    return this.router.serializeUrl(
      this.router.createUrlTree([...route.commands], { queryParams: route.query }),
    );
  }

  /** Opens where it leads; an unread one is marked read first (a failure there does not stop the navigation). */
  protected async follow(event: MouseEvent, n: AppNotification): Promise<void> {
    if (event.ctrlKey || event.metaKey || event.shiftKey || event.button !== 0) {
      return; // a new tab: let the browser do it
    }
    event.preventDefault();
    if (!n.readAt) {
      try {
        this.unread.set((await this.api.markRead(this.organizationId(), [n.id])).unread);
      } catch {
        // The next refresh shows the right count.
      }
    }
    this.open.set(false);
    const route = notificationRoute(this.slug(), n);
    await this.router.navigate([...route.commands], { queryParams: route.query });
  }

  protected async markAll(): Promise<void> {
    try {
      this.unread.set((await this.api.markRead(this.organizationId(), 'all')).unread);
      this.announcement.set('All notifications marked as read.');
      await this.load();
    } catch {
      this.announcement.set('The notifications could not be marked as read. Try again.');
    }
  }

  private async refreshCount(): Promise<void> {
    if (this.document.visibilityState !== 'visible') {
      return;
    }
    try {
      this.unread.set((await this.api.list(this.organizationId(), 1)).unread);
    } catch {
      // Keep the last count; the next refresh tries again.
    }
  }

  private startTimer(): void {
    this.stopTimer();
    this.timer = setInterval(() => void this.refreshCount(), unreadRefreshMs);
  }

  private stopTimer(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }
}
