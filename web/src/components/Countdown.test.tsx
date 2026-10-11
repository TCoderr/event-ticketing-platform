import { act, render, screen } from '@testing-library/react';
import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { Countdown, remainingMs } from './Countdown';

// Monoton saat testte elle ilerletilir; tarayıcının duvar saati bilerek yanlış bir yıla ayarlanır.
let monotonic = 0;
const now = () => monotonic;

function advance(ms: number) {
  monotonic += ms;
  act(() => {
    vi.advanceTimersByTime(250);
  });
}

beforeEach(() => {
  monotonic = 5_000;
  vi.useFakeTimers();
  vi.setSystemTime(new Date('2001-01-01T00:00:00Z'));
});

afterEach(() => {
  vi.useRealTimers();
});

const timer = () => screen.getByRole('timer');
const live = () => screen.getByTestId('countdown-live');

describe('rezervasyon geri sayımı', () => {
  it('kalan süre sunucunun expiresAt − serverTime farkından kurulur; tarayıcı saati etkilemez', () => {
    expect(remainingMs('2026-11-20T17:40:00Z', '2026-11-20T17:30:00Z')).toBe(600_000);
    render(<Countdown expiresAt="2026-11-20T17:40:00Z" serverTime="2026-11-20T17:30:00Z" receivedAt={5_000} onExpire={vi.fn()} now={now} />);
    // Tarayıcı saati 2001: sayaç yine 10:00'dan başlar.
    expect(timer()).toHaveTextContent('10:00');

    advance(1_000);
    expect(timer()).toHaveTextContent('09:59');

    // Duvar saati ileri/geri alınsa da sayaç monoton saate göre ilerler.
    vi.setSystemTime(new Date('2031-06-01T00:00:00Z'));
    advance(59_000);
    expect(timer()).toHaveTextContent('09:00');
  });

  it('yanıtın alındığı andan beri geçen süre düşülür', () => {
    monotonic = 35_000; // Yanıt 30 sn önce alınmıştı.
    render(<Countdown expiresAt="2026-11-20T17:32:00Z" serverTime="2026-11-20T17:30:00Z" receivedAt={5_000} onExpire={vi.fn()} now={now} />);
    expect(timer()).toHaveTextContent('01:30');
  });

  it('sayaç aria-live="off"; yalnız 2 dakika ve 30 saniye kala duyuru yapılır', () => {
    render(<Countdown expiresAt="2026-11-20T17:40:00Z" serverTime="2026-11-20T17:30:00Z" receivedAt={5_000} onExpire={vi.fn()} now={now} />);
    expect(timer()).toHaveAttribute('aria-live', 'off');
    expect(live()).toHaveAttribute('aria-live', 'polite');
    expect(live()).toBeEmptyDOMElement();

    advance(300_000); // 05:00 kaldı
    expect(live()).toBeEmptyDOMElement();

    advance(179_000); // 02:01
    expect(timer()).toHaveTextContent('02:01');
    expect(live()).toBeEmptyDOMElement();

    advance(1_000); // 02:00
    expect(live()).toHaveTextContent('Rezervasyonun bitmesine 2 dakika kaldı.');
    expect(screen.getByText('Süre azalıyor')).toBeInTheDocument();

    advance(60_000); // 01:00 — yeni duyuru yok
    expect(live()).toHaveTextContent('Rezervasyonun bitmesine 2 dakika kaldı.');

    advance(30_000); // 00:30
    expect(live()).toHaveTextContent('Rezervasyonun bitmesine 30 saniye kaldı.');
  });

  it('süre bitince 00:00 gösterir ve sunucudan yeniden okuma için onExpire\'ı bir kez çağırır', () => {
    const onExpire = vi.fn();
    render(<Countdown expiresAt="2026-11-20T17:30:08Z" serverTime="2026-11-20T17:30:00Z" receivedAt={5_000} onExpire={onExpire} now={now} />);
    expect(timer()).toHaveTextContent('00:08');

    advance(7_000);
    expect(timer()).toHaveTextContent('00:01');
    expect(onExpire).not.toHaveBeenCalled();

    advance(1_000);
    expect(timer()).toHaveTextContent('00:00');
    expect(onExpire).toHaveBeenCalledTimes(1);
    expect(live()).toHaveTextContent('Rezervasyon süresi doldu.');
    expect(screen.getByText('Süre doldu')).toBeInTheDocument();

    advance(5_000);
    expect(onExpire).toHaveBeenCalledTimes(1);
    expect(timer()).toHaveTextContent('00:00');
  });

  it('sunucu hâlâ aktif derse (yeni serverTime) sayaç yeniden kurulur ve süre bitiminde tekrar sorar', () => {
    const onExpire = vi.fn();
    const view = render(<Countdown expiresAt="2026-11-20T17:30:02Z" serverTime="2026-11-20T17:30:00Z" receivedAt={5_000} onExpire={onExpire} now={now} />);
    advance(2_000);
    expect(onExpire).toHaveBeenCalledTimes(1);

    // Yeniden okuma: sunucuya göre 1 saniye daha var.
    view.rerender(<Countdown expiresAt="2026-11-20T17:30:02Z" serverTime="2026-11-20T17:30:01Z" receivedAt={monotonic} onExpire={onExpire} now={now} />);
    expect(timer()).toHaveTextContent('00:01');
    advance(1_000);
    expect(onExpire).toHaveBeenCalledTimes(2);
  });

  it('süresi çoktan dolmuş yanıt hemen 00:00 gösterir', () => {
    const onExpire = vi.fn();
    render(<Countdown expiresAt="2026-11-20T17:29:00Z" serverTime="2026-11-20T17:30:00Z" receivedAt={5_000} onExpire={onExpire} now={now} />);
    expect(timer()).toHaveTextContent('00:00');
    expect(onExpire).toHaveBeenCalledTimes(1);
  });
});
