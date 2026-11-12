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
  {
    path: 'documents/:documentId/chunks',
    loadComponent: () => import('./chunks/index-chunks.component').then(c => c.IndexChunksComponent),
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: INDEX_PERMISSIONS.Status.Default },
  },
  {
    path: 'profiles',
    loadComponent: () => import('./profiles/index-profiles.component').then(c => c.IndexProfilesComponent),
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: INDEX_PERMISSIONS.Profiles },
  },
  {
    path: 'search-test',
    loadComponent: () => import('./search-test/index-search-test.component').then(c => c.IndexSearchTestComponent),
    canActivate: [authGuard, permissionGuard],
    data: { requiredPolicy: INDEX_PERMISSIONS.Status.Default },
  },
];
