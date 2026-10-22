import { HttpClient, HttpParams } from '@angular/common/http';
import { inject } from '@angular/core';
import { patchState, signalStore, withComputed, withMethods, withState } from '@ngrx/signals';
import { computed } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { ApiProblem, toApiProblem } from '../../core/http/api-problem';

/** One page of a server-side list (SPEC 7). */
export interface PagedList<T> {
  readonly items: readonly T[];
  readonly page: number;
  readonly pageSize: number;
  readonly total: number;
}

interface PagedListState<T> {
  readonly result: PagedList<T> | null;
  readonly loading: boolean;
  readonly error: ApiProblem | null;
  /** The last request, so "Try again" repeats it exactly. */
  readonly lastRequest: { readonly url: string; readonly params: Record<string, string> } | null;
}

/**
 * The reusable list pattern (M04): the query lives in the URL (the page passes it in), this store only fetches
 * and holds one page plus its loading and error state. A newer request wins; an older answer arriving late is
 * ignored, so fast typing in a search box cannot show stale results.
 */
export function createPagedListStore<T>() {
  return signalStore(
    withState<PagedListState<T>>({ result: null, loading: false, error: null, lastRequest: null }),
    withComputed(({ result, loading }) => ({
      items: computed(() => result()?.items ?? []),
      total: computed(() => result()?.total ?? 0),
      isEmpty: computed(() => !loading() && result() !== null && result()!.total === 0),
    })),
    withMethods((store, http = inject(HttpClient)) => {
      let latest = 0;
      return {
        async load(url: string, params: Record<string, string>): Promise<void> {
          const request = ++latest;
          patchState(store, { loading: true, error: null, lastRequest: { url, params } });
          try {
            const result = await firstValueFrom(
              http.get<PagedList<T>>(url, { params: new HttpParams({ fromObject: params }) }),
            );
            if (request === latest) {
              patchState(store, { result, loading: false });
            }
          } catch (error) {
            if (request === latest) {
              patchState(store, { error: toApiProblem(error), loading: false });
            }
          }
        },
        async retry(): Promise<void> {
          const last = store.lastRequest();
          if (last) {
            await this.load(last.url, last.params);
          }
        },
      };
    }),
  );
}
