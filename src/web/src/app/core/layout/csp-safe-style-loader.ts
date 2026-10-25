import { _CdkPrivateStyleLoader } from '@angular/cdk/private';
import { Injectable, Provider } from '@angular/core';

/**
 * The CDK adds the structural styles of the overlay (menus, dialogs) and of `.cdk-visually-hidden` (live
 * announcer) at runtime, as a component's inline <style>. The strict CSP (`style-src 'self'`, no nonce) blocks
 * that element. The same rules are already loaded from `overlay-prebuilt.css` and `a11y-prebuilt.css`
 * (angular.json), so the runtime copy is skipped (D023).
 *
 * `_CdkPrivateStyleLoader` is a CDK-internal API: re-check this on every CDK upgrade. If another CDK feature
 * that loads styles this way is adopted, add its prebuilt CSS to angular.json first. The specs in
 * app-shell.spec.ts and confirm-dialog.spec.ts fail if opening a menu or a dialog inserts a <style> again.
 */
@Injectable()
export class CspSafeStyleLoader extends _CdkPrivateStyleLoader {
  override load(): void {
    // Styles come from the prebuilt CSS files instead.
  }
}

/**
 * App-wide: the style loader only. The dialog container (shared/ui/dialog-container.ts) is passed where dialogs
 * are opened (`Confirm`), so the CDK dialog code stays in the lazy chunks that use it, not the initial bundle.
 */
export function provideCspSafeCdkStyles(): Provider {
  return { provide: _CdkPrivateStyleLoader, useClass: CspSafeStyleLoader };
}
