import { expect, test } from '@playwright/test';
import { USERS } from './support/api';
import { login } from './support/ui';

test('11 · logout sonrası eski oturum: kaydedilmiş çerezlerle korumalı sayfa girişe düşer, /api/auth/me 401', async ({ browser }) => {
  const context = await browser.newContext();
  const page = await context.newPage();
  await login(page, USERS.ayse);
  await page.goto('/biletlerim');
  await expect(page.getByRole('heading', { level: 1, name: 'Biletlerim' })).toBeVisible();

  // Çıkıştan ÖNCEKİ çerezler saklanır (çalınmış oturum çerezi senaryosu).
  const stolen = await context.storageState();
  expect(stolen.cookies.length).toBeGreaterThan(0);
  const before = await browser.newContext({ storageState: stolen });
  expect((await before.request.get('/api/auth/me')).status()).toBe(200); // Çıkıştan önce çerezler geçerli.
  await before.close();

  await page.getByRole('button', { name: 'Çıkış yap' }).click();
  await expect(page.getByRole('link', { name: 'Giriş yap' })).toBeVisible();
  await expect(page.getByRole('navigation', { name: 'Ana gezinme' }).getByRole('link', { name: 'Biletlerim' })).toHaveCount(0);

  // Aynı tarayıcıda korumalı sayfa girişe yönlenir.
  await page.goto('/biletlerim');
  await expect(page).toHaveURL(/\/giris$/);
  await context.close();

  // Eski çerezlerle açılan yeni bağlam: oturum sunucuda iptal edilmiştir.
  const replay = await browser.newContext({ storageState: stolen });
  const me = await replay.request.get('/api/auth/me');
  expect(me.status()).toBe(401);
  expect(((await me.json()) as { code: string }).code).toBe('unauthenticated');
  const replayPage = await replay.newPage();
  for (const path of ['/biletlerim', '/siparislerim', '/organizator', '/kapi']) {
    await replayPage.goto(path);
    await expect(replayPage, path).toHaveURL(/\/giris$/);
    await expect(replayPage.getByRole('heading', { level: 1, name: 'Giriş yap' })).toBeVisible();
  }
  await replay.close();
});

test('oturum sunucuda biterse açık sayfa girişe yönlenir ve özel veri ekranda kalmaz', async ({ browser }) => {
  const context = await browser.newContext();
  const page = await context.newPage();
  await login(page, USERS.mehmet);
  await page.goto('/siparislerim');
  await expect(page.getByRole('heading', { level: 1, name: 'Siparişlerim' })).toBeVisible();

  // Oturum çerezi artık geçerli değil: sonraki API isteği 401 döner.
  await context.clearCookies();
  await page.getByRole('navigation', { name: 'Ana gezinme' }).getByRole('link', { name: 'Biletlerim' }).click();
  await expect(page).toHaveURL(/\/giris$/);
  await expect(page.getByRole('button', { name: 'Çıkış yap' })).toHaveCount(0);
  await context.close();
});

test('12 · CSRF: token başlığı olmadan tarayıcı içinden POST → 403 csrf_invalid', async ({ page }) => {
  await login(page, USERS.ayse);

  const result = await page.evaluate(async () => {
    const response = await fetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin' });
    return { status: response.status, type: response.headers.get('content-type'), body: (await response.json()) as { code?: string } };
  });
  expect(result.status).toBe(403);
  expect(result.type).toContain('application/problem+json');
  expect(result.body.code).toBe('csrf_invalid');

  // Yanlış token da reddedilir.
  const forged = await page.evaluate(async () => {
    const response = await fetch('/api/auth/logout', { method: 'POST', credentials: 'same-origin', headers: { 'X-CSRF-TOKEN': 'forged-token' } });
    return { status: response.status, body: (await response.json()) as { code?: string } };
  });
  expect(forged.status).toBe(403);
  expect(forged.body.code).toBe('csrf_invalid');

  // Reddedilen istek oturumu kapatmadı.
  expect(await page.evaluate(async () => (await fetch('/api/auth/me', { credentials: 'same-origin' })).status)).toBe(200);
  await page.reload();
  await expect(page.getByRole('button', { name: 'Çıkış yap' })).toBeVisible();
});

test('istemci token/secret\'i tarayıcı deposuna yazmaz', async ({ page }) => {
  await login(page, USERS.ayse);
  await page.goto('/biletlerim');
  await expect(page.getByRole('heading', { level: 1, name: 'Biletlerim' })).toBeVisible();
  // İfade metin olarak çalıştırılır: uygulama kodundaki depo yasağı (ESLint) test kodunu da kapsar.
  const storage = await page.evaluate<{ local: number; session: number }>('({ local: window.localStorage.length, session: window.sessionStorage.length })');
  expect(storage).toEqual({ local: 0, session: 0 });
  // Oturum çerezi betikten okunamaz (HttpOnly).
  const visible = await page.evaluate(() => document.cookie);
  const cookies = await page.context().cookies();
  const httpOnly = cookies.filter((cookie) => cookie.httpOnly);
  expect(httpOnly.length).toBeGreaterThan(0);
  for (const cookie of httpOnly) expect(visible).not.toContain(cookie.value);
});
