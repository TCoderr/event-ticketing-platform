import { mkdirSync } from 'node:fs';
import path from 'node:path';
import AxeBuilder from '@axe-core/playwright';
import { expect, type Browser, type BrowserContext, type Locator, type Page } from '@playwright/test';
import jsQRModule from 'jsqr';
import pngjs from 'pngjs';
import { passwordOf } from './api';

type JsQr = (data: Uint8ClampedArray, width: number, height: number) => { data: string } | null;
// jsqr CommonJS paketidir; ESM'den içe aktarıldığında varsayılan dışa aktarım iki biçimde gelebilir.
const jsQR: JsQr = (jsQRModule as unknown as { default?: JsQr }).default ?? (jsQRModule as unknown as JsQr);

/** Giriş ekranından gerçek kullanıcı girişi. */
export async function login(page: Page, email: string): Promise<void> {
  await page.goto('/giris');
  await submitLogin(page, email);
  await expect(page.getByRole('button', { name: 'Çıkış yap' })).toBeVisible();
}

/** Zaten giriş ekranındayken formu doldurup gönderir (yönlendirmeli akışlar için). */
export async function submitLogin(page: Page, email: string): Promise<void> {
  await expect(page.getByRole('heading', { level: 1, name: 'Giriş yap' })).toBeVisible();
  await page.getByLabel('E-posta').fill(email);
  await page.getByLabel('Parola').fill(passwordOf(email));
  await page.getByRole('button', { name: 'Giriş yap' }).click();
}

export async function newUserPage(browser: Browser, email: string, options: Parameters<Browser['newContext']>[0] = {}): Promise<{ context: BrowserContext; page: Page }> {
  const context = await browser.newContext(options);
  const page = await context.newPage();
  await login(page, email);
  return { context, page };
}

/** Ekrandaki QR'ın ekran görüntüsünü gerçek bir çözücüyle (pngjs + jsQR) okur. */
export async function decodeQr(locator: Locator): Promise<string> {
  const png = pngjs.PNG.sync.read(await locator.screenshot());
  const decoded = jsQR(new Uint8ClampedArray(png.data.buffer, png.data.byteOffset, png.data.byteLength), png.width, png.height);
  expect(decoded, 'QR görüntüsü çözülebilmeli').not.toBeNull();
  return decoded!.data;
}

const WCAG_TAGS = ['wcag2a', 'wcag2aa', 'wcag21a', 'wcag21aa', 'wcag22aa'];

/** axe ile WCAG A/AA taraması: ihlal 0 olmalı. */
export async function expectNoA11yViolations(page: Page, screen: string): Promise<void> {
  const results = await new AxeBuilder({ page: page as never }).withTags(WCAG_TAGS).analyze();
  const summary = results.violations.map((v) => `${v.id} (${v.impact}): ${v.help} → ${v.nodes.map((n) => n.target.join(' ')).slice(0, 5).join(' | ')}`);
  expect(summary, `${screen}: erişilebilirlik ihlalleri`).toEqual([]);
}

export const WIDTHS = [360, 768, 1440] as const;

export function screenshotDir(): string {
  const dir = path.resolve(process.cwd(), '..', 'artifacts', 'verification', 'screenshots');
  mkdirSync(dir, { recursive: true });
  return dir;
}

/** Yatay taşma denetimi + ekran görüntüsü (`<ekran>-<genişlik>.png`). */
export async function expectNoHorizontalOverflow(page: Page, screen: string, width: number): Promise<void> {
  await page.evaluate(() => document.fonts.ready);
  const size = await page.evaluate(() => ({ scroll: document.documentElement.scrollWidth, inner: window.innerWidth }));
  // Kayıtlı görüntüde bilet token'ı (QR ya da metin) okunamaz: taşıyıcı kod dosyaya yazılmaz.
  await page.screenshot({
    path: path.join(screenshotDir(), `${screen}-${width}.png`), fullPage: true,
    mask: [page.getByTestId('ticket-qr'), page.getByTestId('ticket-token')], maskColor: '#5A544A',
  });
  expect(size.scroll, `${screen} @ ${width}px: yatay taşma olmamalı (scrollWidth ${size.scroll}, innerWidth ${size.inner})`).toBeLessThanOrEqual(size.inner);
}

/** Koltuk listesindeki onay kutusu: "Salon · A sırası" grubunda "Koltuk N". */
export function seatCheckbox(page: Page, row: string, number: number): Locator {
  return page.getByRole('group', { name: new RegExp(`· ${row} sırası`) }).getByRole('checkbox', { name: new RegExp(`^Koltuk ${number}(,|$)`) });
}

export const orderStatus = (page: Page) => page.getByTestId('order-status');
