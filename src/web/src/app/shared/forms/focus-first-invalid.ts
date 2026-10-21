import { ElementRef, Injector, afterNextRender } from '@angular/core';

/** After the next render, moves focus to the first invalid field of a form, so keyboard and screen-reader users land on it. */
export function focusFirstInvalid(form: ElementRef<HTMLElement>, injector: Injector): void {
  afterNextRender(
    () => form.nativeElement.querySelector<HTMLElement>('[aria-invalid="true"]')?.focus(),
    { injector },
  );
}
