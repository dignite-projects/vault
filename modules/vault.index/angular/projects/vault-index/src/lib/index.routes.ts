import { Routes } from '@angular/router';
import { authGuard, permissionGuard } from '@abp/ng.core';
import { INDEX_PERMISSIONS } from './tokens';

/** Vault Index's pages. The host mounts them with `loadChildren` under `vault-index`. */
export const INDEX_ROUTES: Routes = [
  {
    path: '',
    pathMatch: 'full',
    redirectTo: 'status',
  },
  {
    path: 'status',
    loadComponent: () => import('./status/index-status.component').then(c => c.IndexStatusComponent),
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: INDEX_PERMISSIONS.Status.Default },
  },
];
