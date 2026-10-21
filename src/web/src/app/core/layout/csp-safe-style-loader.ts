import { Injectable, Provider } from '@angular/core';
import { _CdkPrivateStyleLoader } from '@angular/cdk/private';

/**
 * The CDK adds the structural styles of the overlay (menus, dialogs) and of `.cdk-visually-hidden` (live
 * announcer) at runtime, as a component's inline <style>. The strict CSP (`style-src 'self'`, no nonce) blocks
 * that element. The same rules are already loaded from `overlay-prebuilt.css` and `a11y-prebuilt.css`
 * (angular.json), so the runtime copy is skipped (D023).
 *
 * `_CdkPrivateStyleLoader` is a CDK-internal API: re-check this on every CDK upgrade. If another CDK feature
 * that loads styles this way is adopted, add its prebuilt CSS to angular.json first. The spec in
 * app-shell.spec.ts fails if opening a menu inserts a <style> again.
 */
@Injectable()
export class CspSafeStyleLoader extends _CdkPrivateStyleLoader {
  override load(): void {
    // Styles come from the prebuilt CSS files instead.
  }
}

export function provideCspSafeCdkStyles(): Provider {
  return { provide: _CdkPrivateStyleLoader, useClass: CspSafeStyleLoader };
}
