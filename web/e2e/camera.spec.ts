// Kamera yolu, Chromium'un SAHTE kamera aygıtıyla sınanır: tarayıcıya gerçek kamera yerine, biletin QR'ını
// içeren sentetik bir video dosyası verilir. getUserMedia → <video> → canvas → jsQR → check-in zinciri gerçek
// kodla çalışır; ancak gerçek bir cihaz kamerası (odak, ışık, açı, izin arayüzü) DOĞRULANMAZ.
import { mkdtempSync, rmSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { chromium, expect, test } from '@playwright/test';
import { createQrMatrix, QUIET_ZONE_MODULES } from '../src/lib/qr';
import { ApiSession, assignGateStaff, baseUrl, createSeatedSession, purchase, seatsOf, USERS } from './support/api';
import { login } from './support/ui';

const WIDTH = 640;
const HEIGHT = 480;

/** Token'ın QR'ını ortasında gösteren kısa bir YUV4MPEG2 (y4m) videosu yazar. */
function writeQrVideo(file: string, token: string): void {
  const matrix = createQrMatrix(token);
  const modules = matrix.size + QUIET_ZONE_MODULES * 2;
  const scale = Math.floor(Math.min(WIDTH, HEIGHT) / modules);
  const offsetX = Math.floor((WIDTH - modules * scale) / 2);
  const offsetY = Math.floor((HEIGHT - modules * scale) / 2);
  const luma = Buffer.alloc(WIDTH * HEIGHT, 235);
  for (let row = 0; row < matrix.size; row++) {
    for (let column = 0; column < matrix.size; column++) {
      if (!matrix.isDark(row, column)) continue;
      const x0 = offsetX + (column + QUIET_ZONE_MODULES) * scale;
      const y0 = offsetY + (row + QUIET_ZONE_MODULES) * scale;
      for (let y = y0; y < y0 + scale; y++) luma.fill(16, y * WIDTH + x0, y * WIDTH + x0 + scale);
    }
  }
  const chroma = Buffer.alloc((WIDTH / 2) * (HEIGHT / 2) * 2, 128);
  const frame = Buffer.concat([Buffer.from('FRAME\n', 'ascii'), luma, chroma]);
  const header = Buffer.from(`YUV4MPEG2 W${WIDTH} H${HEIGHT} F10:1 Ip A1:1 C420jpeg\n`, 'ascii');
  writeFileSync(file, Buffer.concat([header, ...Array.from({ length: 10 }, () => frame)]));
}

test('kamera (sahte aygıt): izin verilince QR kadrajdan okunur → KABUL; aynı kod yeniden gönderilmez', async () => {
  const organizer = await ApiSession.login(USERS.kumru);
  const created = await createSeatedSession(organizer, { title: 'Kamera Gösterisi', startsInHours: 2 });
  await assignGateStaff(organizer, created);
  await organizer.dispose();
  const customer = await ApiSession.login(USERS.ayse);
  const seats = await seatsOf(customer, created.sessionId);
  const bought = await purchase(customer, created.sessionId, { seatIds: [seats[0]!.id] });
  const token = (await customer.get<{ token: string }>(`/api/tickets/${bought.ticketIds[0]}`)).token;
  await customer.dispose();

  const dir = mkdtempSync(path.join(tmpdir(), 'loca-e2e-camera-'));
  const video = path.join(dir, 'ticket-qr.y4m');
  writeQrVideo(video, token);
  const browser = await chromium.launch({
    args: ['--use-fake-device-for-media-stream', '--use-fake-ui-for-media-stream', `--use-file-for-fake-video-capture=${video}`],
  });
  try {
    const context = await browser.newContext({
      baseURL: baseUrl(), ignoreHTTPSErrors: true, locale: 'tr-TR', timezoneId: 'Europe/Istanbul',
      viewport: { width: 360, height: 780 }, permissions: ['camera'],
    });
    const page = await context.newPage();
    let checkIns = 0;
    page.on('request', (request) => {
      if (request.url().includes('/check-ins')) checkIns += 1;
    });
    await login(page, USERS.gate);
    await page.goto(`/kapi/${created.sessionId}`);
    const result = page.getByTestId('gate-result');
    await expect(result).toHaveAttribute('data-r', 'idle');
    await expect(page.getByTestId('camera-status')).toHaveText('Kamera kapalı');

    await page.getByRole('button', { name: 'Kamerayı aç' }).click();
    await expect(page.getByTestId('camera-video')).toBeVisible();

    // Kullanıcı hiçbir şey yazmadan: kadrajdaki QR çözülür ve check-in yapılır.
    await expect(result).toHaveAttribute('data-r', 'accepted', { timeout: 30_000 });
    await expect(result).toContainText('KABUL');
    await expect(result).toContainText('Salon / A / 1');
    // Sonuç ekrandayken tarama durur: aynı bilet kadrajda kalsa da ikinci istek gitmez.
    await expect(page.getByTestId('camera-status')).toContainText('Tarama duraklatıldı');
    await page.waitForTimeout(1500);
    expect(checkIns).toBe(1);

    // "Sıradaki bilet"ten sonra aynı kod yeniden okunur: sunucu ZATEN KULLANILDI der.
    await result.getByRole('button', { name: 'Sıradaki bilet' }).click();
    await expect(result).toHaveAttribute('data-r', 'used', { timeout: 30_000 });
    await expect(result).toContainText('ZATEN KULLANILDI');

    await page.getByRole('button', { name: 'Kamerayı kapat' }).click();
    await expect(page.getByTestId('camera-status')).toHaveText('Kamera kapalı');
    await context.close();
  } finally {
    await browser.close();
    rmSync(dir, { recursive: true, force: true });
  }
});

test('kamera izni reddedilince açık metin gösterilir ve elle giriş kullanılabilir kalır', async () => {
  const organizer = await ApiSession.login(USERS.kumru);
  const created = await createSeatedSession(organizer, { title: 'Kamera İzni Gösterisi', startsInHours: 2 });
  await assignGateStaff(organizer, created);
  await organizer.dispose();

  // Sahte aygıt var ama izin verilmiyor: getUserMedia NotAllowedError ile reddedilir. Headless shell izin akışını
  // desteklemediğinden (NotSupportedError verir) bu test tam Chromium'un yeni headless kipiyle çalışır.
  const browser = await chromium.launch({ channel: 'chromium', args: ['--use-fake-device-for-media-stream'] });
  try {
    const context = await browser.newContext({
      baseURL: baseUrl(), ignoreHTTPSErrors: true, locale: 'tr-TR', timezoneId: 'Europe/Istanbul', viewport: { width: 360, height: 780 },
    });
    const page = await context.newPage();
    await login(page, USERS.gate);
    await page.goto(`/kapi/${created.sessionId}`);
    await page.getByRole('button', { name: 'Kamerayı aç' }).click();
    await expect(page.getByTestId('camera-status')).toContainText('Kamera izni verilmedi.');
    await expect(page.getByTestId('camera-status')).toContainText('kodu aşağıdan elle gir');
    await expect(page.getByRole('button', { name: 'Kamerayı yeniden dene' })).toBeVisible();

    // Elle giriş her zaman çalışır.
    await page.getByLabel('Elle kod girişi').fill(`LT1.${'C'.repeat(43)}`);
    await page.getByRole('button', { name: 'Kontrol et' }).click();
    await expect(page.getByTestId('gate-result')).toHaveAttribute('data-r', 'invalid');
    await context.close();
  } finally {
    await browser.close();
  }
});

test('kamera API\'si olmayan tarayıcıda "desteklenmiyor" metni gösterilir', async ({ page }) => {
  const organizer = await ApiSession.login(USERS.kumru);
  const created = await createSeatedSession(organizer, { title: 'Kamerasız Tarayıcı Gösterisi', startsInHours: 2 });
  await assignGateStaff(organizer, created);
  await organizer.dispose();

  // Eski/kısıtlı tarayıcı benzetimi: mediaDevices tanımsız.
  await page.addInitScript('Object.defineProperty(navigator, "mediaDevices", { value: undefined, configurable: true })');
  await login(page, USERS.gate);
  await page.goto(`/kapi/${created.sessionId}`);
  await page.getByRole('button', { name: 'Kamerayı aç' }).click();
  await expect(page.getByTestId('camera-status')).toContainText('Bu tarayıcı kamera erişimini desteklemiyor.');
  await expect(page.getByLabel('Elle kod girişi')).toBeEditable();
});
