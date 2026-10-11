import { expect, test, type Page } from '@playwright/test';
import { ApiSession, createSeatedSession, USERS } from './support/api';
import { login, newUserPage, orderStatus, seatCheckbox } from './support/ui';

async function holdSeat(page: Page, sessionId: string, row: string, number: number): Promise<void> {
  await page.goto(`/seans/${sessionId}`);
  await seatCheckbox(page, row, number).check();
  await page.getByRole('button', { name: 'Koltukları tut' }).click();
  await expect(page).toHaveURL(/\/hold\//);
}

test('5 · hold süre dolumu: sayaç biter → "Süre doldu"; koltuk yeniden seçilebilir', async ({ page }) => {
  const organizer = await ApiSession.login(USERS.kumru);
  const created = await createSeatedSession(organizer, { title: 'Kısa Süreli Gösteri', holdTtlSeconds: 8 });
  await organizer.dispose();

  await login(page, USERS.ayse);
  await holdSeat(page, created.sessionId, 'A', 1);
  const holdUrl = page.url();
  // Sayaç sunucu süresinden (8 sn) kurulur.
  await expect(page.getByRole('timer')).toHaveText(/^00:0[1-8]$/);
  await expect(page.getByRole('button', { name: /Ödemeyi başlat/ })).toBeVisible();

  await expect(page.getByRole('heading', { name: 'Süre doldu' })).toBeVisible({ timeout: 25_000 });
  await expect(page.getByText('Rezervasyon süresi ödeme başlamadan bitti.')).toBeVisible();
  await expect(page.getByRole('button', { name: /Ödemeyi başlat/ })).toHaveCount(0);
  await expect(page.getByRole('timer')).toHaveCount(0);

  // Yeniden seçime dön: aynı koltuk yeniden boş ve seçilebilir.
  await page.getByRole('link', { name: 'Yeniden seç' }).click();
  await expect(page).toHaveURL(new RegExp(`/seans/${created.sessionId}$`));
  await expect(seatCheckbox(page, 'A', 1)).toBeEnabled({ timeout: 15_000 });
  await seatCheckbox(page, 'A', 1).check();
  await expect(page.getByTestId('selection-count')).toHaveText('1 / 6');
  await page.getByRole('button', { name: 'Koltukları tut' }).click();
  await expect(page).toHaveURL(/\/hold\//);
  expect(page.url()).not.toBe(holdUrl);
  await expect(page.getByRole('rowheader', { name: 'Salon / A / 1' })).toBeVisible();
});

test('6 · çakışma: iki müşteri aynı koltuğu ister; ikincisi anlaşılır mesaj alır ve seçimi düzeltilir', async ({ browser }) => {
  const organizer = await ApiSession.login(USERS.kumru);
  const created = await createSeatedSession(organizer, { title: 'Çakışma Gösterisi' });
  await organizer.dispose();

  const first = await newUserPage(browser, USERS.ayse);
  const second = await newUserPage(browser, USERS.mehmet);
  try {
    await first.page.goto(`/seans/${created.sessionId}`);
    await second.page.goto(`/seans/${created.sessionId}`);
    await seatCheckbox(first.page, 'A', 1).check();
    await seatCheckbox(second.page, 'A', 1).check();
    await seatCheckbox(second.page, 'A', 2).check();
    await expect(second.page.getByTestId('selection-count')).toHaveText('2 / 6');

    // İkinci müşterinin planı bayat kalsın: müsaitlik yalnız ipucudur, kesin sonuç hold isteğindedir.
    let stale = true;
    const freshSeats = await second.page.request.get(`/api/catalog/sessions/${created.sessionId}/seats`);
    const staleBody = await freshSeats.text();
    await second.page.route('**/api/catalog/sessions/*/seats', (route) =>
      stale ? route.fulfill({ status: 200, contentType: 'application/json', body: staleBody }) : route.continue());

    await first.page.getByRole('button', { name: 'Koltukları tut' }).click();
    await expect(first.page).toHaveURL(/\/hold\//);

    const conflict = second.page.waitForResponse((r) => r.url().includes('/holds') && r.request().method() === 'POST');
    await second.page.getByRole('button', { name: 'Koltukları tut' }).click();
    expect((await conflict).status()).toBe(409);
    stale = false;

    const message = second.page.locator('#hold-message');
    await expect(message).toContainText('Salon / A / 1 az önce başkası tarafından alındı.');
    await expect(message).toContainText('Hiçbir koltuk tutulmadı');
    await expect(second.page).toHaveURL(new RegExp(`/seans/${created.sessionId}$`));
    // Seçim düzeltildi (yalnız A2 kaldı) ve plan yenilendi (A1 artık dolu).
    await expect(second.page.getByTestId('selection-count')).toHaveText('1 / 6');
    await expect(second.page.getByTestId('selection-labels')).toHaveText('Salon / A / 2');
    await expect(second.page.getByRole('checkbox', { name: 'Koltuk 1, dolu' })).toBeDisabled({ timeout: 15_000 });
    await expect(second.page.locator('[data-seat-label="Salon / A / 1"]')).toHaveClass(/is-taken/);

    // Kalan seçimle yeniden denenebilir.
    await second.page.getByRole('button', { name: 'Koltukları tut' }).click();
    await expect(second.page).toHaveURL(/\/hold\//);
    await expect(second.page.getByRole('rowheader', { name: 'Salon / A / 2' })).toBeVisible();
  } finally {
    await first.context.close();
    await second.context.close();
  }
});

test('7 · ret: tok_decline → "Reddedildi"', async ({ page }) => {
  const organizer = await ApiSession.login(USERS.kumru);
  const created = await createSeatedSession(organizer, { title: 'Ret Gösterisi', demoSimulator: true });
  await organizer.dispose();

  await login(page, USERS.ayse);
  await holdSeat(page, created.sessionId, 'A', 1);
  // Demo simülatörü açık seansta senaryo seçici "Demo" etiketiyle görünür.
  const scenarios = page.getByRole('group', { name: /Demo ödeme senaryosu/ });
  await expect(scenarios).toBeVisible();
  await expect(scenarios.getByRole('radio')).toHaveCount(5);
  await expect(scenarios.getByRole('radio', { name: /^Başarılı/ })).toBeChecked();
  await scenarios.getByRole('radio', { name: /^Ret/ }).check();
  await page.getByRole('button', { name: /Ödemeyi başlat/ }).click();

  await expect(orderStatus(page).getByRole('heading')).toHaveText('Reddedildi', { timeout: 30_000 });
  await expect(orderStatus(page)).toContainText('Tahsilat yapılmadı ve bilet oluşmadı.');
  await expect(orderStatus(page).getByRole('link', { name: /Biletleri gör/ })).toHaveCount(0);

  // Koltuk yeniden satışa açılır.
  await orderStatus(page).getByRole('link', { name: 'Yeniden seç' }).click();
  await expect(seatCheckbox(page, 'A', 1)).toBeEnabled({ timeout: 15_000 });
});

test('8 · belirsiz → kurtarılan ödeme: önce "Sonuç belirsiz", sonra kendiliğinden "Ödendi"', async ({ page }) => {
  test.setTimeout(240_000);
  const organizer = await ApiSession.login(USERS.kumru);
  const created = await createSeatedSession(organizer, { title: 'Belirsiz Gösteri', demoSimulator: true });
  await organizer.dispose();

  await login(page, USERS.ayse);
  await holdSeat(page, created.sessionId, 'A', 1);
  await page.getByRole('radio', { name: /^Belirsiz/ }).check();
  await page.getByRole('button', { name: /Ödemeyi başlat/ }).click();

  const heading = orderStatus(page).getByRole('heading');
  await expect(heading).toHaveText('Sonuç belirsiz', { timeout: 45_000 });
  // Belirsiz sonuç ret ya da başarı olarak sunulmaz.
  await expect(orderStatus(page)).toContainText('Bu bir ret değildir');
  await expect(orderStatus(page)).toHaveAttribute('aria-busy', 'true');
  await expect(orderStatus(page).getByRole('link', { name: /Biletleri gör/ })).toHaveCount(0);

  // Kullanıcı hiçbir şey yapmadan (reconciliation worker) sonuç kesinleşir.
  await expect(heading).toHaveText('Ödendi', { timeout: 150_000 });
  await expect(orderStatus(page)).toContainText('1 bilet hazır', { timeout: 30_000 });
});

test('geç başarı: süre dolduktan sonra gelen tahsilat bilet üretmez, telafi iadesi notu gösterilir', async ({ page }) => {
  test.setTimeout(240_000);
  const organizer = await ApiSession.login(USERS.kumru);
  const created = await createSeatedSession(organizer, { title: 'Geç Başarı Gösterisi', demoSimulator: true, holdTtlSeconds: 8 });
  await organizer.dispose();

  await login(page, USERS.mehmet);
  await holdSeat(page, created.sessionId, 'A', 1);
  await page.getByRole('radio', { name: /^Geç başarı/ }).check();
  await page.getByRole('button', { name: /Ödemeyi başlat/ }).click();

  const heading = orderStatus(page).getByRole('heading');
  await expect(heading).toHaveText('Süre doldu', { timeout: 60_000 });
  // Geç gelen başarı siparişi yeniden "Ödendi" yapmaz; tutar iade edilir.
  await expect(page.getByTestId('late-success-note')).toBeVisible({ timeout: 120_000 });
  await expect(page.getByTestId('late-success-note')).toContainText('Bilet verilmedi');
  await expect(heading).toHaveText('Süre doldu');
  await expect(orderStatus(page).getByRole('link', { name: /Biletleri gör/ })).toHaveCount(0);
});
