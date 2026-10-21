import { Injectable, inject } from '@angular/core';
import { Title } from '@angular/platform-browser';
import { RouterStateSnapshot, TitleStrategy } from '@angular/router';

/** "Jobs · TenantHub" for routes with a title, "TenantHub" otherwise. */
@Injectable()
export class AppTitleStrategy extends TitleStrategy {
  private readonly title = inject(Title);

  override updateTitle(snapshot: RouterStateSnapshot): void {
    const pageTitle = this.buildTitle(snapshot);
    this.title.setTitle(pageTitle ? `${pageTitle} · TenantHub` : 'TenantHub');
  }
}
