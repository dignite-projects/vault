// Public API surface of @dignite/ng.vault-index.
//
// The proxy/ folder is owned by the ABP proxy generator (`npm run generate-proxy`) and overwritten on every run.
// As in @dignite/ng.vault-extract, proxy files are re-exported here directly, not through their index.ts barrels.
export * from './lib/tokens';
export * from './lib/index.routes';

// --- generated proxy: services ---
export * from './lib/proxy/http-api/search/index-search.service';
export * from './lib/proxy/http-api/search/index-search-test.service';
export * from './lib/proxy/http-api/status/index-status.service';
export * from './lib/proxy/http-api/chunks/index-chunk.service';
export * from './lib/proxy/http-api/profiles/index-profile.service';

// --- generated proxy: models and enums ---
export * from './lib/proxy/search/models';
export * from './lib/proxy/chunks/models';
export * from './lib/proxy/profiles/models';
// Extract's field condition, which IndexSearchInput.fieldFilters carries.
export * from './lib/proxy/dignite/vault/extract/documents/models';
export * from './lib/proxy/status/models';
export * from './lib/proxy/indexing/indexed-document-status.enum';
export * from './lib/proxy/indexing/index-run-status.enum';
export * from './lib/proxy/indexing/index-run-trigger.enum';
export * from './lib/proxy/indexing/chunking-strategy.enum';
export * from './lib/proxy/indexing/index-enrichment.enum';
