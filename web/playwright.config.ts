import { defineConfig, devices } from '@playwright/test';

// E2E testleri doğrudan değil, `npm run e2e` (e2e/run-e2e.mjs) ile çalıştırılır: o betik bu koşuya özel
// boş bir stack açar ve adresini E2E_BASE_URL ile verir.
const baseURL = process.env.E2E_BASE_URL;
if (!baseURL) {
  throw new Error('E2E_BASE_URL tanımlı değil. Testleri `npm run e2e` ile çalıştırın (izole stack açar).');
}

export default defineConfig({
  testDir: './e2e',
  testMatch: '**/*.spec.ts',
  // Tüm testler tek stack'i paylaşır; her test kendi verisini oluşturur. Sıralı çalışma sonuçları deterministik kılar.
  fullyParallel: false,
  workers: 1,
  retries: 0,
  forbidOnly: true,
  timeout: 120_000,
  expect: { timeout: 15_000 },
  outputDir: './test-results',
  reporter: [['list'], ['html', { outputFolder: 'playwright-report', open: 'never' }], ['json', { outputFile: 'test-results/e2e-results.json' }]],
  use: {
    baseURL,
    ignoreHTTPSErrors: true, // Yerel stack self-signed sertifika kullanır.
    locale: 'tr-TR',
    timezoneId: 'Europe/Istanbul',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  projects: [{ name: 'chromium', use: { ...devices['Desktop Chrome'] } }],
});
