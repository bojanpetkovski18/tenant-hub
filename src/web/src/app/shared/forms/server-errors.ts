import { AbstractControl, FormGroup } from '@angular/forms';
import { ApiProblem } from '../../core/http/api-problem';

/**
 * Puts the server's field errors on the matching controls (as a `server` error) and returns the messages that
 * belong to no control, for a form-level alert. The server is authoritative (CLAUDE.md); client checks only
 * save a round trip.
 */
export function applyServerErrors(form: FormGroup, problem: ApiProblem): string[] {
  const unmatched: string[] = [];
  for (const [field, messages] of Object.entries(problem.fieldErrors)) {
    const control = field ? form.get(field) : null;
    if (control && messages.length > 0) {
      control.setErrors({ ...control.errors, server: messages.join(' ') });
      control.markAsTouched();
    } else {
      unmatched.push(...messages);
    }
  }
  return unmatched;
}

/**
 * Shows a failed request on a form: field errors on their controls, a business-rule error (by `code`) on the
 * field it is about when `codeFields` names one, and anything else as the returned form-level message.
 */
export function applyProblem(
  form: FormGroup,
  problem: ApiProblem,
  codeFields: Readonly<Record<string, string>> = {},
): string | null {
  const field = problem.code ? codeFields[problem.code] : undefined;
  const control = field ? form.get(field) : null;
  if (control) {
    control.setErrors({ ...control.errors, server: problem.title });
    control.markAsTouched();
    return null;
  }

  const unmatched = applyServerErrors(form, problem);
  if (unmatched.length > 0) {
    return unmatched.join(' ');
  }
  return Object.keys(problem.fieldErrors).length > 0 ? null : problem.title;
}

/** The message to show for a control, or null; `messages` maps validator keys to text. */
export function controlError(
  control: AbstractControl,
  messages: Readonly<Record<string, string>>,
): string | null {
  if (!control.touched || !control.errors) {
    return null;
  }
  const errors = control.errors;
  if (typeof errors['server'] === 'string') {
    return errors['server'];
  }
  const key = Object.keys(errors).find((k) => k in messages);
  return key ? (messages[key] ?? null) : 'Check this field.';
}
