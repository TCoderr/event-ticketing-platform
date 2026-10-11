import { expect, test } from '@playwright/test';

test('izole stack hazır ve SPA sunuluyor', async ({ page, request }) => {
  const ready = await request.get('/health/ready');
  expect(ready.status()).toBe(200);

  const response = await page.goto('/');
  expect(response?.status()).toBe(200);
  expect(response?.headers()['content-security-policy']).toContain("frame-ancestors 'none'");
  await expect(page).toHaveTitle(/Loca/);
});

test('bilinmeyen API yolu SPA yerine JSON 404 döner', async ({ request }) => {
  const response = await request.get('/api/yok-boyle-bir-yol');
  expect(response.status()).toBe(404);
  expect(response.headers()['content-type']).toContain('application/problem+json');
});
