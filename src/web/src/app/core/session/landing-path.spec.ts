import { olivia } from '../../testing/session';
import { isSafeReturnUrl, landingPath } from './landing-path';

describe('landingPath', () => {
  const single = { ...olivia, memberships: [olivia.memberships[0]!] };
  const none = { ...olivia, memberships: [] };

  it('prefers a safe return URL', () => {
    expect(landingPath(olivia, '/o/harbor-pine/jobs', 'brightline')).toBe('/o/harbor-pine/jobs');
  });

  it('falls back to the last organization when the user still belongs to it', () => {
    expect(landingPath(olivia, null, 'harbor-pine')).toBe('/o/harbor-pine/dashboard');
  });

  it('ignores a last organization the user no longer belongs to', () => {
    expect(landingPath(olivia, null, 'someone-else')).toBe('/orgs');
  });

  it('opens the only organization, or the picker for none or several', () => {
    expect(landingPath(single, null, null)).toBe('/o/brightline/dashboard');
    expect(landingPath(none, null, null)).toBe('/orgs');
    expect(landingPath(olivia, null, null)).toBe('/orgs');
  });

  it.each([
    ['//evil.example/phish', false],
    ['/\\evil.example', false],
    ['https://evil.example', false],
    ['javascript:alert(1)', false],
    ['/sign-in', false],
    ['/sign-up?x=1', false],
    ['/o/brightline/jobs?status=open', true],
    ['/account', true],
  ])('treats %s as safe: %s', (url, safe) => {
    expect(isSafeReturnUrl(url)).toBe(safe);
  });

  it('never redirects to an unsafe return URL', () => {
    expect(landingPath(single, '//evil.example', null)).toBe('/o/brightline/dashboard');
  });
});
