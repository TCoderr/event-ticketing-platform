#!/usr/bin/env node
// Uçtan uca testleri, bu koşuya özel BOŞ bir Compose stack'inde çalıştırır.
// Geliştirme stack'ine ("loca") veya başka projelere dokunmaz: kendi project adı, portu, volume'u ve durum
// dizini vardır; koşu bitince yalnız kendi kaynaklarını (sahipliği betik tarafından doğrulanarak) siler.
//
//   node e2e/run-e2e.mjs [--keep] [--build] [-- <playwright argümanları>]
//     --keep   Koşu sonunda stack'i silme (hata ayıklama için). Silmek için yazdırılan reset komutunu kullanın.
//     --build  İmajı yeniden derle (varsayılan: mevcut loca-api:local imajını kullan).
import { spawnSync } from 'node:child_process';
import { randomBytes } from 'node:crypto';
import { existsSync } from 'node:fs';
import net from 'node:net';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const webDir = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const repoRoot = path.resolve(webDir, '..');
const args = process.argv.slice(2);
const separator = args.indexOf('--');
const own = separator >= 0 ? args.slice(0, separator) : args;
const playwrightArgs = separator >= 0 ? args.slice(separator + 1) : [];
const keep = own.includes('--keep');
const build = own.includes('--build');

const project = `loca-e2e-${randomBytes(4).toString('hex')}`;
const stateDir = path.join(repoRoot, '.local', project);

function freePort() {
  return new Promise((resolve, reject) => {
    const server = net.createServer();
    server.unref();
    server.on('error', reject);
    server.listen(0, '127.0.0.1', () => {
      const { port } = server.address();
      server.close(() => resolve(port));
    });
  });
}

function stack(command, extra = []) {
  const isWindows = process.platform === 'win32';
  const [exe, exeArgs] = isWindows
    ? ['powershell.exe', ['-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', path.join(repoRoot, 'scripts', 'local-stack.ps1'), command, '-ProjectName', project, ...extra]]
    : ['bash', [path.join(repoRoot, 'scripts', 'local-stack.sh'), command, '--project', project,
        ...extra.map((a) => ({ '-HttpsPort': '--https-port', '-NoBuild': '--no-build', '-ConfirmProject': '--confirm-project' }[a] ?? a))]];
  return spawnSync(exe, exeArgs, { cwd: repoRoot, stdio: 'inherit', env: stackEnv });
}

const port = await freePort();
// Yalnız bu izole stack için: kısa hold süresine izin, hızlı "geç başarı", sentetik alım kapalı, gevşek hız sınırları.
const stackEnv = {
  ...process.env,
  LOCA_HOLDS_MIN_TTL_SECONDS: '5',
  LOCA_PSP_LATE_SUCCESS_DELAY_SECONDS: '25',
  LOCA_DEMO_CROWD_ENABLED: 'false',
  LOCA_RATE_LOGIN_PER_MINUTE: '100000',
  LOCA_RATE_REGISTER_PER_MINUTE: '100000',
  LOCA_RATE_CATALOG_PER_MINUTE: '100000',
  LOCA_RATE_HOLD_PER_MINUTE: '100000',
  LOCA_RATE_CHECKOUT_PER_MINUTE: '100000',
};

let exitCode = 1;
try {
  console.log(`[e2e] stack: ${project}  port: ${port}`);
  const init = stack('init', ['-HttpsPort', String(port), ...(build ? [] : ['-NoBuild'])]);
  if (init.status !== 0) throw new Error(`Stack başlatılamadı (çıkış kodu ${init.status}).`);

  const cli = path.join(webDir, 'node_modules', '@playwright', 'test', 'cli.js');
  const run = spawnSync(process.execPath, [cli, 'test', ...playwrightArgs], {
    cwd: webDir,
    stdio: 'inherit',
    env: { ...process.env, E2E_BASE_URL: `https://localhost:${port}`, E2E_STATE_DIR: stateDir, E2E_PROJECT: project },
  });
  exitCode = run.status ?? 1;
} catch (error) {
  console.error(`[e2e] ${error.message}`);
} finally {
  if (keep) {
    console.log(`[e2e] --keep: stack bırakıldı. Silmek için: scripts/local-stack reset (proje: ${project}, onay: ${project})`);
  } else if (existsSync(path.join(stateDir, 'stack.manifest.json'))) {
    const reset = stack('reset', ['-ConfirmProject', project]);
    if (reset.status !== 0) console.error(`[e2e] UYARI: stack temizlenemedi (${project}). Elle: reset -ConfirmProject ${project}`);
  }
}
process.exit(exitCode);
