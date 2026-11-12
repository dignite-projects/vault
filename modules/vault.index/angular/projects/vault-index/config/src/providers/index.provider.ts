import { ConfigStateService, eLayoutType, RoutesService } from '@abp/ng.core';
import { DestroyRef, EnvironmentProviders, inject, makeEnvironmentProviders, provideAppInitializer } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { INDEX_FEATURES, INDEX_PERMISSIONS, isIndexFeatureDisabled } from '@dignite/ng.vault-index';
import { distinctUntilChanged, map } from 'rxjs';

const INDEX_MENU = 'VaultIndex::Menu:VaultIndex';

/**
 * Adds the Vault Index menu, and takes it out of sight while the tenant does not have the `VaultIndex.Enable`
 * feature, the way `provideExtract()` does for Extract's menu.
 */
export function provideIndex(): EnvironmentProviders {
  return makeEnvironmentProviders([
    provideAppInitializer(() => {
      const routes = inject(RoutesService);
      const configState = inject(ConfigStateService);
      const destroyRef = inject(DestroyRef);
      routes.add([
        {
          path: '/vault-index',
          name: INDEX_MENU,
          iconClass: 'fas fa-magnifying-glass',
          requiredPolicy: INDEX_PERMISSIONS.Status.Default,
          order: 3,
          layout: eLayoutType.application,
        },
        {
          path: '/vault-index/status',
          name: 'VaultIndex::Menu:VaultIndex.Status',
          iconClass: 'fas fa-heart-pulse',
          parentName: INDEX_MENU,
          requiredPolicy: INDEX_PERMISSIONS.Status.Default,
          order: 0,
          layout: eLayoutType.application,
        },
      ]);

      configState
        .getFeature$(INDEX_FEATURES.Enable)
        .pipe(map(isIndexFeatureDisabled), distinctUntilChanged(), takeUntilDestroyed(destroyRef))
        .subscribe(invisible => routes.patch(INDEX_MENU, { invisible }));
    }),
  ]);
}
