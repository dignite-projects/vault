// Fails when a project references a module it may not, per the dependency rules in #716.
//
//   vault.parse    -> nothing in this repo outside vault.parse
//   vault.extract  -> vault.parse (all of it); never vault.index
//   vault.index    -> Dignite.Vault.Extract.Abstractions, .Application.Contracts, .HttpApi.Client only
//   src/ (host)    -> anything
//
// The modules share one repository, so a ProjectReference into another module's Domain is one line
// away and compiles fine - nothing else would notice that Index started reaching into Extract's
// internals, or that Parse grew a dependency on the product that consumes it. A PackageReference to
// a Dignite.Vault.* package is checked the same way, so the rule cannot be bypassed through NuGet.
//
// Test projects follow their module's rule. A new module under modules/ has no rule until one is
// added here, and fails until then.
//
// Usage: node etc/scripts/check-module-references.mjs   (from the repository root)

import { readFileSync, readdirSync, statSync } from 'node:fs';
import { basename, dirname, join, relative, resolve, sep } from 'node:path';

const root = process.cwd();

const RULES = {
  'vault.parse': { allows: target => target.module === 'vault.parse' },
  'vault.extract': { allows: target => target.module === 'vault.extract' || target.module === 'vault.parse' },
  'vault.index': {
    allows: target =>
      target.module === 'vault.index' ||
      [
        'Dignite.Vault.Extract.Abstractions',
        'Dignite.Vault.Extract.Application.Contracts',
        'Dignite.Vault.Extract.HttpApi.Client',
      ].includes(target.name),
  },
  host: { allows: () => true },
};

// Module of a project, from where its .csproj sits.
function moduleOfPath(csprojPath) {
  const parts = relative(root, csprojPath).split(sep);
  if (parts[0] === 'modules') return parts[1];
  if (parts[0] === 'src') return 'host';
  return null;
}

// Module of a Dignite.Vault.* package name (PackageReference).
function moduleOfPackage(name) {
  const m = /^Dignite\.Vault\.(Parse|Extract|Index)(\.|$)/.exec(name);
  return m ? `vault.${m[1].toLowerCase()}` : null;
}

function findProjects(dir, out = []) {
  for (const entry of readdirSync(dir)) {
    if (entry === 'node_modules' || entry === 'bin' || entry === 'obj' || entry.startsWith('.')) continue;
    const full = join(dir, entry);
    if (statSync(full).isDirectory()) findProjects(full, out);
    else if (entry.endsWith('.csproj')) out.push(full);
  }
  return out;
}

const projects = [...findProjects(join(root, 'modules')), ...findProjects(join(root, 'src'))];
const problems = [];

for (const project of projects) {
  const from = moduleOfPath(project);
  const rule = RULES[from];
  const name = basename(project, '.csproj');
  if (!rule) {
    problems.push(`${relative(root, project)}: module '${from}' has no rule in check-module-references.mjs`);
    continue;
  }
  const xml = readFileSync(project, 'utf8').replace(/<!--[\s\S]*?-->/g, '');
  const targets = [];
  for (const [, include] of xml.matchAll(/<ProjectReference\s+Include="([^"]+)"/g)) {
    const path = resolve(dirname(project), include.replace(/\\/g, sep));
    targets.push({ name: basename(path, '.csproj'), module: moduleOfPath(path), via: 'ProjectReference' });
  }
  for (const [, include] of xml.matchAll(/<PackageReference\s+Include="(Dignite\.Vault\.[^"]+)"/g)) {
    targets.push({ name: include, module: moduleOfPackage(include), via: 'PackageReference' });
  }
  for (const target of targets) {
    if (!rule.allows(target)) {
      problems.push(`${name} (${from}) -> ${target.name} (${target.module ?? 'outside modules/'}) via ${target.via}`);
    }
  }
}

if (problems.length > 0) {
  console.error('Module dependency rule violated (#716):');
  for (const p of problems) console.error(`  ${p}`);
  process.exit(1);
}
console.log(`OK: ${projects.length} projects follow the module dependency rules.`);
