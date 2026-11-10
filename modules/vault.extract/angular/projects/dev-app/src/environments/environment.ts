import { Environment } from '@abp/ng.core';

/**
 * Not a runnable app. `abp generate-proxy --source dev-app` reads the API root namespace from this
 * file; the module workspace has no application of its own to read it from. The real SPA is the
 * host under the repo-root angular/.
 */
export const environment = {
  production: false,
  application: {
    name: '',
  },
  apis: {
    default: {
      url: 'https://localhost:44348',
      rootNamespace: 'Dignite.Vault.Extract',
    },
  },
} as Environment;
