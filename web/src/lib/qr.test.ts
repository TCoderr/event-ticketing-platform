import jsQR from 'jsqr';
import { describe, expect, it } from 'vitest';
import { createQrMatrix, qrToSvgPath, rasterizeQr } from './qr';

// Sunucunun ürettiği biçim: "LT1." + 43 karakter base64url (32 bayt).
const sampleTokens = [
  'LT1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA',
  'LT1.q3Zr8-_Xb0LmN5tYw1Pd7cE2hG4jK6sU9vA0xB_CdEf',
  'LT1.-_-_-_-_-_-_-_-_-_-_-_-_-_-_-_-_-_-_-_-_-_-',
  'LT1.zzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzzz',
];

describe('bilet QR kodu', () => {
  it.each(sampleTokens)('gerçek bir çözücüyle (jsQR) gidiş-dönüşte aynı token okunur: %s', (token) => {
    const image = rasterizeQr(createQrMatrix(token), 4);
    const decoded = jsQR(image.data, image.width, image.height);
    expect(decoded?.data).toBe(token);
  });

  it('düşük çözünürlükte (modül başına 2 piksel) de okunur', () => {
    const token = sampleTokens[1]!;
    const image = rasterizeQr(createQrMatrix(token), 2);
    expect(jsQR(image.data, image.width, image.height)?.data).toBe(token);
  });

  it('içerik yalnız token baytlarıdır; başka veri eklenmez', () => {
    const token = sampleTokens[1]!;
    const image = rasterizeQr(createQrMatrix(token), 4);
    const decoded = jsQR(image.data, image.width, image.height);
    expect(decoded?.binaryData.length).toBe(token.length);
    expect(decoded?.data.startsWith('LT1.')).toBe(true);
  });

  it('farklı token farklı desen üretir ve SVG yolu boş değildir', () => {
    const first = qrToSvgPath(createQrMatrix(sampleTokens[0]!));
    const second = qrToSvgPath(createQrMatrix(sampleTokens[1]!));
    expect(first.length).toBeGreaterThan(100);
    expect(first).not.toBe(second);
  });
});
