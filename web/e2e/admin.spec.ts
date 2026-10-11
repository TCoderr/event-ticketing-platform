import { expect, test } from '@playwright/test';
import { ApiSession, createSeatedSession, idempotencyKey, pollOrder, seatsOf, uniqueSuffix, USERS, type OrderInfo } from './support/api';
import { login, newUserPage, orderStatus } from './support/ui';

test('kayıt → organizatör başvurusu → platform yöneticisi onaylar; müşteri Yönetim\'e erişemez', async ({ browser, page }) => {
  const suffix = uniqueSuffix();
  const email = `e2e-${suffix}@loca.test`;
  const password = `e2e-Parola-${suffix}-7`; // Yalnız bu koşuda üretilen sentetik hesap.
  const organization = `E2E Organizasyonu ${suffix}`;

  // Kayıt: zayıf parola alan hatası verir (alanla ilişkili, role="alert").
  await page.goto('/kayit');
  await page.getByLabel('Ad', { exact: true }).fill('E2E Başvuran');
  await page.getByLabel('E-posta').fill(email);
  await page.getByLabel('Parola').fill('kisa');
  await page.getByRole('button', { name: 'Kayıt ol' }).click();
  const passwordInput = page.getByLabel('Parola');
  await expect(passwordInput).toHaveAttribute('aria-invalid', 'true');
  const describedBy = (await passwordInput.getAttribute('aria-describedby')) ?? '';
  const errorId = describedBy.split(' ').find((id) => id.endsWith('-err'));
  expect(errorId, 'hata metni alana aria-describedby ile bağlı olmalı').toBeTruthy();
  await expect(page.locator(`[id="${errorId}"]`)).toHaveAttribute('role', 'alert');
  await expect(page.locator(`[id="${errorId}"]`)).toContainText('en az 10 karakter');

  await passwordInput.fill(password);
  await page.getByRole('button', { name: 'Kayıt ol' }).click();
  await expect(page.getByRole('button', { name: 'Çıkış yap' })).toBeVisible();
  // Yeni müşteri: Organizatör / Kapı / Yönetim bağlantıları yok.
  const nav = page.getByRole('navigation', { name: 'Ana gezinme' });
  await expect(nav.getByRole('link')).toHaveText(['Etkinlikler', 'Biletlerim', 'Siparişlerim']);

  // Başvuru.
  await page.getByRole('link', { name: 'Organizatör başvurusu' }).click();
  await expect(page.getByRole('heading', { level: 1, name: 'Organizatör başvurusu' })).toBeVisible();
  await page.getByLabel('Organizasyon adı').fill(organization);
  await page.getByRole('button', { name: 'Başvuruyu gönder' }).click();
  await expect(page).toHaveURL(/\/organizator\/[0-9a-f-]{36}\/etkinlikler$/);
  await expect(page.getByText('Onay bekliyor').first()).toBeVisible();
  await expect(nav.getByRole('link', { name: 'Organizatör' })).toBeVisible();

  // Platform yöneticisi onaylar.
  const admin = await newUserPage(browser, USERS.admin);
  try {
    await admin.page.getByRole('navigation', { name: 'Ana gezinme' }).getByRole('link', { name: 'Yönetim' }).click();
    await expect(admin.page).toHaveURL(/\/yonetim\/organizasyonlar$/);
    const row = admin.page.getByTestId('admin-organizations').getByRole('listitem').filter({ hasText: organization });
    await expect(row).toContainText('Onay bekliyor');
    await row.getByRole('button', { name: /Onayla/ }).click();
    await expect(row).toContainText('Aktif');
    await expect(row.getByRole('button', { name: /Onayla/ })).toHaveCount(0);

    // Diğer sekmeler gerçek API'den yüklenir.
    const tabs = admin.page.getByRole('navigation', { name: 'Yönetim bölümleri' });
    await tabs.getByRole('link', { name: /Etkinlikler/ }).click();
    await expect(admin.page.getByTestId('admin-events')).toBeVisible();
    await tabs.getByRole('link', { name: /Operasyon/ }).click();
    await expect(admin.page.getByTestId('ops-counts')).toBeVisible();
    await tabs.getByRole('link', { name: /Dead-letter/i }).click();
    await expect(admin.page.getByText('Dead-letter kuyruğu boş.').or(admin.page.getByTestId('admin-dead-letters'))).toBeVisible();
  } finally {
    await admin.context.close();
  }

  // Başvuran için uyarı kalkar.
  await page.reload();
  await expect(page.getByRole('heading', { level: 1, name: 'Etkinlikler' })).toBeVisible();
  await expect(page.getByText('Onay bekliyor')).toHaveCount(0);

  // Müşteri yönetim ekranına erişemez (sunucu da 403 verir).
  const customer = await newUserPage(browser, USERS.ayse);
  await customer.page.goto('/yonetim');
  await expect(customer.page.getByText('Erişim yok')).toBeVisible();
  expect((await customer.page.request.get('/api/admin/organizations')).status()).toBe(403);
  await customer.context.close();
});

test('Demo PSP paneli: bekleyen tahsilat "Demo" etiketiyle listelenir; yönetici reddedince sipariş "Reddedildi" olur', async ({ browser, page }) => {
  test.setTimeout(180_000);
  const kumru = await ApiSession.login(USERS.kumru);
  const created = await createSeatedSession(kumru, { title: 'Demo PSP Gösterisi', demoSimulator: true });
  await kumru.dispose();

  // Geç başarı senaryosu: tahsilat sağlayıcıda 25 sn bekler (E2E ayarı); bu sürede panelde görünür.
  const mehmet = await ApiSession.login(USERS.mehmet);
  const hold = await mehmet.post<{ id: string }>(`/api/sessions/${created.sessionId}/holds`,
    { seatIds: [(await seatsOf(mehmet, created.sessionId))[0]!.id] }, { expectStatus: 201, idempotencyKey: idempotencyKey() });
  const order = await mehmet.post<OrderInfo>(`/api/holds/${hold.id}/checkout`, { paymentMethod: 'tok_late_success' }, { expectStatus: 202, idempotencyKey: idempotencyKey() });
  const reference = `pay_${order.id.replace(/-/g, '')}`;

  await login(page, USERS.admin);
  await page.goto('/yonetim/demo-psp');
  await expect(page.getByRole('heading', { level: 2, name: /Demo ödeme sağlayıcısı paneli/ })).toContainText('Demo');
  const row = page.getByTestId('demo-psp-charges').getByRole('row').filter({ hasText: reference });
  await expect(row).toBeVisible({ timeout: 20_000 });
  await expect(row).toContainText('450,00 ₺');
  await expect(row).toContainText('pending');
  await row.getByRole('button', { name: /Reddedildi olarak sonuçlandır/ }).click();
  await expect(page.getByText(/Simülatör tahsilatı “declined” olarak işaretledi/)).toBeVisible();
  await expect(row).toHaveCount(0, { timeout: 20_000 });

  // Müşteri gerçek sonucu görür.
  const declined = await pollOrder(mehmet, order.id, (o) => o.status === 'Failed', 60_000);
  expect(declined.payment.failureKind).toBe('Declined');
  await mehmet.dispose();
  const customer = await newUserPage(browser, USERS.mehmet);
  await customer.page.goto(`/siparis/${order.id}`);
  await expect(orderStatus(customer.page).getByRole('heading')).toHaveText('Reddedildi');
  await customer.context.close();
});
