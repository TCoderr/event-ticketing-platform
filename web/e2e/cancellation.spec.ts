import { expect, test } from '@playwright/test';
import { ApiSession, createSeatedSession, purchase, seatsOf, USERS } from './support/api';
import { login, newUserPage, orderStatus, seatCheckbox } from './support/ui';

test('9 · müşteri iptali → iade → bilet geçersiz', async ({ page }) => {
  const organizer = await ApiSession.login(USERS.kumru);
  // İptal sınırı açık: başlangıç 3 gün sonra (sınır: başlangıçtan 24 saat önce).
  const created = await createSeatedSession(organizer, { title: 'İptal Edilecek Gösteri', startsInHours: 72 });
  await organizer.dispose();
  const customer = await ApiSession.login(USERS.ayse);
  const seats = await seatsOf(customer, created.sessionId);
  const bought = await purchase(customer, created.sessionId, { seatIds: [seats[0]!.id, seats[1]!.id] });
  await customer.dispose();

  await login(page, USERS.ayse);
  await page.goto(`/siparis/${bought.orderId}`);
  await expect(orderStatus(page).getByRole('heading')).toHaveText('Ödendi');
  await expect(orderStatus(page)).toContainText('anına kadar'); // cancelDeadline gösterilir.

  // Onay adımı: ilk tıklama iptal etmez.
  await page.getByRole('button', { name: 'Siparişi iptal et' }).click();
  const confirm = page.getByRole('group', { name: 'Sipariş iptal edilsin mi?' });
  await expect(confirm).toContainText('Son iptal zamanı:');
  await expect(confirm).toContainText('2 bilet hemen geçersiz olur');
  await confirm.getByRole('button', { name: 'Vazgeç' }).click();
  await expect(confirm).toHaveCount(0);
  await expect(orderStatus(page).getByRole('heading')).toHaveText('Ödendi');

  await page.getByRole('button', { name: 'Siparişi iptal et' }).click();
  await page.getByRole('button', { name: 'Evet, siparişi iptal et' }).click();
  const heading = orderStatus(page).getByRole('heading');
  await expect(heading).toHaveText(/^İptal edildi · iade (bekliyor|tamamlandı)$/, { timeout: 20_000 });
  await expect(orderStatus(page)).toContainText('biletler artık geçersiz');
  // İade asenkron tamamlanır; ekran kendiliğinden güncellenir.
  await expect(heading).toHaveText('İptal edildi · iade tamamlandı', { timeout: 90_000 });
  await expect(orderStatus(page)).toContainText('900,00 ₺ ödeme yöntemine iade edildi.');
  await expect(page.getByRole('button', { name: 'Siparişi iptal et' })).toHaveCount(0);

  // Bilet geçersiz: QR gösterilmez.
  await page.goto(`/bilet/${bought.ticketIds[0]}`);
  await expect(page.getByTestId('ticket-status')).toContainText('İptal · geçersiz');
  await expect(page.getByTestId('ticket-qr')).toHaveCount(0);
  await expect(page.getByTestId('ticket-no-qr')).toContainText('giriş kodu yok');
  await page.goto('/biletlerim');
  const rows = page.getByTestId('ticket-list').getByRole('listitem').filter({ hasText: created.eventTitle });
  await expect(rows).toHaveCount(2);
  await expect(rows.first()).toContainText('İptal · geçersiz');

  // Envanter yeniden satışta.
  await page.goto(`/seans/${created.sessionId}`);
  await expect(seatCheckbox(page, 'A', 1)).toBeEnabled();
});

test('10 · seans iptali: organizatör iptal eder → keşiften kalkar, sipariş "İptal edildi", bilet geçersiz', async ({ browser, page }) => {
  const organizer = await ApiSession.login(USERS.kumru);
  const created = await createSeatedSession(organizer, { title: 'İptal Edilecek Seans' });
  await organizer.dispose();
  const customer = await ApiSession.login(USERS.mehmet);
  const seats = await seatsOf(customer, created.sessionId);
  const bought = await purchase(customer, created.sessionId, { seatIds: [seats[0]!.id] });
  await customer.dispose();

  // Önce: seans keşifte.
  await page.goto(`/etkinlik/${created.eventSlug}`);
  await expect(page.getByTestId('session-rows').getByRole('listitem')).toHaveCount(1);

  // Organizatör arayüzden iptal eder (onaylı).
  const org = await newUserPage(browser, USERS.kumru);
  try {
    await org.page.goto(`/organizator/${created.organizationId}/seans/${created.sessionId}`);
    await expect(org.page.getByRole('list', { name: 'Seans özellikleri' })).toContainText('Yayında');
    await org.page.getByRole('button', { name: 'Seansı iptal et' }).click();
    await expect(org.page.getByRole('group', { name: 'Seans iptal edilsin mi?' })).toBeVisible();
    await org.page.getByRole('button', { name: 'Evet, seansı iptal et' }).click();
    await expect(org.page.getByRole('list', { name: 'Seans özellikleri' })).toContainText('İptal edildi', { timeout: 20_000 });
    await expect(org.page.getByRole('button', { name: 'Seansı iptal et' })).toHaveCount(0);
  } finally {
    await org.context.close();
  }

  // Keşiften kalkar.
  await page.goto(`/etkinlik/${created.eventSlug}`);
  await expect(page.getByText('Bu etkinliğin yaklaşan seansı yok.')).toBeVisible();
  await page.goto('/');
  await expect(page.getByRole('heading', { level: 1, name: 'Yaklaşan seanslar' })).toBeVisible();
  const more = page.getByRole('button', { name: 'Daha fazla seans göster' });
  for (let i = 0; i < 20 && (await more.isVisible().catch(() => false)); i++) await more.click();
  await expect(page.getByText(created.eventTitle)).toHaveCount(0);
  await page.goto(`/seans/${created.sessionId}`);
  await expect(page.getByText('Bulunamadı')).toBeVisible();

  // Müşterinin siparişi iptal, bileti geçersiz.
  await login(page, USERS.mehmet);
  await page.goto(`/siparis/${bought.orderId}`);
  const heading = orderStatus(page).getByRole('heading');
  await expect(heading).toHaveText(/^İptal edildi/, { timeout: 60_000 });
  await expect(orderStatus(page)).toContainText('Seans organizatör tarafından iptal edildi');
  await expect(heading).toHaveText('İptal edildi · iade tamamlandı', { timeout: 90_000 });
  await page.goto(`/bilet/${bought.ticketIds[0]}`);
  await expect(page.getByTestId('ticket-status')).toContainText('İptal · geçersiz');
  await expect(page.getByTestId('ticket-qr')).toHaveCount(0);
});
