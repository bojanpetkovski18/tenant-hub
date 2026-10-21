import { TestBed } from '@angular/core/testing';
import { ThemeStore } from './theme.store';

describe('ThemeStore', () => {
  beforeEach(() => {
    localStorage.clear();
    delete document.documentElement.dataset['theme'];
    TestBed.resetTestingModule();
  });

  it('uses a saved theme over the system preference', () => {
    localStorage.setItem('th-theme', 'dark');

    const store = TestBed.inject(ThemeStore);
    TestBed.tick();

    expect(store.theme()).toBe('dark');
    expect(document.documentElement.dataset['theme']).toBe('dark');
  });

  it('toggles, applies the theme to <html> and remembers the choice', () => {
    localStorage.setItem('th-theme', 'light');
    const store = TestBed.inject(ThemeStore);

    store.toggle();
    TestBed.tick();

    expect(store.theme()).toBe('dark');
    expect(document.documentElement.dataset['theme']).toBe('dark');
    expect(localStorage.getItem('th-theme')).toBe('dark');
  });

  it('ignores an invalid saved value', () => {
    localStorage.setItem('th-theme', 'purple');

    const store = TestBed.inject(ThemeStore);

    expect(['light', 'dark']).toContain(store.theme());
  });
});
