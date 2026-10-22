import { CdkDialogContainer } from '@angular/cdk/dialog';
import { CdkPortalOutlet } from '@angular/cdk/portal';
import { ChangeDetectionStrategy, Component, ViewEncapsulation } from '@angular/core';

/**
 * The CDK dialog container, minus its component styles (which Angular would also inject as an inline <style>;
 * D027). Same template and host bindings as `CdkDialogContainer`; its one CSS rule lives in styles.css.
 */
@Component({
  selector: 'app-dialog-container',
  imports: [CdkPortalOutlet],
  template: '<ng-template cdkPortalOutlet />',
  host: {
    class: 'cdk-dialog-container',
    tabindex: '-1',
    '[attr.id]': '_config.id || null',
    '[attr.role]': '_config.role',
    '[attr.aria-modal]': '_config.ariaModal',
    '[attr.aria-labelledby]': '_config.ariaLabel ? null : _ariaLabelledByQueue[0]',
    '[attr.aria-label]': '_config.ariaLabel',
    '[attr.aria-describedby]': '_config.ariaDescribedBy || null',
  },
  encapsulation: ViewEncapsulation.None,
  // Matches CdkDialogContainer, which is eager because portal content is attached imperatively.
  // eslint-disable-next-line @angular-eslint/prefer-on-push-component-change-detection
  changeDetection: ChangeDetectionStrategy.Eager,
})
export class CspSafeDialogContainer extends CdkDialogContainer {}
