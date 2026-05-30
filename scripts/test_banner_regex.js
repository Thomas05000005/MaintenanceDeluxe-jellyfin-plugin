#!/usr/bin/env node
// CI guard for the XSS URL allowlist in banner.js.
// Extracts the ACTUAL `SAFE_URL_RE` literal from the source (no duplicated logic,
// so the test can never drift from the shipped regex) and runs attack/benign vectors.
// Exits non-zero if any vector misbehaves — protocol: protocol-relative //host MUST be rejected.
const fs = require('fs');
const path = require('path');

const file = path.join(__dirname, '..', 'Jellyfin.Plugin.MaintenanceDeluxe', 'Resources', 'banner.js');
const src = fs.readFileSync(file, 'utf8');

const m = src.match(/var\s+SAFE_URL_RE\s*=\s*\/(.+?)\/([a-z]*)\s*;/);
if (!m) {
  console.error('FAIL: SAFE_URL_RE literal not found in banner.js (was it renamed?)');
  process.exit(2);
}
const re = new RegExp(m[1], m[2]);
console.log('Extracted SAFE_URL_RE = /' + m[1] + '/' + m[2]);

const cases = [
  ['https://example.com', true,  'absolute https'],
  ['http://example.com', true,  'absolute http'],
  ['HTTPS://EXAMPLE.com', true,  'uppercase scheme'],
  ['/foo/bar', true,  'relative path'],
  ['/', true,  'site root'],
  ['//evil.com', false, 'protocol-relative host (XSS vector)'],
  ['//evil.com/path', false, 'protocol-relative with path'],
  ['///triple', false, 'triple slash'],
  ['javascript:alert(1)', false, 'javascript scheme'],
  ['data:text/html,x', false, 'data scheme'],
  ['vbscript:msgbox', false, 'vbscript scheme'],
  ['ftp://example.com', false, 'ftp scheme'],
  ['<script>', false, 'literal angle bracket'],
  ['https:/single-slash.com', false, 'malformed single-slash https'],
];

let fail = 0;
for (const [url, expected, label] of cases) {
  const got = re.test(url);
  const ok = got === expected;
  if (!ok) fail++;
  console.log((ok ? 'OK  ' : 'FAIL') + ' [' + url + '] expected=' + expected + ' got=' + got + ' (' + label + ')');
}
if (fail) {
  console.error('\n' + fail + ' vector(s) failed — banner.js URL allowlist regression.');
  process.exit(1);
}
console.log('\nAll ' + cases.length + ' SAFE_URL_RE vectors OK.');
