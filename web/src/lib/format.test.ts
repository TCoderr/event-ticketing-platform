import { describe, expect, it } from 'vitest';
import {
  dateColumn, formatClock, formatCountdown, formatInZone, formatMoney, formatSessionTime, formatShare, formatViewerTime,
  moneyInputValue, parseMoneyInput, toLocalInput,
} from './format';

const NBSP = ' ';

describe('para biçimi (TRY, kuruş tam sayı)', () => {
  it.each([
    [45000, `450,00${NBSP}₺`],
    [0, `0,00${NBSP}₺`],
    [5, `0,05${NBSP}₺`],
    [99, `0,99${NBSP}₺`],
    [100, `1,00${NBSP}₺`],
    [3750000, `37.500,00${NBSP}₺`],
    [123456789, `1.234.567,89${NBSP}₺`],
  ])('%d kuruş → %s', (minor, expected) => {
    expect(formatMoney(minor)).toBe(expected);
  });

  it('kayan nokta hatası üretmez (0,1 + 0,2 türü tutarlar)', () => {
    expect(formatMoney(10 + 20)).toBe(`0,30${NBSP}₺`);
    expect(formatMoney(1999 * 3)).toBe(`59,97${NBSP}₺`);
  });

  it.each([
    ['450', 45000], ['450,5', 45050], ['450,00', 45000], ['1.450,00', 145000], [' 12,34 ₺', 1234], ['0', 0],
  ])('giriş "%s" → %d kuruş', (text, minor) => {
    expect(parseMoneyInput(text)).toBe(minor);
  });

  it.each(['', 'abc', '-5', '1,234', '12.5', '1.45,00'])('geçersiz giriş "%s" reddedilir', (text) => {
    expect(parseMoneyInput(text)).toBeNull();
  });

  it('form alanı değeri gidiş-dönüşte aynı kuruşu verir', () => {
    expect(moneyInputValue(45005)).toBe('450,05');
    expect(parseMoneyInput(moneyInputValue(45005))).toBe(45005);
  });
});

describe('zaman biçimi', () => {
  const startsAt = '2026-11-20T17:30:00Z';

  it('seans saati seansın saat diliminde ve etiketiyle gösterilir', () => {
    expect(formatSessionTime(startsAt, 'Europe/Istanbul')).toBe('20 Kas 2026 Cum 20:30 · Europe/Istanbul');
    expect(formatInZone(startsAt, 'Europe/Istanbul')).toBe('20 Kas 2026 Cum 20:30');
  });

  it('gün sınırını saat dilimine göre hesaplar', () => {
    expect(formatSessionTime('2026-11-20T22:30:00Z', 'Europe/Istanbul')).toBe('21 Kas 2026 Cmt 01:30 · Europe/Istanbul');
    expect(formatSessionTime('2026-11-20T22:30:00Z', 'America/New_York')).toBe('20 Kas 2026 Cum 17:30 · America/New_York');
  });

  it('tarayıcı saat dilimi farklıysa "Senin saatinle" satırı üretir', () => {
    expect(formatViewerTime(startsAt, 'Europe/Istanbul', 'Europe/Berlin')).toBe('Senin saatinle 20 Kas 2026 Cum 18:30 · Europe/Berlin');
  });

  it('aynı saat diliminde ya da aynı duvar saatinde ikinci satır yoktur', () => {
    expect(formatViewerTime(startsAt, 'Europe/Istanbul', 'Europe/Istanbul')).toBeNull();
    expect(formatViewerTime(startsAt, 'Europe/Istanbul', 'Europe/Moscow')).toBeNull();
  });

  it('keşif satırının tarih sütunu', () => {
    expect(dateColumn(startsAt, 'Europe/Istanbul')).toEqual({ day: '20', month: 'KAS', weekday: 'CUM', time: '20:30' });
  });

  it('kapı ekranı için saniyeli saat', () => {
    expect(formatClock('2026-11-20T17:04:12Z', 'Europe/Istanbul')).toBe('20:04:12');
  });

  it('UTC anını mekanın yerel saatine (datetime-local biçimi) çevirir', () => {
    expect(toLocalInput(startsAt, 'Europe/Istanbul')).toBe('2026-11-20T20:30');
    expect(toLocalInput('2026-01-01T00:05:00Z', 'America/New_York')).toBe('2025-12-31T19:05');
  });

  it.each([[600, '10:00'], [125, '02:05'], [59.2, '00:59'], [0, '00:00'], [-4, '00:00']])('geri sayım %d sn → %s', (seconds, text) => {
    expect(formatCountdown(seconds)).toBe(text);
  });
});

describe('pay yüzdesi', () => {
  it.each([[71, 116, '%61,2'], [9, 116, '%7,8'], [36, 116, '%31,0'], [116, 116, '%100'], [0, 116, '%0,0'], [3, 0, '%0']])(
    '%d / %d → %s', (part, total, text) => {
      expect(formatShare(part, total)).toBe(text);
    });
});
