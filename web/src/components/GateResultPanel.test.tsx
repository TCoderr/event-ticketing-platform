import { render, screen } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ApiError, NetworkError } from '../lib/api';
import { GateResultPanel, gateResultFromError, gateResultFromResponse, type GateResult } from './GateResultPanel';

const TZ = 'Europe/Istanbul';

function show(result: GateResult, handlers: { onNext?: () => void; onRetry?: () => void } = {}) {
  render(<GateResultPanel result={result} timezone={TZ} onNext={handlers.onNext ?? vi.fn()} onRetry={handlers.onRetry ?? vi.fn()} />);
  return screen.getByTestId('gate-result');
}

const apiError = (status: number, code: string, extra: Record<string, unknown> = {}) => new ApiError({ status, code, ...extra });

describe('kapı sonuç paneli', () => {
  it('hazır: tarama bekler, sonuç iddia etmez', () => {
    const panel = show({ kind: 'idle' });
    expect(panel).toHaveAttribute('data-r', 'idle');
    expect(panel).toHaveTextContent('Taramaya hazır');
    expect(screen.queryByRole('button')).not.toBeInTheDocument();
  });

  it('KABUL: etiket, giriş saati (seans saat diliminde) ve kapı', () => {
    const panel = show({ kind: 'accepted', label: 'Salon / E / 7', checkedInAt: '2026-11-20T17:04:12Z', gate: 'A' });
    expect(panel).toHaveAttribute('data-r', 'accepted');
    expect(panel).toHaveTextContent('KABUL');
    expect(panel).toHaveTextContent('Salon / E / 7');
    expect(panel).toHaveTextContent('Giriş kaydedildi · 20:04:12 · Kapı A');
    expect(screen.getByRole('button', { name: 'Sıradaki bilet' })).toBeInTheDocument();
  });

  it('ZATEN KULLANILDI: ilk giriş zamanı ve kapısı gösterilir', () => {
    const panel = show({ kind: 'used', label: 'Salon / E / 7', checkedInAt: '2026-11-20T16:52:07Z', gate: 'B' });
    expect(panel).toHaveAttribute('data-r', 'used');
    expect(panel).toHaveTextContent('ZATEN KULLANILDI');
    expect(panel).toHaveTextContent('İlk giriş: 20 Kas 2026 Cum 19:52 (19:52:07) · Kapı B.');
    expect(panel).toHaveTextContent('İçeri alma.');
  });

  it.each([
    ['ticket_not_found', 'Bu kodla bilet bulunamadı.'],
    ['wrong_session', 'Bilet bu seansa ait değil.'],
    ['ticket_void', 'Bilet iptal veya iade edilmiş.'],
    ['outside_checkin_window', 'Giriş penceresi dışında.'],
    ['session_cancelled', 'Seans iptal edildi.'],
  ] as const)('GEÇERSİZ (%s): neden açık metinle', (code, reason) => {
    const panel = show({ kind: 'invalid', code });
    expect(panel).toHaveAttribute('data-r', 'invalid');
    expect(panel).toHaveTextContent('GEÇERSİZ');
    expect(panel).toHaveTextContent(reason);
    expect(panel).toHaveTextContent(code);
  });

  it('GEÇERSİZ · pencere dışı: pencere sınırları gösterilir', () => {
    const panel = show({ kind: 'invalid', code: 'outside_checkin_window', opensAt: '2026-11-20T14:30:00Z', closesAt: '2026-11-20T23:30:00Z' });
    expect(panel).toHaveTextContent('Pencere: 20 Kas 2026 Cum 17:30 – 21 Kas 2026 Cmt 02:30.');
  });

  it('BAĞLANTI YOK: sonuç bilinmiyor, aynı tarama yeniden denenir; başarı gösterilmez', async () => {
    const onRetry = vi.fn();
    const panel = show({ kind: 'offline', attempts: 2 }, { onRetry });
    expect(panel).toHaveAttribute('data-r', 'offline');
    expect(panel).toHaveTextContent('BAĞLANTI YOK');
    expect(panel).toHaveTextContent('Sonuç bilinmiyor — aynı tarama yeniden denenecek.');
    expect(panel).toHaveTextContent('Deneme 2 · aynı istek anahtarı');
    expect(panel).not.toHaveTextContent('KABUL');
    await userEvent.click(screen.getByRole('button', { name: 'Yeniden dene' }));
    expect(onRetry).toHaveBeenCalledTimes(1);
  });

  it('ÇEVRİMDIŞI: tarama gönderilmedi, check-in yapılamaz', () => {
    const panel = show({ kind: 'device-offline' });
    expect(panel).toHaveAttribute('data-r', 'device-offline');
    expect(panel).toHaveTextContent('ÇEVRİMDIŞI');
    expect(panel).toHaveTextContent('Çevrimdışı — check-in yapılamaz.');
  });

  it('sonuç yardımcı teknolojiye hemen duyurulur', () => {
    const panel = show({ kind: 'accepted', label: 'Ayakta', checkedInAt: '2026-11-20T17:04:12Z', gate: null });
    expect(panel).toHaveAttribute('role', 'status');
    expect(panel).toHaveAttribute('aria-live', 'assertive');
    expect(panel).toHaveTextContent('Giriş kaydedildi · 20:04:12');
    expect(panel).not.toHaveTextContent('Kapı');
  });

  it('"Sıradaki bilet" yeni taramaya geçer', async () => {
    const onNext = vi.fn();
    show({ kind: 'invalid', code: 'ticket_void' }, { onNext });
    await userEvent.click(screen.getByRole('button', { name: 'Sıradaki bilet' }));
    expect(onNext).toHaveBeenCalledTimes(1);
  });

  it('her durum farklı bir data-r değeri (farklı doku/çerçeve) taşır', () => {
    const kinds: GateResult[] = [
      { kind: 'idle' }, { kind: 'accepted', label: 'x', checkedInAt: '2026-11-20T17:04:12Z', gate: null },
      { kind: 'used', label: null, checkedInAt: null, gate: null }, { kind: 'invalid', code: 'ticket_void' },
      { kind: 'offline', attempts: 1 }, { kind: 'device-offline' },
    ];
    const seen = kinds.map((result) => {
      const view = render(<GateResultPanel result={result} timezone={TZ} onNext={vi.fn()} onRetry={vi.fn()} />);
      const value = view.getByTestId('gate-result').getAttribute('data-r');
      view.unmount();
      return value;
    });
    expect(new Set(seen).size).toBe(kinds.length);
  });
});

describe('check-in yanıtının panel durumuna çevrilmesi', () => {
  it('200 → KABUL', () => {
    expect(gateResultFromResponse({ result: 'Accepted', ticketId: 't', label: 'Salon / A / 1', checkedInAt: '2026-11-20T17:04:12Z', gate: 'A' }))
      .toEqual({ kind: 'accepted', label: 'Salon / A / 1', checkedInAt: '2026-11-20T17:04:12Z', gate: 'A' });
  });

  it('409 already_checked_in → ZATEN KULLANILDI (ilk giriş bilgisiyle)', () => {
    expect(gateResultFromError(apiError(409, 'already_checked_in', { checkedInAt: '2026-11-20T16:52:07Z', gate: 'B', label: 'Salon / A / 1' }), 1))
      .toEqual({ kind: 'used', label: 'Salon / A / 1', checkedInAt: '2026-11-20T16:52:07Z', gate: 'B' });
  });

  it.each([
    [404, 'ticket_not_found'], [409, 'wrong_session'], [409, 'ticket_void'], [409, 'session_cancelled'],
  ] as const)('%d %s → GEÇERSİZ', (status, code) => {
    expect(gateResultFromError(apiError(status, code), 1)).toMatchObject({ kind: 'invalid', code });
  });

  it('409 outside_checkin_window → GEÇERSİZ + pencere sınırları', () => {
    expect(gateResultFromError(apiError(409, 'outside_checkin_window', { opensAt: 'a', closesAt: 'b' }), 1))
      .toEqual({ kind: 'invalid', code: 'outside_checkin_window', opensAt: 'a', closesAt: 'b' });
  });

  it('ağ hatası ve 503 → BAĞLANTI YOK (asla kabul ya da geçersiz değil)', () => {
    expect(gateResultFromError(new NetworkError(), 3)).toEqual({ kind: 'offline', attempts: 3 });
    expect(gateResultFromError(apiError(503, 'service_unavailable'), 1)).toEqual({ kind: 'offline', attempts: 1 });
  });

  it('atanmamış personel (404 not_found) ve hız sınırı açık metinle hata olur', () => {
    expect(gateResultFromError(apiError(404, 'not_found'), 1)).toMatchObject({ kind: 'error' });
    const limited = gateResultFromError(apiError(429, 'rate_limited', { retryAfterSeconds: 12 }), 1);
    expect(limited).toMatchObject({ kind: 'error' });
    expect(limited.kind === 'error' && limited.message).toContain('12 saniye sonra');
  });
});
