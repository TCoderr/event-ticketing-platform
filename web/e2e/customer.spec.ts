import { expect, test, type Locator, type Page } from '@playwright/test';
import { ApiSession, createGaSession, createSeatedSession, purchase, seatsOf, USERS } from './support/api';
import { decodeQr, login, orderStatus, seatCheckbox, submitLogin } from './support/ui';

/** Keşif listesinde seansı bulur; gerekirse "daha fazla" ile sonraki sayfaları yükler. */
async function findInDiscovery(page: Page, title: string): Promise<Locator> {
  const row = page.getByTestId('session-rows').getByRole('listitem').filter({ hasText: title });
  const more = page.getByRole('button', { name: 'Daha fazla seans göster' });
  await expect(page.getByTestId('session-rows')).toBeVisible();
  for (let i = 0; i < 20 && (await row.count()) === 0 && (await more.isVisible()); i++) {
    await more.click();
    await expect(more).toBeEnabled().catch(() => undefined);
  }
  await expect(row).toHaveCount(1);
  return row;
}

async function tabTo(page: Page, target: Locator, limit = 80): Promise<void> {
  for (let i = 0; i < limit; i++) {
    await page.keyboard.press('Tab');
    if (await target.evaluate((element) => element === document.activeElement)) return;
  }
  throw new Error('Klavye odağı hedef öğeye ulaşmadı.');
}

test('1 · müşteri: keşif → koltuklu seans → koltuk seç → tut → öde → Ödendi → Biletlerim\'de gerçek QR', async ({ page }) => {
  const organizer = await ApiSession.login(USERS.kumru);
  const created = await createSeatedSession(organizer, { title: 'Koltuklu Gösteri' });
  await organizer.dispose();

  // Keşif (girişsiz).
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 1, name: 'Yaklaşan seanslar' })).toBeVisible();
  const row = await findInDiscovery(page, created.eventTitle);
  await expect(row).toContainText('Satışta');
  await expect(row).toContainText('450,00 ₺');
  await expect(row).toContainText('· Europe/Istanbul');
  await row.getByRole('link', { name: /Koltuk seç/ }).click();

  // Koltuk seçimi: biri haritadan, biri listeden; ikisi aynı durumu gösterir.
  await expect(page).toHaveURL(new RegExp(`/seans/${created.sessionId}$`));
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(created.eventTitle);
  await page.locator('[data-seat-label="Salon / A / 1"]').click();
  await expect(seatCheckbox(page, 'A', 1)).toBeChecked();
  await seatCheckbox(page, 'A', 2).check();
  await expect(page.locator('[data-seat-label="Salon / A / 2"]')).toHaveClass(/is-sel/);
  await expect(page.getByTestId('selection-count')).toHaveText('2 / 6');
  await expect(page.getByTestId('selection-total')).toHaveText('900,00 ₺');

  // Girişsiz "tut": giriş ekranına gider ve aynı seçimle geri döner.
  await page.getByRole('button', { name: 'Koltukları tut' }).click();
  await expect(page).toHaveURL(/\/giris$/);
  await submitLogin(page, USERS.ayse);
  await expect(page).toHaveURL(new RegExp(`/seans/${created.sessionId}$`));
  await expect(seatCheckbox(page, 'A', 1)).toBeChecked();
  await expect(seatCheckbox(page, 'A', 2)).toBeChecked();
  await page.getByRole('button', { name: 'Koltukları tut' }).click();

  // Hold + ödeme.
  await expect(page).toHaveURL(/\/hold\/[0-9a-f-]{36}$/);
  await expect(page.getByRole('heading', { level: 1, name: 'Ödeme' })).toBeVisible();
  await expect(page.getByRole('timer')).toHaveText(/^0\d:[0-5]\d$/);
  await expect(page.getByRole('rowheader', { name: 'Salon / A / 1' })).toBeVisible();
  await expect(page.getByTestId('hold-total')).toHaveText('900,00 ₺');
  await expect(page.getByText('Demo ödeme senaryosu')).toHaveCount(0); // Simülatör kapalı seansta senaryo seçici yok.
  await page.getByRole('button', { name: /Ödemeyi başlat/ }).click();

  // Sipariş durumu: 202 "ödendi" değildir; gerçek sonuç yoklanır.
  await expect(page).toHaveURL(/\/siparis\/[0-9a-f-]{36}$/);
  await expect(orderStatus(page).getByRole('heading')).toHaveText('Ödendi', { timeout: 30_000 });
  await expect(orderStatus(page)).toContainText('900,00 ₺ tahsil edildi');
  await expect(orderStatus(page)).toContainText('2 bilet hazır', { timeout: 30_000 });

  // Biletlerim → bilet → QR.
  await orderStatus(page).getByRole('link', { name: /Biletleri gör/ }).click();
  await expect(page).toHaveURL(/\/biletlerim$/);
  const tickets = page.getByTestId('ticket-list').getByRole('listitem').filter({ hasText: created.eventTitle });
  await expect(tickets).toHaveCount(2);
  await expect(tickets.first()).toContainText('Geçerli');
  await tickets.filter({ hasText: 'Salon / A / 1' }).getByRole('link', { name: /Bileti aç/ }).click();

  await expect(page).toHaveURL(/\/bilet\/[0-9a-f-]{36}$/);
  const ticket = page.getByRole('article');
  await expect(ticket.getByRole('heading', { level: 2 })).toHaveText(created.eventTitle);
  await expect(ticket.getByText('Salon / A / 1', { exact: true })).toBeVisible();
  await expect(page.getByTestId('ticket-status')).toContainText('Geçerli');
  const qr = page.getByTestId('ticket-qr');
  await expect(qr).toBeVisible();
  // QR gerçek: ekran görüntüsü bağımsız bir çözücüyle okunur ve sunucunun verdiği token çıkar.
  const token = await decodeQr(qr);
  expect(token).toMatch(/^LT1\.[A-Za-z0-9_-]{43}$/);
  await page.getByRole('button', { name: 'Kodu metin olarak göster' }).click();
  await expect(page.getByTestId('ticket-token')).toHaveText(token);
});

test('2 · müşteri: genel giriş seansında kategori/adet → tut → öde → bilet', async ({ page }) => {
  const organizer = await ApiSession.login(USERS.kumru);
  const created = await createGaSession(organizer, {
    title: 'Genel Giriş Konseri',
    categories: [{ name: 'Ayakta', priceMinor: 32000, capacity: 40 }, { name: 'Tribün', priceMinor: 48000, capacity: 3 }],
  });
  await organizer.dispose();

  await login(page, USERS.mehmet);
  await page.goto(`/etkinlik/${created.eventSlug}`);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(created.eventTitle);
  await page.getByTestId('session-rows').getByRole('link', { name: /Bilet seç/ }).click();
  await expect(page).toHaveURL(new RegExp(`/seans/${created.sessionId}$`));

  await page.getByRole('button', { name: 'Ayakta: bir artır' }).click();
  await page.getByRole('button', { name: 'Ayakta: bir artır' }).click();
  await page.getByRole('button', { name: 'Tribün: bir artır' }).click();
  await expect(page.getByRole('group', { name: 'Ayakta adedi' }).getByRole('status')).toHaveText('2');
  await expect(page.getByTestId('selection-count')).toHaveText('3 / 6');
  await expect(page.getByTestId('selection-labels')).toContainText('2 × Ayakta');
  await expect(page.getByTestId('selection-total')).toHaveText('1.120,00 ₺');

  // Toplam 6 sınırı: 3 Ayakta daha → 6; yedinci reddedilir.
  for (let i = 0; i < 3; i++) await page.getByRole('button', { name: 'Ayakta: bir artır' }).click();
  await expect(page.getByTestId('selection-count')).toHaveText('6 / 6');
  // Sınırdaki düğme aria-disabled'dır; yine de tıklanınca nedenini söyler.
  await page.getByRole('button', { name: 'Ayakta: bir artır' }).click({ force: true });
  await expect(page.getByRole('alert').filter({ hasText: 'en fazla 6 bilet' })).toBeVisible();
  await expect(page.getByTestId('selection-count')).toHaveText('6 / 6');
  for (let i = 0; i < 3; i++) await page.getByRole('button', { name: 'Ayakta: bir azalt' }).click();
  await expect(page.getByTestId('selection-total')).toHaveText('1.120,00 ₺');

  await page.getByRole('button', { name: 'Biletleri tut' }).click();
  await expect(page).toHaveURL(/\/hold\//);
  await expect(page.getByRole('rowheader', { name: 'Ayakta' })).toBeVisible();
  await expect(page.getByTestId('hold-total')).toHaveText('1.120,00 ₺');
  await page.getByRole('button', { name: /Ödemeyi başlat/ }).click();

  await expect(orderStatus(page).getByRole('heading')).toHaveText('Ödendi', { timeout: 30_000 });
  await expect(orderStatus(page)).toContainText('3 bilet hazır', { timeout: 30_000 });
  await orderStatus(page).getByRole('link', { name: /Biletleri gör/ }).click();
  const tickets = page.getByTestId('ticket-list').getByRole('listitem').filter({ hasText: created.eventTitle });
  await expect(tickets).toHaveCount(3);
  await tickets.first().getByRole('link', { name: /Bileti aç/ }).click();
  expect(await decodeQr(page.getByTestId('ticket-qr'))).toMatch(/^LT1\./);
});

test('13 · klavye: koltuk listesinden yalnız klavyeyle seçim yapıp tutabilme', async ({ page }) => {
  const organizer = await ApiSession.login(USERS.kumru);
  const created = await createSeatedSession(organizer, { title: 'Klavye Gösterisi', rows: 1, seatsPerRow: 4 });
  await organizer.dispose();

  await login(page, USERS.ayse);
  await page.goto(`/seans/${created.sessionId}`);
  await expect(seatCheckbox(page, 'A', 1)).toBeVisible();

  // Buradan sonra fare kullanılmaz.
  await page.keyboard.press('Tab');
  await expect(page.getByRole('link', { name: 'İçeriğe geç' })).toBeFocused();
  await tabTo(page, seatCheckbox(page, 'A', 1));
  await page.keyboard.press('Space');
  await expect(seatCheckbox(page, 'A', 1)).toBeChecked();
  await page.keyboard.press('Tab');
  await expect(seatCheckbox(page, 'A', 2)).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(seatCheckbox(page, 'A', 3)).toBeFocused();
  await page.keyboard.press('Space');
  await expect(page.getByTestId('selection-count')).toHaveText('2 / 6');
  await expect(page.getByTestId('selection-labels')).toContainText('Salon / A / 3');
  // Harita aynı durumu gösterir.
  await expect(page.locator('[data-seat-label="Salon / A / 3"]')).toHaveClass(/is-sel/);

  const hold = page.getByRole('button', { name: 'Koltukları tut' });
  await tabTo(page, hold);
  // Odak halkası görünür olmalı.
  const outline = await hold.evaluate((element) => getComputedStyle(element).outlineStyle);
  expect(outline).not.toBe('none');
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(/\/hold\//);
  await expect(page.getByRole('rowheader', { name: 'Salon / A / 3' })).toBeVisible();

  // Temizlik de klavyeyle: rezervasyonu bırak.
  const release = page.getByRole('button', { name: 'Rezervasyonu bırak' });
  await tabTo(page, release);
  await page.keyboard.press('Enter');
  await expect(page).toHaveURL(new RegExp(`/seans/${created.sessionId}$`));
});

test('satış durumları: satışta / "… itibarıyla açılır" / tükendi / kapandı keşifte ve seans sayfasında doğru gösterilir', async ({ page }) => {
  const organizer = await ApiSession.login(USERS.kumru);
  const soon = await createSeatedSession(organizer, { title: 'Satışı Açılacak Gösteri', salesStartsInHours: 24 });
  const closed = await createSeatedSession(organizer, { title: 'Satışı Kapanmış Gösteri', salesStartsInHours: -3, salesEndsInHours: -1 });
  const soldOut = await createSeatedSession(organizer, { title: 'Tükenmiş Gösteri', rows: 1, seatsPerRow: 1 });
  await organizer.dispose();
  const mehmet = await ApiSession.login(USERS.mehmet);
  await purchase(mehmet, soldOut.sessionId, { seatIds: [(await seatsOf(mehmet, soldOut.sessionId))[0]!.id] });
  await mehmet.dispose();

  // Satışı henüz başlamamış seans: başlangıç anı seansın saat diliminde.
  await page.goto(`/etkinlik/${soon.eventSlug}`);
  const soonRow = page.getByTestId('session-rows').getByRole('listitem');
  await expect(soonRow).toContainText(/Satış \d{1,2} \S+ \d{4} \S+ \d{2}:\d{2} itibarıyla açılır/);
  await expect(soonRow.getByRole('link', { name: /Koltuk seç/ })).toHaveCount(0);
  await soonRow.getByRole('link', { name: /Ayrıntı/ }).click();
  await expect(page.getByText(/Satış .* itibarıyla açılır/)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Koltukları tut' })).toHaveCount(0);
  await expect(page.getByText('450,00 ₺')).toBeVisible(); // Fiyat listesi görünür.

  // Satışı kapanmış seans.
  await page.goto(`/etkinlik/${closed.eventSlug}`);
  await expect(page.getByTestId('session-rows').getByRole('listitem')).toContainText('Satış kapandı');
  await page.goto(`/seans/${closed.sessionId}`);
  await expect(page.getByText('Satış kapandı')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Koltukları tut' })).toHaveCount(0);

  // Tükenmiş seans.
  await page.goto(`/etkinlik/${soldOut.eventSlug}`);
  await expect(page.getByTestId('session-rows').getByRole('listitem')).toContainText('Tükendi');
  await page.goto(`/seans/${soldOut.sessionId}`);
  await expect(page.getByText('Tükendi', { exact: true })).toBeVisible();
  await expect(page.getByRole('checkbox', { name: 'Koltuk 1, dolu' })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Koltukları tut' })).toHaveCount(0);
});
