// The npm audit gate (D020): like `npm audit --audit-level=moderate`, but an advisory listed in
// tools/audit-exceptions.json, with a reason and an expiry date, does not fail it until that date. An expired or
// unlisted moderate, high or critical advisory fails it. A listed advisory that no longer appears is reported, so its
// entry can be removed. Run from a folder with a package-lock.json: node <repo>/tools/npm-audit.mjs
// (exit 1 on a failure; --today YYYY-MM-DD checks the expiry against another day, to test the gate itself).
import { spawnSync } from 'node:child_process';
import fs from 'node:fs';

const failing = new Set(['moderate', 'high', 'critical']);
const todayArg = process.argv.indexOf('--today');
const today = todayArg > 0 ? process.argv[todayArg + 1] : new Date().toISOString().slice(0, 10);
const exceptions = JSON.parse(fs.readFileSync(new URL('./audit-exceptions.json', import.meta.url), 'utf8'));

// npm audit exits 1 whenever it finds something; the JSON says what.
const audit = spawnSync('npm', ['audit', '--json'], { encoding: 'utf8', shell: process.platform === 'win32' });
let report;
try {
  report = JSON.parse(audit.stdout);
} catch {
  console.error(`npm audit gave no report (exit ${audit.status}):\n${audit.stderr}`);
  process.exit(1);
}
if (report.error) {
  console.error(`npm audit failed: ${report.error.summary ?? JSON.stringify(report.error)}`);
  process.exit(1);
}

// The advisories themselves (the "via" entries that are objects); the other entries only point at other packages.
const advisories = new Map();
for (const [name, vulnerability] of Object.entries(report.vulnerabilities ?? {})) {
  for (const via of vulnerability.via.filter((v) => typeof v === 'object')) {
    const id = via.url.split('/').pop();
    const entry = advisories.get(id) ?? { id, package: via.name, severity: via.severity, title: via.title, affects: new Set() };
    entry.affects.add(name);
    advisories.set(id, entry);
  }
}

let failed = false;
for (const advisory of advisories.values()) {
  if (!failing.has(advisory.severity)) {
    continue;
  }
  const exception = exceptions.find((e) => e.advisory === advisory.id);
  const line = `${advisory.severity} ${advisory.id} in ${advisory.package}: ${advisory.title} (via ${[...advisory.affects].join(', ')})`;
  if (!exception) {
    console.error(`FAIL ${line}`);
    failed = true;
  } else if (exception.until < today) {
    console.error(`FAIL ${line}; its exception expired on ${exception.until}`);
    failed = true;
  } else {
    console.log(`allowed until ${exception.until}: ${line}`);
  }
}

for (const exception of exceptions.filter((e) => !advisories.has(e.advisory))) {
  console.log(`not found here (remove the entry if no audited folder reports it): ${exception.advisory} (${exception.package})`);
}

console.log(failed ? 'npm audit gate: failed' : 'npm audit gate: passed');
process.exit(failed ? 1 : 0);
