#!/usr/bin/env node
// Depo taraması: (1) secret sızıntısı, (2) sahiplik/yazar bilgisi.
// Yalnız git'in yok saymadığı dosyalara bakar (izlenen + izlenmeyen). Değerleri ASLA ekrana yazmaz.
//   node scripts/scan-repo.mjs
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const files = execFileSync('git', ['ls-files', '-co', '--exclude-standard', '-z'], { cwd: repoRoot, encoding: 'utf8', maxBuffer: 64 * 1024 * 1024 })
  .split('\0').filter(Boolean);

const findings = [];
const notes = [];
const report = (kind, file, detail) => findings.push({ kind, file, detail });

// --- 1) Yerelde üretilmiş gerçek secret değerleri depoda geçiyor mu? -----------------------------
const localSecrets = [];
const localDir = path.join(repoRoot, '.local');
if (existsSync(localDir)) {
  for (const project of readdirSync(localDir)) {
    for (const sub of ['secrets', 'users']) {
      const dir = path.join(localDir, project, sub);
      if (!existsSync(dir)) continue;
      for (const name of readdirSync(dir)) {
        const value = readFileSync(path.join(dir, name), 'utf8').trim();
        if (value.length >= 12) localSecrets.push({ label: `${project}/${sub}/${name}`, value });
      }
    }
  }
}

// --- 2) Genel secret desenleri ------------------------------------------------------------------
const secretPatterns = [
  ['private-key', /-----BEGIN (?:RSA |EC |OPENSSH |DSA )?PRIVATE KEY-----/],
  ['aws-access-key', /\bAKIA[0-9A-Z]{16}\b/],
  ['github-token', /\bgh[pousr]_[A-Za-z0-9]{36,}\b/],
  ['slack-token', /\bxox[baprs]-[A-Za-z0-9-]{10,}\b/],
  // Yalnız bağlantı dizesi biçimi: aynı satırda Host/Server ve değeri yazılı Password (kod atamaları değil).
  ['connection-string-password', /(?:Host|Server|Data Source)\s*=[^;\r\n]+;.*(?:Password|Pwd)\s*=\s*[^;\s"'{$<]{6,}/i],
  ['inline-secret-assignment', /(?:api[_-]?key|secret|passwd|password)["']?\s*[:=]\s*["'][A-Za-z0-9+/_\-]{24,}["']/i],
  ['jwt', /\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b/],
];
// Sentetik test sabitleri: yalnız tek kullanımlık test veritabanlarında/sunucusunda kullanılır.
const allowedSecretContexts = [/^tests\//, /^web\/e2e\//, /^web\/src\/.*\.test\.tsx?$/, /^scripts\/scan-repo\.mjs$/];

// --- 3) Sahiplik / imza -------------------------------------------------------------------------
const signaturePatterns = [
  ['co-author-trailer', /co-authored-by\s*:/i],
  ['generated-by-signature', /generated (?:with|by)\s+(?:\[?claude|anthropic|chatgpt|openai|codex|copilot|an? ai)/i],
  ['ai-author-metadata', /"?(?:author|authors|owner|maintainer)"?\s*[:=>]\s*[<"']?[^\n]{0,40}(?:claude|anthropic|openai|codex|chatgpt)/i],
  ['robot-emoji-signature', /🤖/u],
];
const vendorMention = /\b(?:claude|anthropic|openai|codex|chatgpt)\b/i;
const vendorMentionFiles = new Set();

const binaryExtensions = new Set(['.png', '.jpg', '.jpeg', '.gif', '.ico', '.woff', '.woff2', '.ttf', '.pfx', '.zip', '.pdf']);
let scanned = 0;
for (const file of files) {
  const absolute = path.join(repoRoot, file);
  if (!existsSync(absolute) || binaryExtensions.has(path.extname(file).toLowerCase())) continue;
  const size = statSync(absolute).size;
  if (size > 3 * 1024 * 1024) continue;
  const text = readFileSync(absolute, 'utf8');
  if (text.includes('\u0000')) continue;
  scanned++;

  for (const secret of localSecrets) {
    if (text.includes(secret.value)) report('local-secret-value', file, `yerel secret dosyasının değeri bulundu (${secret.label})`);
  }
  const isLockFile = /(?:package-lock\.json|packages\.lock\.json)$/.test(file);
  if (!isLockFile && !allowedSecretContexts.some((p) => p.test(file))) {
    for (const [name, pattern] of secretPatterns) if (pattern.test(text)) report('secret-pattern', file, name);
  }
  if (file !== 'scripts/scan-repo.mjs') {
    for (const [name, pattern] of signaturePatterns) if (pattern.test(text)) report('authorship', file, name);
    if (!isLockFile && vendorMention.test(text)) vendorMentionFiles.add(file);
  }
}

// --- 4) Yazar metadatası doğru mu? --------------------------------------------------------------
const expectations = [
  ['Directory.Build.props', /<Authors>TCoderr<\/Authors>/],
  ['Directory.Build.props', /<Company>TCoderr<\/Company>/],
  ['web/package.json', /"author":\s*"TCoderr"/],
  ['README.md', /\*\*Sahip \/ yazar:\*\* TCoderr/],
  ['Dockerfile', /org\.opencontainers\.image\.authors="TCoderr"/],
];
for (const [file, pattern] of expectations) {
  const absolute = path.join(repoRoot, file);
  if (!existsSync(absolute) || !pattern.test(readFileSync(absolute, 'utf8'))) report('authorship', file, `beklenen sahiplik bilgisi yok: ${pattern}`);
}

// --- 5) Secret klasörleri gerçekten yok sayılıyor mu? --------------------------------------------
for (const probe of ['.local/loca/secrets/db-app.password.txt', '.local/loca/users/customer-ayse@loca.demo.password.txt', '.env']) {
  try {
    execFileSync('git', ['check-ignore', '-q', probe], { cwd: repoRoot });
  } catch {
    report('gitignore', probe, 'git tarafından yok sayılmıyor');
  }
}
if (files.some((f) => f.startsWith('.local/') || /(^|\/)\.env$/.test(f) || /\.(pfx|key)$/.test(f))) {
  report('gitignore', '(depo)', 'secret/sertifika dosyası depoda listeleniyor');
}

if (vendorMentionFiles.size) {
  notes.push(`AI araç adı geçen dosyalar (imza değil; kullanılan skill/kaynak belgelemesi olmalı): ${[...vendorMentionFiles].sort().join(', ')}`);
}

console.log(`Taranan dosya: ${scanned} · karşılaştırılan yerel secret değeri: ${localSecrets.length}`);
for (const note of notes) console.log(`NOT   ${note}`);
for (const f of findings) console.log(`BULGU ${f.kind}  ${f.file}  — ${f.detail}`);
console.log(findings.length ? `\nTarama: ${findings.length} bulgu` : '\nTarama: bulgu yok');
process.exit(findings.length ? 1 : 0);
