import { expect, test } from '@playwright/test';
import { ApiSession, createSeatedSession, idempotencyKey, pollOrder, purchase, seatsOf, USERS, type OrderInfo } from './support/api';
import { login, newUserPage, orderStatus } from './support/ui';

test('3 · organizatör yalnız kendi etkinlik/seans/satışını görür; başkasının seans adresi "Bulunamadı"', async ({ page }) => {
  const kumru = await ApiSession.login(USERS.kumru);
  const marti = await ApiSession.login(USERS.marti);
  const mine = await createSeatedSession(kumru, { title: 'Kumru Gösterisi', priceMinor: 45000 });
  const theirs = await createSeatedSession(marti, { title: 'Martı Gösterisi', priceMinor: 70000 });
  expect(mine.organizationId).not.toBe(theirs.organizationId);

  // İki organizasyonda da birer satış var.
  const ayse = await ApiSession.login(USERS.ayse);
  await purchase(ayse, mine.sessionId, { seatIds: [(await seatsOf(ayse, mine.sessionId))[0]!.id] });
  await purchase(ayse, theirs.sessionId, { seatIds: (await seatsOf(ayse, theirs.sessionId)).slice(0, 2).map((s) => s.id) });
  await Promise.all([kumru.dispose(), marti.dispose(), ayse.dispose()]);

  await login(page, USERS.kumru);
  await page.getByRole('navigation', { name: 'Ana gezinme' }).getByRole('link', { name: 'Organizatör' }).click();

  // Kendi etkinlik listesi: kendi etkinliği var, diğer organizatörünki yok.
  await expect(page).toHaveURL(new RegExp(`/organizator/${mine.organizationId}/etkinlikler$`));
  await expect(page.getByRole('heading', { level: 1, name: 'Etkinlikler' })).toBeVisible();
  const events = page.getByTestId('org-events');
  await expect(events).toContainText(mine.eventTitle);
  await expect(events).not.toContainText(theirs.eventTitle);
  await expect(events).not.toContainText('Martı');

  // Kendi seansının özeti: yalnız kendi satışı (1 bilet, 450,00 ₺).
  await page.goto(`/organizator/${mine.organizationId}/seans/${mine.sessionId}/ozet`);
  await expect(page.getByRole('heading', { level: 1, name: 'Seans özeti' })).toBeVisible();
  await expect(page.getByText(mine.eventTitle).first()).toBeVisible();
  await expect(page.getByTestId('summary-gross')).toHaveText('450,00 ₺');
  await expect(page.getByTestId('summary-net')).toHaveText('450,00 ₺');
  await expect(page.getByTestId('summary-refunded')).toHaveText('0,00 ₺');
  await expect(page.getByTestId('summary-tickets-sold')).toHaveText('1');
  await expect(page.getByRole('img', { name: 'Kapasite 10 koltuk: 1 satıldı, 0 tutuluyor, 9 boş' })).toBeVisible();

  // Başka organizatörün seansı: editör ve özet adresleri erişilemez (bulunamadı), veri sızmaz.
  for (const path of [
    `/organizator/${mine.organizationId}/seans/${theirs.sessionId}`,
    `/organizator/${mine.organizationId}/seans/${theirs.sessionId}/ozet`,
    `/organizator/${mine.organizationId}/etkinlik/${theirs.eventId}`,
    `/organizator/${theirs.organizationId}/etkinlikler`,
    `/organizator/${theirs.organizationId}/seans/${theirs.sessionId}/ozet`,
  ]) {
    await page.goto(path);
    await expect(page.getByText('Bulunamadı').first(), path).toBeVisible();
    await expect(page.getByText(theirs.eventTitle), path).toHaveCount(0);
    await expect(page.getByText('1.400,00 ₺'), path).toHaveCount(0);
  }

  // Sunucu da aynı kararı verir (arayüz kontrolü yetki kanıtı değildir).
  for (const url of [
    `/api/organizer/sessions/${theirs.sessionId}`,
    `/api/organizer/sessions/${theirs.sessionId}/summary`,
    `/api/organizer/events/${theirs.eventId}`,
    `/api/organizer/organizations/${theirs.organizationId}/events`,
    `/api/organizer/organizations/${theirs.organizationId}/operations`,
  ]) {
    expect((await page.request.get(url)).status(), url).toBe(404);
  }
});

test('organizatör arayüzden etkinlik → seans kurar, yayınlar ve seans keşifte görünür', async ({ page }) => {
  const kumru = await ApiSession.login(USERS.kumru);
  const base = await createSeatedSession(kumru, { title: 'Hazır Mekan Kaynağı', publish: false });
  await kumru.dispose();
  const title = `Arayüzden Kurulan Etkinlik ${Date.now().toString(36)}`;

  await login(page, USERS.kumru);
  await page.goto(`/organizator/${base.organizationId}/etkinlikler`);

  // Yeni etkinlik.
  await page.getByLabel('Başlık', { exact: true }).fill(title);
  await page.getByLabel('Açıklama').fill('Arayüz üzerinden oluşturulan sentetik etkinlik açıklaması.');
  await page.getByLabel('Kategori', { exact: true }).selectOption('Concert');
  await page.getByRole('button', { name: 'Etkinliği oluştur' }).click();
  await expect(page).toHaveURL(/\/organizator\/[0-9a-f-]{36}\/etkinlik\/[0-9a-f-]{36}$/);
  await expect(page.getByRole('heading', { level: 1 })).toContainText(title);
  await page.getByRole('button', { name: 'Etkinliği yayınla' }).click();
  await expect(page.getByRole('list', { name: 'Etkinlik özellikleri' })).toContainText('Yayında');

  // Yeni genel giriş seansı: yerel başlangıç saati 5 gün sonra.
  const start = new Date(Date.now() + 5 * 24 * 3600 * 1000);
  const local = `${start.getFullYear()}-${String(start.getMonth() + 1).padStart(2, '0')}-${String(start.getDate()).padStart(2, '0')}T20:30`;
  await page.getByLabel('Mekan', { exact: true }).selectOption(base.venueId);
  await page.getByLabel('Başlangıç (mekanın yerel saati)').fill(local);
  await page.getByLabel('Satış modu').selectOption('GeneralAdmission');
  await page.getByRole('button', { name: 'Taslak seans oluştur' }).click();
  await expect(page).toHaveURL(/\/seans\/[0-9a-f-]{36}$/);
  const sessionId = page.url().split('/').pop()!;

  // Kategori ekle ve yayınla.
  await page.getByLabel('Kategori adı').fill('Ayakta');
  await page.getByLabel('Fiyat (TL)').fill('275,50');
  await page.getByLabel('Kapasite', { exact: true }).fill('25');
  await page.getByRole('button', { name: 'Kategori ekle' }).click();
  await expect(page.getByText('Kategori eklendi.')).toBeVisible();
  await expect(page.getByText('Ayakta · 0 satıldı · 0 tutuluyor · 25 boş')).toBeVisible();
  await page.getByRole('button', { name: 'Yayınla', exact: true }).click();
  await expect(page.getByRole('list', { name: 'Seans özellikleri' })).toContainText('Yayında');

  // Public seans sayfası: fiyat ve saat gerçek API'den.
  await page.goto(`/seans/${sessionId}`);
  await expect(page.getByRole('heading', { level: 1 })).toHaveText(title);
  await expect(page.getByText('275,50 ₺')).toBeVisible();
  await expect(page.getByText(/20:30 · Europe\/Istanbul/)).toBeVisible();
});

test('operasyon kuyruğu: başarısız iade yeniden denenir; sonuç planlandı / denendi / başarısız olarak ayrı gösterilir ve "başarılı" sayılmaz', async ({ browser, page }) => {
  test.setTimeout(240_000);
  const kumru = await ApiSession.login(USERS.kumru);
  const created = await createSeatedSession(kumru, { title: 'İadesi Başarısız Gösteri', demoSimulator: true });
  await kumru.dispose();

  // Ön koşul (API): iadesi sağlayıcıda reddedilecek bir ödeme, ardından müşteri iptali.
  const ayse = await ApiSession.login(USERS.ayse);
  const bought = await purchase(ayse, created.sessionId, { seatIds: [(await seatsOf(ayse, created.sessionId))[0]!.id] }, 'tok_refund_fail');
  await ayse.post(`/api/orders/${bought.orderId}/cancel`, undefined, { expectStatus: 200, idempotencyKey: idempotencyKey() });
  await pollOrder(ayse, bought.orderId, (o) => o.refund?.status === 'Failed', 120_000);
  await ayse.dispose();

  await login(page, USERS.kumru);
  await page.goto(`/organizator/${created.organizationId}/operasyon`);
  await expect(page.getByRole('heading', { level: 1, name: 'Operasyon kuyruğu' })).toBeVisible();
  const row = page.getByRole('row').filter({ hasText: bought.orderNumber });
  await expect(row).toContainText('Başarısız iade');
  await expect(row).toContainText('450,00 ₺');
  const succeededBefore = Number(await page.getByTestId('runs-succeeded').textContent());
  const failedBefore = Number(await page.getByTestId('runs-failed').textContent());

  await row.getByRole('button', { name: 'İadeyi yeniden dene' }).click();
  const last = page.getByTestId('ops-last-action');
  await expect(last).toContainText(bought.orderNumber, { timeout: 30_000 });
  // Adımlar ayrı: planlandı ✓, denendi ✓, sonuç başarısız. Başarısız deneme "başarılı" görünmez.
  await expect(last).toContainText('planlandı');
  await expect(last).toContainText('denendi');
  await expect(last).toContainText('Sonuç: başarısız.');
  await expect(last).not.toContainText('başarılı');
  await expect(page.getByTestId('runs-failed')).toHaveText(String(failedBefore + 1), { timeout: 20_000 });
  await expect(page.getByTestId('runs-succeeded')).toHaveText(String(succeededBefore));
  // Kayıt kuyrukta kalır (iade hâlâ başarısız).
  await expect(row).toContainText('Başarısız iade');

  // Müşteri tarafı aynı gerçeği görür.
  const customer = await newUserPage(browser, USERS.ayse);
  await customer.page.goto(`/siparis/${bought.orderId}`);
  await expect(orderStatus(customer.page).getByRole('heading')).toHaveText('İptal edildi · iade başarısız');
  await expect(orderStatus(customer.page)).toContainText('henüz geri ödenmedi');
  await customer.context.close();
});

test('operasyon kuyruğu: belirsiz ödeme için mutabakat gerçek sonucu gösterir ve sipariş "Ödendi" olur', async ({ page }) => {
  test.setTimeout(240_000);
  const kumru = await ApiSession.login(USERS.kumru);
  const created = await createSeatedSession(kumru, { title: 'Mutabakat Gösterisi', demoSimulator: true });
  await kumru.dispose();

  const mehmet = await ApiSession.login(USERS.mehmet);
  const hold = await mehmet.post<{ id: string }>(`/api/sessions/${created.sessionId}/holds`,
    { seatIds: [(await seatsOf(mehmet, created.sessionId))[0]!.id] }, { expectStatus: 201, idempotencyKey: idempotencyKey() });
  const order = await mehmet.post<OrderInfo>(`/api/holds/${hold.id}/checkout`, { paymentMethod: 'tok_timeout_success' }, { expectStatus: 202, idempotencyKey: idempotencyKey() });
  await pollOrder(mehmet, order.id, (o) => o.payment.status === 'Unknown', 60_000);

  await login(page, USERS.kumru);
  await page.goto(`/organizator/${created.organizationId}/operasyon`);
  const row = page.getByRole('row').filter({ hasText: order.orderNumber });
  await expect(row).toContainText('Belirsiz ödeme');
  const succeededBefore = Number(await page.getByTestId('runs-succeeded').textContent());
  await row.getByRole('button', { name: 'Mutabakat yap' }).click();

  const last = page.getByTestId('ops-last-action');
  await expect(last).toContainText(order.orderNumber, { timeout: 30_000 });
  await expect(last).toContainText('planlandı');
  await expect(last).toContainText('denendi');
  await expect(last).toContainText('Sonuç: başarılı.');
  await expect(page.getByTestId('runs-succeeded')).toHaveText(String(succeededBefore + 1), { timeout: 20_000 });
  // Çözülen kayıt kuyruktan düşer.
  await expect(row).toHaveCount(0, { timeout: 20_000 });

  const paid = await pollOrder(mehmet, order.id, (o) => o.status === 'Paid', 30_000);
  expect(paid.payment.status).toBe('Succeeded');
  await mehmet.dispose();
});
