import { expect, test, type Page } from '@playwright/test';
import { ApiSession, assignGateStaff, createSeatedSession, idempotencyKey, purchase, seatsOf, USERS } from './support/api';
import { decodeQr, newUserPage } from './support/ui';

const MOBILE = { viewport: { width: 360, height: 780 } };

async function scan(page: Page, token: string, gate?: string): Promise<void> {
  await page.getByLabel('Elle kod girişi').fill(token);
  if (gate !== undefined) await page.getByLabel('Kapı adı (isteğe bağlı)').fill(gate);
  await page.getByRole('button', { name: 'Kontrol et' }).click();
}

test('4 · kapı: QR\'dan çözülen token elle girilir → KABUL; aynı bilet ikinci kez → ZATEN KULLANILDI', async ({ browser }) => {
  const organizer = await ApiSession.login(USERS.kumru);
  // Başlangıç 2 saat sonra: giriş penceresi (başlangıçtan 3 saat önce) açık.
  const created = await createSeatedSession(organizer, { title: 'Kapı Gösterisi', startsInHours: 2 });
  await assignGateStaff(organizer, created);
  await organizer.dispose();
  const customerApi = await ApiSession.login(USERS.ayse);
  const seats = await seatsOf(customerApi, created.sessionId);
  const bought = await purchase(customerApi, created.sessionId, { seatIds: [seats[0]!.id] });
  await customerApi.dispose();

  // Müşteri biletini açar; token ekrandaki QR'dan çözülür.
  const customer = await newUserPage(browser, USERS.ayse);
  await customer.page.goto(`/bilet/${bought.ticketIds[0]}`);
  const token = await decodeQr(customer.page.getByTestId('ticket-qr'));
  expect(token).toMatch(/^LT1\./);

  const gate = await newUserPage(browser, USERS.gate, MOBILE);
  try {
    const page = gate.page;
    // Kapı görevlisi gezinmede "Kapı"yı görür, "Organizatör" ve "Yönetim"i görmez.
    await expect(page.getByRole('navigation', { name: 'Ana gezinme' }).getByRole('link', { name: 'Kapı' })).toBeVisible();
    await expect(page.getByRole('navigation', { name: 'Ana gezinme' }).getByRole('link', { name: 'Organizatör' })).toHaveCount(0);
    await page.getByRole('navigation', { name: 'Ana gezinme' }).getByRole('link', { name: 'Kapı' }).click();
    await page.getByTestId('gate-sessions').getByRole('link', { name: new RegExp(created.eventTitle) }).click();
    await expect(page.getByRole('heading', { level: 1 })).toHaveText(created.eventTitle);
    const result = page.getByTestId('gate-result');
    await expect(result).toHaveAttribute('data-r', 'idle');
    await expect(page.getByText('Giriş yapan: 0 / 1')).toBeVisible();

    // Boş gönderim: alan hatası.
    await page.getByRole('button', { name: 'Kontrol et' }).click();
    await expect(page.getByText('Kodu yaz veya bileti okut.')).toBeVisible();

    // Geçersiz kod.
    await scan(page, `LT1.${'A'.repeat(43)}`);
    await expect(result).toHaveAttribute('data-r', 'invalid');
    await expect(result).toContainText('GEÇERSİZ');
    await expect(result).toContainText('Bu kodla bilet bulunamadı.');
    await result.getByRole('button', { name: 'Sıradaki bilet' }).click();
    await expect(result).toHaveAttribute('data-r', 'idle');

    // Bağlantı koparsa: sonuç bilinmiyor; aynı tarama aynı Idempotency-Key ile yeniden denenir.
    const keys: string[] = [];
    let drop = true;
    await page.route('**/api/gate/sessions/*/check-ins', async (route) => {
      keys.push(route.request().headers()['idempotency-key'] ?? '');
      if (drop) await route.abort('connectionfailed');
      else await route.continue();
    });
    await scan(page, token, 'A');
    await expect(result).toHaveAttribute('data-r', 'offline');
    await expect(result).toContainText('BAĞLANTI YOK');
    await expect(result).toContainText('Sonuç bilinmiyor — aynı tarama yeniden denenecek.');
    await expect(result).not.toContainText('KABUL');
    drop = false;
    await result.getByRole('button', { name: 'Yeniden dene' }).click();

    // KABUL.
    await expect(result).toHaveAttribute('data-r', 'accepted');
    await expect(result).toContainText('KABUL');
    await expect(result).toContainText('Salon / A / 1');
    await expect(result).toContainText('Kapı A');
    expect(keys.length).toBeGreaterThanOrEqual(2);
    expect(new Set(keys).size).toBe(1);
    expect(keys[0]).toMatch(/^[A-Za-z0-9_.:-]{16,128}$/);
    await page.unroute('**/api/gate/sessions/*/check-ins');
    await expect(page.getByText('Giriş yapan: 1 / 1')).toBeVisible({ timeout: 20_000 });

    // Aynı bilet ikinci kez.
    await result.getByRole('button', { name: 'Sıradaki bilet' }).click();
    await scan(page, token, 'B');
    await expect(result).toHaveAttribute('data-r', 'used');
    await expect(result).toContainText('ZATEN KULLANILDI');
    await expect(result).toContainText('İlk giriş:');
    await expect(result).toContainText('Kapı A');
    await expect(result).toContainText('İçeri alma.');

    // Çevrimdışı: tarama gönderilmez (offline check-in yok).
    await result.getByRole('button', { name: 'Sıradaki bilet' }).click();
    let sent = 0;
    page.on('request', (request) => {
      if (request.url().includes('/check-ins')) sent += 1;
    });
    await gate.context.setOffline(true);
    await expect(page.getByText('Çevrimdışı — check-in yapılamaz. Bağlantı gelene kadar tarama gönderilmez.')).toBeVisible();
    await scan(page, token);
    await expect(result).toHaveAttribute('data-r', 'device-offline');
    await expect(result).toContainText('ÇEVRİMDIŞI');
    expect(sent).toBe(0);
    await gate.context.setOffline(false);
  } finally {
    await gate.context.close();
  }

  // Müşteri tarafında bilet artık "Kullanıldı" ve QR gösterilmez.
  await customer.page.reload();
  await expect(customer.page.getByTestId('ticket-status')).toContainText('Kullanıldı');
  await expect(customer.page.getByTestId('ticket-qr')).toHaveCount(0);
  await customer.context.close();
});

test('kapı: görevli yalnız atandığı seansı görür; atanmadığı seansın adresi "Bulunamadı"', async ({ browser }) => {
  const organizer = await ApiSession.login(USERS.kumru);
  const assigned = await createSeatedSession(organizer, { title: 'Atanmış Seans', startsInHours: 2 });
  const other = await createSeatedSession(organizer, { title: 'Atanmamış Seans', startsInHours: 2 });
  await assignGateStaff(organizer, assigned);
  await organizer.dispose();

  const gate = await newUserPage(browser, USERS.gate, MOBILE);
  try {
    const page = gate.page;
    await page.goto('/kapi');
    await expect(page.getByTestId('gate-sessions')).toContainText(assigned.eventTitle);
    await expect(page.getByTestId('gate-sessions')).not.toContainText(other.eventTitle);

    await page.goto(`/kapi/${other.sessionId}`);
    await expect(page.getByText('Bu seans yok ya da kapı ekibinde değilsin.')).toBeVisible();

    // Sunucu da aynı kararı verir: atanmamış seansa check-in isteği 404.
    const csrf = ((await (await page.request.get('/api/auth/csrf')).json()) as { token: string }).token;
    const refused = await page.request.post(`/api/gate/sessions/${other.sessionId}/check-ins`, {
      data: { token: `LT1.${'A'.repeat(43)}` }, headers: { 'X-CSRF-TOKEN': csrf, 'Idempotency-Key': idempotencyKey() },
    });
    expect(refused.status()).toBe(404);

    await page.goto(`/kapi/${assigned.sessionId}`);
    await expect(page.getByTestId('camera-status')).toHaveText('Kamera kapalı');
    await expect(page.getByLabel('Elle kod girişi')).toBeEditable();
    await expect(page.getByRole('button', { name: 'Kontrol et' })).toBeEnabled();
  } finally {
    await gate.context.close();
  }
});
