#!/usr/bin/env node
// Loca Compose HTTPS smoke testi. Çalışan yerel stack'e gerçek HTTPS üzerinden, sunucunun kendi
// sertifikasını sabitleyerek bağlanır. Kullanım:
//   node scripts/smoke.mjs [--project loca] [--skip-disruptive]
// --skip-disruptive: yeniden başlatma ve veritabanı kesintisi adımlarını atlar.
import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import https from 'node:https';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const args = process.argv.slice(2);
const option = (name, fallback) => {
  const index = args.indexOf(name);
  return index >= 0 ? args[index + 1] : fallback;
};
const project = option('--project', 'loca');
const skipDisruptive = args.includes('--skip-disruptive');

const repoRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const stateDir = path.join(repoRoot, '.local', project);
const manifestPath = path.join(stateDir, 'stack.manifest.json');
if (!existsSync(manifestPath)) {
  console.error(`Stack durumu bulunamadı: ${manifestPath}. Önce local-stack init çalıştırın.`);
  process.exit(2);
}
const manifest = JSON.parse(readFileSync(manifestPath, 'utf8'));
const ca = readFileSync(path.join(stateDir, 'certs', 'loca-local.crt'));
const origin = `https://localhost:${manifest.httpsPort}`;

const composeEnv = {
  ...process.env,
  LOCA_STATE_DIR: stateDir.replaceAll('\\', '/'),
  LOCA_HTTPS_PORT: String(manifest.httpsPort),
  LOCA_STACK_ID: manifest.stackId,
};
function compose(...composeArgs) {
  return execFileSync('docker',
    ['compose', '--project-name', project, '--project-directory', repoRoot, '--file', path.join(repoRoot, 'compose.yaml'), ...composeArgs],
    { env: composeEnv, encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'], maxBuffer: 64 * 1024 * 1024 });
}

const password = (email) => readFileSync(path.join(stateDir, 'users', `${email}.password.txt`), 'utf8').trim();

/** Çerez kavanozu ve CSRF token'ı taşıyan küçük HTTPS istemcisi. */
class Client {
  cookies = new Map();
  csrf = null;

  request(method, url, { body, headers = {}, csrf = true } = {}) {
    return new Promise((resolve, reject) => {
      const payload = body === undefined ? null : Buffer.from(typeof body === 'string' ? body : JSON.stringify(body));
      const allHeaders = { accept: 'application/json', ...headers };
      if (this.cookies.size) allHeaders.cookie = [...this.cookies].map(([k, v]) => `${k}=${v}`).join('; ');
      if (payload) { allHeaders['content-type'] = 'application/json'; allHeaders['content-length'] = payload.length; }
      if (csrf && this.csrf && method !== 'GET') allHeaders['x-csrf-token'] = this.csrf;

      const req = https.request(origin + url, { method, headers: allHeaders, ca, timeout: 15000 }, (res) => {
        const chunks = [];
        res.on('data', (c) => chunks.push(c));
        res.on('end', () => {
          for (const line of res.headers['set-cookie'] ?? []) {
            const [pair, ...attrs] = line.split(';').map((s) => s.trim());
            const eq = pair.indexOf('=');
            const name = pair.slice(0, eq);
            const value = pair.slice(eq + 1);
            const expired = attrs.some((a) => /^expires=/i.test(a) && new Date(a.slice(8)) < new Date());
            if (!value || expired) this.cookies.delete(name); else this.cookies.set(name, value);
          }
          const raw = Buffer.concat(chunks);
          let json = null;
          try { json = raw.length ? JSON.parse(raw.toString('utf8')) : null; } catch { /* JSON değil */ }
          resolve({ status: res.statusCode, headers: res.headers, raw, json });
        });
      });
      req.on('timeout', () => req.destroy(new Error('request timeout')));
      req.on('error', reject);
      if (payload) req.write(payload);
      req.end();
    });
  }

  get(url) { return this.request('GET', url); }
  post(url, body, extra) { return this.request('POST', url, { body, ...extra }); }
  put(url, body, extra) { return this.request('PUT', url, { body, ...extra }); }

  async login(email) {
    const token = await this.get('/api/auth/csrf');
    this.csrf = token.json.token;
    const response = await this.post('/api/auth/login', { email, password: password(email) });
    if (response.status === 200) this.csrf = response.json.csrfToken;
    return response;
  }

  clone() {
    const copy = new Client();
    copy.cookies = new Map(this.cookies);
    copy.csrf = this.csrf;
    return copy;
  }
}

const results = [];
const observedSecrets = new Set();
function check(name, condition, detail = '') {
  results.push({ name, ok: Boolean(condition), detail });
  console.log(`${condition ? 'PASS' : 'FAIL'}  ${name}${detail ? ` — ${detail}` : ''}`);
}
async function waitFor(name, probe, timeoutMs = 90000) {
  const started = Date.now();
  for (;;) {
    try { if (await probe()) return true; } catch { /* henüz değil */ }
    if (Date.now() - started > timeoutMs) { check(name, false, 'zaman aşımı'); return false; }
    await new Promise((r) => setTimeout(r, 1000));
  }
}
const ready = async () => (await new Client().get('/health/ready')).status;
const idem = () => `smoke-${randomUUID()}`;

async function main() {
  // --- Temel -------------------------------------------------------------------------------
  check('HTTPS hazır (sabitlenmiş sertifika)', (await ready()) === 200);
  const live = await new Client().get('/health/live');
  check('Güvenlik başlıkları', live.headers['x-content-type-options'] === 'nosniff' && /frame-ancestors 'none'/.test(live.headers['content-security-policy'] ?? ''));
  check('Server başlığı sızmıyor', !('server' in live.headers));
  check('Kimliksiz istek JSON 401', await (async () => {
    const r = await new Client().get('/api/auth/me');
    return r.status === 401 && r.json?.code === 'unauthenticated' && /problem\+json/.test(r.headers['content-type'] ?? '');
  })());

  // --- Demo kullanıcı eşlemesi --------------------------------------------------------------
  const expectations = {
    'platform-admin@loca.demo': (me) => me.isPlatformAdmin === true,
    'organizer-kumru@loca.demo': (me) => !me.isPlatformAdmin && me.memberships.some((m) => m.role === 'OrganizerAdmin'),
    'organizer-marti@loca.demo': (me) => !me.isPlatformAdmin && me.memberships.some((m) => m.role === 'OrganizerAdmin'),
    'gate-kumru@loca.demo': (me) => !me.isPlatformAdmin && me.memberships.length === 1 && me.memberships[0].role === 'GateStaff',
    'customer-ayse@loca.demo': (me) => !me.isPlatformAdmin && me.memberships.length === 0,
    'customer-mehmet@loca.demo': (me) => !me.isPlatformAdmin && me.memberships.length === 0,
  };
  const sessions = {};
  for (const [email, expectation] of Object.entries(expectations)) {
    const client = new Client();
    const login = await client.login(email);
    sessions[email] = client;
    observedSecrets.add(password(email));
    for (const value of client.cookies.values()) observedSecrets.add(value);
    check(`Demo girişi ve rol eşlemesi: ${email}`, login.status === 200 && expectation(login.json.user) && login.json.user.isDemo === true);
  }

  // --- Logout sonrası çerez replay ----------------------------------------------------------
  {
    const client = new Client();
    await client.login('customer-mehmet@loca.demo');
    const stolen = client.clone();
    const out = await client.post('/api/auth/logout');
    const replay = await stolen.get('/api/auth/me');
    check('Logout sonrası eski çerez reddedilir', out.status === 204 && replay.status === 401);
  }
  {
    const client = sessions['customer-ayse@loca.demo'].clone();
    client.csrf = null;
    const response = await client.post('/api/organizations', { name: 'CSRF Olmadan (Demo)' });
    check('CSRF token olmadan yazma reddedilir', response.status === 403 && response.json?.code === 'csrf_invalid');
  }

  for (const step of extraSteps) await step({ Client, sessions, check, idem, compose, waitFor });

  // --- Seed idempotency ---------------------------------------------------------------------
  {
    const output = compose('run', '--rm', '--no-deps', 'api', 'seed-demo');
    check('Seed idempotent (ikinci çalıştırma kayıt üretmez)', /seed-demo: completed \(created=0, existing=\d+\)/.test(output));
  }

  // --- Root olmayan çalışma -----------------------------------------------------------------
  {
    const uid = compose('exec', '-T', 'api', 'id', '-u').trim();
    check('API konteyneri root değil', uid !== '0' && /^\d+$/.test(uid), `uid=${uid}`);
  }

  if (!skipDisruptive) {
    // --- Yeniden başlatma kalıcılığı --------------------------------------------------------
    const before = sessions['organizer-kumru@loca.demo'];
    compose('down');
    compose('up', '--detach', '--wait', '--wait-timeout', '180');
    await waitFor('Yeniden başlatma sonrası hazır', async () => (await ready()) === 200);
    const me = await before.get('/api/auth/me');
    check('Restart sonrası veri ve oturum korunur (Data Protection anahtarları kalıcı)', me.status === 200 && me.json.email === 'organizer-kumru@loca.demo');
    const relogin = await new Client().login('customer-ayse@loca.demo');
    check('Restart sonrası demo kullanıcı aynı parolayla girer', relogin.status === 200);
    for (const step of afterRestartSteps) await step({ Client, sessions, check, idem, compose, waitFor });

    // --- Veritabanı kesintisi ve kurtarma ---------------------------------------------------
    compose('stop', 'postgres');
    const downReady = await waitFor('DB kapalıyken hazır değil', async () => (await ready()) === 503, 30000);
    check('DB kapalı: /health/ready 503', downReady);
    check('DB kapalı: /health/live 200', (await new Client().get('/health/live')).status === 200);
    const failing = await new Client();
    const csrf = await failing.get('/api/auth/csrf');
    failing.csrf = csrf.json?.token ?? null;
    const loginDown = await failing.post('/api/auth/login', { email: 'customer-ayse@loca.demo', password: password('customer-ayse@loca.demo') });
    check('DB kapalı: istek başarı göstermez (503)', loginDown.status === 503 && loginDown.json?.code === 'service_unavailable');
    compose('start', 'postgres');
    const recovered = await waitFor('DB açılınca kurtarma', async () => (await ready()) === 200, 90000);
    check('DB açıldı: /health/ready 200', recovered);
    check('DB açıldı: giriş yeniden çalışır', (await new Client().login('customer-ayse@loca.demo')).status === 200);
  }

  // --- Loglarda secret yok ------------------------------------------------------------------
  {
    const logs = compose('logs', '--no-color', '--tail', '5000');
    for (const file of ['db-superuser.password.txt', 'db-owner.password.txt', 'db-app.password.txt', 'psp-api-key.secret.txt', 'psp-webhook.secret.txt']) {
      const secretPath = path.join(stateDir, 'secrets', file);
      if (existsSync(secretPath)) observedSecrets.add(readFileSync(secretPath, 'utf8').trim());
    }
    const leaked = [...observedSecrets].filter((s) => s && s.length >= 8 && logs.includes(s));
    check('Konteyner loglarında secret/parola/çerez/token yok', leaked.length === 0, `${observedSecrets.size} değer tarandı`);
    check('Loglarda e-posta adresi yok', !/@loca\.demo/.test(logs));
  }
}

/** Sonraki aşamaların iş akışı adımları buraya eklenir (hold, ödeme, check-in ...). */
const extraSteps = [];
const afterRestartSteps = [];
export function trackSecret(value) { if (value) observedSecrets.add(value); }

const flowModule = path.join(path.dirname(fileURLToPath(import.meta.url)), 'smoke-flow.mjs');
if (existsSync(flowModule)) {
  const flow = await import(new URL(`file://${flowModule.replaceAll('\\', '/')}`).href);
  extraSteps.push(...flow.steps(trackSecret));
  afterRestartSteps.push(...(flow.afterRestart?.() ?? []));
}

try {
  await main();
} catch (error) {
  check('Beklenmeyen hata', false, String(error?.message ?? error));
}

const failed = results.filter((r) => !r.ok);
const reportDir = path.join(repoRoot, 'artifacts', 'verification');
mkdirSync(reportDir, { recursive: true });
writeFileSync(path.join(reportDir, `smoke-${project}.json`), JSON.stringify({
  project, origin, at: new Date().toISOString(), skipDisruptive,
  total: results.length, passed: results.length - failed.length, failed: failed.length, results,
}, null, 2));
console.log(`\nSmoke: ${results.length - failed.length}/${results.length} geçti${skipDisruptive ? ' (yıkıcı adımlar atlandı)' : ''}`);
process.exit(failed.length ? 1 : 0);
