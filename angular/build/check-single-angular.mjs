// Fails when the host bundle holds two copies of a framework package.
//
// The host SPA compiles the module libraries from source (tsconfig.base.json `paths` point into
// ../modules/*/angular/projects). esbuild resolves a bare import from the importing file's own
// directory upward, so a module file that imports @angular/core takes the first node_modules above
// modules/<module>/angular, not the host's. With one npm install at the repo root (the root
// package.json `workspaces`), both walks end at the same copy. If a module workspace ever gets its
// own node_modules - a separate `npm install` inside it, or a version range that npm cannot hoist -
// the bundle carries a second copy of that package. A second @angular/core happens to fail this
// repo's build already (strictTemplates sees two InputSignal brands), but a second @abp/ng.core,
// @dignite/ng.flex-fields or rxjs builds cleanly and splits DI tokens / singletons only at runtime -
// verified by planting a nested @dignite/ng.flex-fields: `ng build host` passed, this check failed.
// The sibling campus repo ships such a bundle today (two @angular/core, two @abp/ng.core, three
// rxjs; it builds without strictTemplates).
//
// Usage: node build/check-single-angular.mjs <esbuild-metafile.json>
//        (`ng build host --stats-json` writes dist/host/browser-stats.json)

import { readFileSync } from 'node:fs';

const statsPath = process.argv[2];
if (!statsPath) {
  throw new Error('Usage: node build/check-single-angular.mjs <esbuild-metafile.json>');
}

// Packages that hold runtime singletons (DI tokens, zone, observables shared across the app).
// A second copy of any of them breaks at runtime, not at build time.
const SINGLETON = /^(@angular|@abp|@volo|@dignite|@ng-bootstrap|@ngx-validate)\/|^(rxjs|zone\.js|ng-zorro-antd)$/;

const { inputs } = JSON.parse(readFileSync(statsPath, 'utf8'));
const roots = new Map(); // package name -> Set of node_modules directories it was bundled from

for (const input of Object.keys(inputs)) {
  const path = input.replace(/\\/g, '/');
  const match = /^(.*?node_modules)\/((?:@[^/]+\/)?[^/]+)\//.exec(path);
  if (!match) continue;
  const [, nodeModules, name] = match;
  if (!SINGLETON.test(name)) continue;
  if (!roots.has(name)) roots.set(name, new Set());
  roots.get(name).add(nodeModules);
}

if (roots.size === 0) {
  // An empty result means the metafile shape changed, not that the bundle is clean.
  console.error(`No framework packages found in ${statsPath}; is it an esbuild metafile?`);
  process.exit(1);
}

const duplicated = [...roots].filter(([, dirs]) => dirs.size > 1);
if (duplicated.length > 0) {
  console.error('The host bundle contains more than one copy of:');
  for (const [name, dirs] of duplicated) {
    console.error(`  ${name}: ${[...dirs].join(', ')}`);
  }
  console.error('Install once from the repo root (npm ci) and keep version ranges hoistable.');
  process.exit(1);
}

console.log(`OK: ${roots.size} framework packages, one copy each.`);
