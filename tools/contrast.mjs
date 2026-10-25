// WCAG 2.2 contrast of the design tokens (SPEC 9, M10): every foreground/background pair the UI uses, in both
// themes, read from src/web/src/styles.css. Text needs 4.5:1; large text and graphical objects (focus ring, meter and
// chart fills) need 3:1. Run: node tools/contrast.mjs (exit 1 when a pair fails).
import fs from 'node:fs';

const css = fs.readFileSync(new URL('../src/web/src/styles.css', import.meta.url), 'utf8');

function tokens(selector) {
  const start = css.indexOf(selector);
  const block = css.slice(start, css.indexOf('}', start));
  return Object.fromEntries([...block.matchAll(/--th-([a-z0-9-]+):\s*(#[0-9a-f]{6})/gi)].map((m) => [m[1], m[2]]));
}

const luminance = (hex) => {
  const [r, g, b] = [1, 3, 5].map((i) => parseInt(hex.slice(i, i + 2), 16) / 255).map((c) => (c <= 0.04045 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4));
  return 0.2126 * r + 0.7152 * g + 0.0722 * b;
};
const ratio = (a, b) => {
  const [hi, lo] = [luminance(a), luminance(b)].sort((x, y) => y - x);
  return (hi + 0.05) / (lo + 0.05);
};

// [foreground, background, minimum, where it is used]
const pairs = [
  ['text', 'bg', 4.5, 'body text'],
  ['text', 'surface', 4.5, 'card text'],
  ['text', 'surface-2', 4.5, 'text on hovered rows, skeleton areas'],
  ['text', 'sidebar', 4.5, 'navigation'],
  ['text', 'accent-soft', 4.5, 'demo banner, active navigation, badges'],
  ['text-muted', 'bg', 4.5, 'hints, secondary text'],
  ['text-muted', 'surface', 4.5, 'hints in cards'],
  ['text-muted', 'surface-2', 4.5, 'muted text on hovered rows'],
  ['text-muted', 'sidebar', 4.5, 'membership role in the switcher'],
  ['accent', 'bg', 4.5, 'links'],
  ['accent', 'surface', 4.5, 'links in cards'],
  ['accent', 'surface-2', 4.5, 'links on hovered rows'],
  ['on-accent', 'accent', 4.5, 'primary buttons'],
  ['on-accent', 'accent-strong', 4.5, 'primary buttons, hover'],
  ['danger', 'surface', 4.5, 'field errors, overdue numbers'],
  ['danger', 'bg', 4.5, 'errors on the page background'],
  ['on-danger', 'danger', 4.5, 'danger buttons'],
  ['success', 'surface', 4.5, 'success text'],
  ['warning', 'surface', 4.5, 'warning text'],
  ['focus', 'bg', 3, 'focus ring (non-text)'],
  ['focus', 'surface', 3, 'focus ring on cards (non-text)'],
  ['focus', 'sidebar', 3, 'focus ring in navigation (non-text)'],
  ['accent', 'surface-2', 3, 'meter and chart fill (non-text)'],
  ['warning', 'surface-2', 3, 'meter near the limit (non-text)'],
  ['danger', 'surface-2', 3, 'meter at the limit (non-text)'],
  ['success', 'surface-2', 3, 'chart fill (non-text)'],
];

let failures = 0;
for (const [theme, selector] of [['light', ":root,\n[data-theme='light']"], ['dark', "[data-theme='dark']"]]) {
  const t = tokens(selector);
  for (const [fg, bg, min, use] of pairs) {
    const value = ratio(t[fg], t[bg]);
    const ok = value >= min;
    failures += ok ? 0 : 1;
    console.log(`${ok ? 'PASS' : 'FAIL'} ${theme.padEnd(5)} ${`${fg} on ${bg}`.padEnd(28)} ${value.toFixed(2).padStart(5)}:1 (needs ${min}) ${use}`);
  }
}
console.log(failures ? `\n${failures} pairs below the minimum` : '\nall pairs meet the minimum');
process.exit(failures ? 1 : 0);
