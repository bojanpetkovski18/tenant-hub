import { DOCUMENT, effect, inject } from '@angular/core';
import { patchState, signalStore, withHooks, withMethods, withState } from '@ngrx/signals';

export type Theme = 'light' | 'dark';

const storageKey = 'th-theme';

/**
 * Light/dark theme (D014). Starts from the saved choice, else the system preference. Storage access is
 * wrapped because it throws in some privacy modes; the theme still works there, it is just not remembered.
 */
export const ThemeStore = signalStore(
  { providedIn: 'root' },
  withState(() => ({ theme: initialTheme(inject(DOCUMENT)) })),
  withMethods((store, document = inject(DOCUMENT)) => ({
    toggle(): void {
      const next: Theme = store.theme() === 'dark' ? 'light' : 'dark';
      patchState(store, { theme: next });
      try {
        document.defaultView?.localStorage.setItem(storageKey, next);
      } catch {
        // Not persisted; acceptable.
      }
    },
  })),
  withHooks((store, document = inject(DOCUMENT)) => ({
    onInit(): void {
      effect(() => {
        document.documentElement.dataset['theme'] = store.theme();
      });
    },
  })),
);

function initialTheme(document: Document): Theme {
  const view = document.defaultView;
  try {
    const saved = view?.localStorage.getItem(storageKey);
    if (saved === 'light' || saved === 'dark') {
      return saved;
    }
  } catch {
    // Fall through to the system preference.
  }
  return view?.matchMedia?.('(prefers-color-scheme: dark)').matches ? 'dark' : 'light';
}
