import { HttpTestingController } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Router, provideRouter, withComponentInputBinding } from '@angular/router';
import { RouterTestingHarness } from '@angular/router/testing';
import { routes } from '../../app.routes';
import { loadSession, olivia, provideTestHttp, settle } from '../../testing/session';

describe('SignUpPage', () => {
  let harness: RouterTestingHarness;
  let http: HttpTestingController;
  let page: HTMLElement;

  beforeEach(async () => {
    TestBed.configureTestingModule({
      providers: [provideTestHttp(), provideRouter(routes, withComponentInputBinding())],
    });
    http = TestBed.inject(HttpTestingController);
    await loadSession(null);
    harness = await RouterTestingHarness.create();
    await harness.navigateByUrl('/sign-up');
    page = harness.routeNativeElement as HTMLElement;
  });

  afterEach(() => http.verify());

  const fill = (values: Record<string, string>) => {
    for (const [id, value] of Object.entries(values)) {
      const element = page.querySelector<HTMLInputElement | HTMLSelectElement>(`#${id}`)!;
      element.value = value;
      element.dispatchEvent(new Event(element instanceof HTMLSelectElement ? 'change' : 'input'));
    }
  };
  const validForm = {
    displayName: 'Olivia Bennett',
    email: 'olivia@brightline.example',
    password: 'Correct-Horse-9',
    organizationName: 'Brightline Cleaning Co.',
    timeZone: 'Europe/Amsterdam',
  };
  const submit = async () => {
    page.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'));
    await harness.fixture.whenStable();
  };

  it('defaults the time zone to the browser’s and offers IANA zones', () => {
    const select = page.querySelector<HTMLSelectElement>('#timeZone')!;

    expect(select.value).toBe(Intl.DateTimeFormat().resolvedOptions().timeZone);
    expect([...select.options].map((o) => o.value)).toContain('Europe/Amsterdam');
  });

  it('puts the server’s field errors on the matching fields', async () => {
    fill(validForm);
    await submit();

    http.expectOne('/api/auth/sign-up').flush(
      {
        title: 'Some fields need attention.',
        errors: {
          email: ['An account with this email already exists. Sign in instead.'],
          password: ['Passwords must have at least one non alphanumeric character.'],
        },
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle(harness.fixture);

    expect(page.querySelector('#email-error')?.textContent).toContain('Sign in instead');
    expect(page.querySelector('#password-error')?.textContent).toContain('non alphanumeric');
    expect(page.querySelector('[role="alert"]')).toBeNull();
    expect(document.activeElement?.id).toBe('email');
  });

  it('shows a business-rule error on the field it is about', async () => {
    fill(validForm);
    await submit();

    http.expectOne('/api/auth/sign-up').flush(
      {
        title: 'Too many organizations already use a similar name.',
        code: 'organization-name-taken',
      },
      { status: 400, statusText: 'Bad Request' },
    );
    await settle(harness.fixture);

    expect(page.querySelector('#organizationName-error')?.textContent).toContain('similar name');
  });

  it('opens the new organization after signing up', async () => {
    fill(validForm);
    await submit();

    const request = http.expectOne('/api/auth/sign-up');
    expect(request.request.body).toEqual(validForm);
    request.flush({ user: { ...olivia, memberships: [olivia.memberships[0]] } });
    await settle(harness.fixture);

    expect(TestBed.inject(Router).url).toBe('/o/brightline/dashboard');
  });
});
