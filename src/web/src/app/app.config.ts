import {
  provideHttpClient,
  withFetch,
  withInterceptors,
  withXsrfConfiguration,
} from '@angular/common/http';
import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
} from '@angular/core';
import {
  TitleStrategy,
  provideRouter,
  withComponentInputBinding,
  withInMemoryScrolling,
} from '@angular/router';
import { routes } from './app.routes';
import { AppTitleStrategy } from './core/app-title-strategy';
import { provideCspSafeCdkStyles } from './core/layout/csp-safe-style-loader';
import { authInterceptor } from './core/session/auth.interceptor';
import { SessionStore } from './core/session/session.store';
import { tenantAccessInterceptor } from './core/session/tenant-access.interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // D023: no runtime <style> from the CDK, which the strict CSP would block; the prebuilt CSS covers it.
    provideCspSafeCdkStyles(),
    provideHttpClient(
      withFetch(),
      // The API issues the XSRF-TOKEN cookie; Angular echoes it on unsafe same-origin requests (D009).
      withXsrfConfiguration({ cookieName: 'XSRF-TOKEN', headerName: 'X-XSRF-TOKEN' }),
      withInterceptors([authInterceptor, tenantAccessInterceptor]),
    ),
    // Know who is signed in before the first route (and its guards) runs.
    provideAppInitializer(() => inject(SessionStore).load()),
    provideRouter(
      routes,
      withComponentInputBinding(),
      withInMemoryScrolling({ scrollPositionRestoration: 'enabled' }),
    ),
    { provide: TitleStrategy, useClass: AppTitleStrategy },
  ],
};
