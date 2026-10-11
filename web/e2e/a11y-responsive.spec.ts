import { expect, test, type Page } from '@playwright/test';
import { ApiSession, assignGateStaff, createGaSession, createSeatedSession, purchase, seatsOf, USERS, type CreatedSession, type Purchase } from './support/api';
import { expectNoA11yViolations, expectNoHorizontalOverflow, login, newUserPage, orderStatus, seatCheckbox, WIDTHS } from './support/ui';

interface World {
  seated: CreatedSession;
  ga: CreatedSession;
  bought: Purchase;
}

/** Ekranların gerçek veriyle dolu olması için: koltuklu + genel giriş seansı, bir satış, kapı ataması. */
async function buildWorld(): Promise<World> {
  const organizer = await ApiSession.login(USERS.kumru);
  // Başlangıç 2 saat sonra: kapı penceresi açık. Demo simülatörü açık: ödeme ekranında senaryo seçici de taranır.
  const seated = await createSeatedSession(organizer, { title: 'Erişilebilirlik Gösterisi', rows: 3, seatsPerRow: 8, startsInHours: 2, demoSimulator: true });
  const ga = await createGaSession(organizer, { title: 'Erişilebilirlik Konseri' });
  await assignGateStaff(organizer, seated);
  await organizer.dispose();
  const customer = await ApiSession.login(USERS.ayse);
  const seats = await seatsOf(customer, seated.sessionId);
  const bought = await purchase(customer, seated.sessionId, { seatIds: [seats[0]!.id, seats[1]!.id] });
  await customer.dispose();
  return { seated, ga, bought };
}

async function openSeatSelection(page: Page, world: World): Promise<void> {
  await page.goto(`/seans/${world.seated.sessionId}`);
  await seatCheckbox(page, 'B', 3).check();
  await seatCheckbox(page, 'B', 4).check();
  await expect(page.getByTestId('selection-count')).toHaveText('2 / 6');
}

test('14 · erişilebilirlik (axe, WCAG A/AA): keşif, koltuk seçimi, genel giriş, ödeme, sipariş, bilet, organizatör özeti, kapı — ihlal 0', async ({ browser, page }) => {
  test.setTimeout(240_000);
  const world = await buildWorld();

  // Keşif (girişsiz) ve giriş formu (hata durumuyla).
  await page.goto('/');
  await expect(page.getByTestId('session-rows')).toBeVisible();
  await expectNoA11yViolations(page, 'keşif');
  await page.goto('/giris');
  await page.getByRole('button', { name: 'Giriş yap' }).click();
  await expect(page.getByText('E-posta adresini yaz.')).toBeVisible();
  await expectNoA11yViolations(page, 'giriş (hata durumu)');

  // Müşteri ekranları.
  await login(page, USERS.mehmet);
  await openSeatSelection(page, world);
  await expectNoA11yViolations(page, 'koltuk seçimi');
  await page.goto(`/seans/${world.ga.sessionId}`);
  await page.getByRole('button', { name: 'Ayakta: bir artır' }).click();
  await expectNoA11yViolations(page, 'genel giriş');

  await openSeatSelection(page, world);
  await page.getByRole('button', { name: 'Koltukları tut' }).click();
  await expect(page).toHaveURL(/\/hold\//);
  await expect(page.getByRole('timer')).toBeVisible();
  await expect(page.getByRole('group', { name: /Demo ödeme senaryosu/ })).toBeVisible();
  await expectNoA11yViolations(page, 'ödeme');
  await page.getByRole('button', { name: /Ödemeyi başlat/ }).click();
  await expect(orderStatus(page).getByRole('heading')).toHaveText('Ödendi', { timeout: 30_000 });
  await expectNoA11yViolations(page, 'sipariş durumu');
  await page.goto('/siparislerim');
  await expect(page.getByRole('list').getByRole('listitem').first()).toBeVisible();
  await expectNoA11yViolations(page, 'siparişlerim');

  const customer = await newUserPage(browser, USERS.ayse);
  await customer.page.goto('/biletlerim');
  await expect(customer.page.getByTestId('ticket-list')).toBeVisible();
  await expectNoA11yViolations(customer.page, 'biletlerim');
  await customer.page.goto(`/bilet/${world.bought.ticketIds[0]}`);
  await expect(customer.page.getByTestId('ticket-qr')).toBeVisible();
  await expectNoA11yViolations(customer.page, 'bilet');
  await customer.context.close();

  // Organizatör özeti (+ etkinlik listesi ve seans editörü).
  const organizer = await newUserPage(browser, USERS.kumru);
  await organizer.page.goto(`/organizator/${world.seated.organizationId}/seans/${world.seated.sessionId}/ozet`);
  await expect(organizer.page.getByTestId('summary-gross')).toHaveText(/₺/);
  await expect(organizer.page.getByRole('img', { name: /^Kapasite 24 koltuk/ })).toBeVisible();
  await expectNoA11yViolations(organizer.page, 'organizatör özeti');
  await organizer.page.goto(`/organizator/${world.seated.organizationId}/etkinlikler`);
  await expect(organizer.page.getByTestId('org-events')).toBeVisible();
  await expectNoA11yViolations(organizer.page, 'organizatör etkinlikleri');
  await organizer.page.goto(`/organizator/${world.seated.organizationId}/seans/${world.seated.sessionId}`);
  await expect(organizer.page.getByRole('list', { name: 'Seans özellikleri' })).toBeVisible();
  await expectNoA11yViolations(organizer.page, 'organizatör seans editörü');
  await organizer.page.goto(`/organizator/${world.seated.organizationId}/operasyon`);
  await expect(organizer.page.getByRole('heading', { level: 1, name: 'Operasyon kuyruğu' })).toBeVisible();
  await expect(organizer.page.getByRole('status').filter({ hasText: 'Yükleniyor' })).toHaveCount(0);
  await expectNoA11yViolations(organizer.page, 'organizatör operasyon kuyruğu');
  await organizer.context.close();

  // Kapı: seans listesi, hazır durumu, sonuç panelleri (koyu tema).
  const gate = await newUserPage(browser, USERS.gate, { viewport: { width: 360, height: 780 } });
  await gate.page.goto('/kapi');
  await expect(gate.page.getByTestId('gate-sessions')).toBeVisible();
  await expectNoA11yViolations(gate.page, 'kapı seans listesi');
  await gate.page.goto(`/kapi/${world.seated.sessionId}`);
  await expect(gate.page.getByTestId('gate-result')).toHaveAttribute('data-r', 'idle');
  await expect(gate.page.getByTestId('camera-status')).toHaveText('Kamera kapalı');
  await expectNoA11yViolations(gate.page, 'kapı (hazır)');
  await gate.page.getByLabel('Elle kod girişi').fill(`LT1.${'B'.repeat(43)}`);
  await gate.page.getByRole('button', { name: 'Kontrol et' }).click();
  await expect(gate.page.getByTestId('gate-result')).toHaveAttribute('data-r', 'invalid');
  await expectNoA11yViolations(gate.page, 'kapı (geçersiz)');

  const token = await (async () => {
    const owner = await newUserPage(browser, USERS.ayse);
    await owner.page.goto(`/bilet/${world.bought.ticketIds[1]}`);
    await owner.page.getByRole('button', { name: 'Kodu metin olarak göster' }).click();
    const value = (await owner.page.getByTestId('ticket-token').textContent()) ?? '';
    await owner.context.close();
    return value;
  })();
  await gate.page.getByRole('button', { name: 'Sıradaki bilet' }).click();
  await gate.page.getByLabel('Elle kod girişi').fill(token);
  await gate.page.getByRole('button', { name: 'Kontrol et' }).click();
  await expect(gate.page.getByTestId('gate-result')).toHaveAttribute('data-r', 'accepted');
  await expectNoA11yViolations(gate.page, 'kapı (kabul)');
  await gate.page.getByRole('button', { name: 'Sıradaki bilet' }).click();
  await gate.page.getByLabel('Elle kod girişi').fill(token);
  await gate.page.getByRole('button', { name: 'Kontrol et' }).click();
  await expect(gate.page.getByTestId('gate-result')).toHaveAttribute('data-r', 'used');
  await expectNoA11yViolations(gate.page, 'kapı (zaten kullanıldı)');
  await gate.context.close();
});

test('15 · duyarlılık: müşteri, organizatör ve kapı ekranlarında 360 / 768 / 1440 genişlikte yatay taşma yok', async ({ browser }) => {
  test.setTimeout(240_000);
  const world = await buildWorld();

  const customer = await newUserPage(browser, USERS.ayse);
  const organizer = await newUserPage(browser, USERS.kumru);
  const gate = await newUserPage(browser, USERS.gate);
  try {
    for (const width of WIDTHS) {
      const size = { width, height: width === 360 ? 780 : 900 };

      // Müşteri: keşif, koltuk seçimi, genel giriş, bilet.
      await customer.page.setViewportSize(size);
      await customer.page.goto('/');
      await expect(customer.page.getByTestId('session-rows')).toBeVisible();
      await expectNoHorizontalOverflow(customer.page, 'musteri-kesif', width);
      await openSeatSelection(customer.page, world);
      await expectNoHorizontalOverflow(customer.page, 'musteri-koltuk-secimi', width);
      await customer.page.goto(`/seans/${world.ga.sessionId}`);
      await customer.page.getByRole('button', { name: 'Ayakta: bir artır' }).click();
      await expectNoHorizontalOverflow(customer.page, 'musteri-genel-giris', width);
      await customer.page.goto(`/bilet/${world.bought.ticketIds[0]}`);
      await expect(customer.page.getByTestId('ticket-qr')).toBeVisible();
      await expectNoHorizontalOverflow(customer.page, 'musteri-bilet', width);
      await customer.page.goto('/biletlerim');
      await expect(customer.page.getByTestId('ticket-list')).toBeVisible();
      await expectNoHorizontalOverflow(customer.page, 'musteri-biletlerim', width);
      // Ödeme: rezervasyon oluşturulur, ekran ölçülür, sonra bırakılır.
      await openSeatSelection(customer.page, world);
      await customer.page.getByRole('button', { name: 'Koltukları tut' }).click();
      await expect(customer.page.getByRole('timer')).toBeVisible();
      await expect(customer.page.getByRole('group', { name: /Demo ödeme senaryosu/ })).toBeVisible();
      await expectNoHorizontalOverflow(customer.page, 'musteri-odeme', width);
      await customer.page.getByRole('button', { name: 'Rezervasyonu bırak' }).click();
      await expect(customer.page).toHaveURL(new RegExp(`/seans/${world.seated.sessionId}$`));
      await customer.page.goto(`/siparis/${world.bought.orderId}`);
      await expect(orderStatus(customer.page)).toBeVisible();
      await expectNoHorizontalOverflow(customer.page, 'musteri-siparis', width);

      // Organizatör: seans özeti (+ etkinlik listesi).
      await organizer.page.setViewportSize(size);
      await organizer.page.goto(`/organizator/${world.seated.organizationId}/seans/${world.seated.sessionId}/ozet`);
      await expect(organizer.page.getByTestId('summary-gross')).toHaveText(/₺/);
      await expectNoHorizontalOverflow(organizer.page, 'organizator-ozet', width);
      await organizer.page.goto(`/organizator/${world.seated.organizationId}/etkinlikler`);
      await expect(organizer.page.getByTestId('org-events')).toBeVisible();
      await expectNoHorizontalOverflow(organizer.page, 'organizator-etkinlikler', width);

      // Kapı: seans listesi ve tarama ekranı.
      await gate.page.setViewportSize(size);
      await gate.page.goto('/kapi');
      await expect(gate.page.getByTestId('gate-sessions')).toBeVisible();
      await expectNoHorizontalOverflow(gate.page, 'kapi-seanslar', width);
      await gate.page.goto(`/kapi/${world.seated.sessionId}`);
      await expect(gate.page.getByTestId('gate-result')).toBeVisible();
      await expect(gate.page.getByTestId('camera-status')).toHaveText('Kamera kapalı');
      await expectNoHorizontalOverflow(gate.page, 'kapi-tarama', width);
      await gate.page.getByLabel('Elle kod girişi').fill(`LT1.${'D'.repeat(43)}`);
      await gate.page.getByRole('button', { name: 'Kontrol et' }).click();
      await expect(gate.page.getByTestId('gate-result')).toHaveAttribute('data-r', 'invalid');
      await expectNoHorizontalOverflow(gate.page, 'kapi-sonuc-gecersiz', width);
    }

    // Dokunma hedefleri: birincil eylemler en az 44px.
    await customer.page.setViewportSize({ width: 360, height: 780 });
    await openSeatSelection(customer.page, world);
    for (const target of [customer.page.getByRole('button', { name: 'Koltukları tut' }), seatCheckbox(customer.page, 'A', 3), customer.page.getByRole('button', { name: 'Çıkış yap' })]) {
      const box = await target.boundingBox();
      expect(box, 'hedef görünür olmalı').not.toBeNull();
      expect(Math.round(box!.height)).toBeGreaterThanOrEqual(44);
      expect(Math.round(box!.width)).toBeGreaterThanOrEqual(44);
    }
  } finally {
    await customer.context.close();
    await organizer.context.close();
    await gate.context.close();
  }
});
