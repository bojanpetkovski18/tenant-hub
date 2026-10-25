import { HttpErrorResponse } from '@angular/common/http';
import { toApiProblem } from './api-problem';

describe('toApiProblem', () => {
  it('reads title, code and field errors from a problem body', () => {
    const problem = toApiProblem(
      new HttpErrorResponse({
        status: 400,
        error: {
          title: 'Some fields need attention.',
          code: 'invalid-time-zone',
          errors: { email: ['Enter a valid email address.'], ignored: 'not an array' },
        },
      }),
    );

    expect(problem).toEqual({
      status: 400,
      title: 'Some fields need attention.',
      code: 'invalid-time-zone',
      fieldErrors: { email: ['Enter a valid email address.'] },
    });
  });

  it('explains network failures and bodies that are not problems', () => {
    expect(toApiProblem(new HttpErrorResponse({ status: 0 })).title).toContain('Cannot reach');
    expect(
      toApiProblem(new HttpErrorResponse({ status: 503, error: 'Bad gateway' })).title,
    ).toContain('our side');
    expect(toApiProblem(new Error('boom')).status).toBe(0);
  });
});
