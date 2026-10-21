import { CdkTrapFocus } from '@angular/cdk/a11y';
import { NgTemplateOutlet } from '@angular/common';
import {
  ChangeDetectionStrategy,
  Component,
  DOCUMENT,
  DestroyRef,
  ElementRef,
  afterNextRender,
  inject,
  input,
  signal,
  viewChild,
} from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { NavigationEnd, Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { filter } from 'rxjs';
import { ThemeToggle } from '../theme/theme-toggle';
import { SkipLink } from './skip-link';
import { Wordmark } from './wordmark';

interface NavItem {
  readonly path: string;
  readonly label: string;
  /** SVG path data, 24×24 viewBox, stroked. */
  readonly icon: string;
}

/** The desktop sidebar is always shown from this width; below it the navigation is a drawer. */
export const desktopQuery = '(min-width: 1024px)';

/**
 * Layout inside an organization (SPEC 6). The organization switcher and user menu are placeholders until
 * M02; navigation is filtered by permission from M03.
 */
@Component({
  selector: 'app-shell',
  imports: [
    NgTemplateOutlet,
    RouterOutlet,
    RouterLink,
    RouterLinkActive,
    CdkTrapFocus,
    ThemeToggle,
    Wordmark,
    SkipLink,
  ],
  templateUrl: './app-shell.html',
  changeDetection: ChangeDetectionStrategy.OnPush,
})
export class AppShell {
  /** Route parameter `:slug`. */
  readonly slug = input.required<string>();

  protected readonly drawerOpen = signal(false);
  protected readonly navItems: readonly NavItem[] = [
    {
      path: 'dashboard',
      label: 'Dashboard',
      icon: 'M4 13h6V4H4zM14 20h6v-9h-6zM14 4v3h6V4zM4 20h6v-3H4z',
    },
    { path: 'jobs', label: 'Jobs', icon: 'M9 5h11M9 12h11M9 19h11M4 5h.01M4 12h.01M4 19h.01' },
    {
      path: 'clients',
      label: 'Clients',
      icon: 'M4 21V5a2 2 0 0 1 2-2h8a2 2 0 0 1 2 2v16M16 9h2a2 2 0 0 1 2 2v10M8 7h4M8 11h4M8 15h4M2 21h20',
    },
    {
      path: 'members',
      label: 'Members',
      icon: 'M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8M22 21v-2a4 4 0 0 0-3-3.9M16 3.1a4 4 0 0 1 0 7.8',
    },
    { path: 'audit', label: 'Audit log', icon: 'M12 8v4l3 2M3 12a9 9 0 1 0 3-6.7L3 8M3 3v5h5' },
    {
      path: 'settings',
      label: 'Settings',
      icon: 'M12 15a3 3 0 1 0 0-6 3 3 0 0 0 0 6M19.4 15a1.7 1.7 0 0 0 .3 1.8l.1.1a2 2 0 1 1-2.8 2.8l-.1-.1a1.7 1.7 0 0 0-1.8-.3 1.7 1.7 0 0 0-1 1.5V21a2 2 0 1 1-4 0v-.1a1.7 1.7 0 0 0-1.1-1.5 1.7 1.7 0 0 0-1.8.3l-.1.1a2 2 0 1 1-2.8-2.8l.1-.1a1.7 1.7 0 0 0 .3-1.8 1.7 1.7 0 0 0-1.5-1H3a2 2 0 1 1 0-4h.1a1.7 1.7 0 0 0 1.5-1.1 1.7 1.7 0 0 0-.3-1.8l-.1-.1a2 2 0 1 1 2.8-2.8l.1.1a1.7 1.7 0 0 0 1.8.3H9a1.7 1.7 0 0 0 1-1.5V3a2 2 0 1 1 4 0v.1a1.7 1.7 0 0 0 1 1.5 1.7 1.7 0 0 0 1.8-.3l.1-.1a2 2 0 1 1 2.8 2.8l-.1.1a1.7 1.7 0 0 0-.3 1.8V9a1.7 1.7 0 0 0 1.5 1H21a2 2 0 1 1 0 4h-.1a1.7 1.7 0 0 0-1.5 1Z',
    },
  ];

  private readonly menuButton = viewChild.required<ElementRef<HTMLButtonElement>>('menuButton');

  constructor() {
    const destroyRef = inject(DestroyRef);

    inject(Router)
      .events.pipe(
        filter((event) => event instanceof NavigationEnd),
        takeUntilDestroyed(),
      )
      .subscribe(() => this.drawerOpen.set(false));

    // Growing past the breakpoint shows the static sidebar, so a drawer left open would be a duplicate.
    const view = inject(DOCUMENT).defaultView;
    afterNextRender(() => {
      const query = view?.matchMedia?.(desktopQuery);
      if (!query) {
        return;
      }
      const onChange = (event: MediaQueryListEvent) => {
        if (event.matches) {
          this.drawerOpen.set(false);
        }
      };
      query.addEventListener('change', onChange);
      destroyRef.onDestroy(() => query.removeEventListener('change', onChange));
    });
  }

  protected openDrawer(): void {
    this.drawerOpen.set(true);
  }

  protected closeDrawer(): void {
    this.drawerOpen.set(false);
    this.menuButton().nativeElement.focus();
  }
}
