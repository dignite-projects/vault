/** Vault Index's permission names, as the server defines them (VaultIndexPermissions). */
export const INDEX_PERMISSIONS = {
  Search: 'VaultIndex.Search',
  Profiles: 'VaultIndex.Profiles',
  Status: {
    Default: 'VaultIndex.Status',
    Rebuild: 'VaultIndex.Status.Rebuild',
  },
} as const;

export const INDEX_FEATURES = {
  // Whether the tenant may use Vault Index's application services (VaultIndexFeatures.Enable). The server is the
  // authority; this value only takes the menu out of sight.
  Enable: 'VaultIndex.Enable',
} as const;

/** Only an explicit `false` hides the menu: a missing value reads as enabled, the server's default. */
export function isIndexFeatureDisabled(value: string | undefined): boolean {
  return value?.trim().toLowerCase() === 'false';
}
