import type { EventCategory, EventStatus, OrganizationStatus, SalesMode, SessionStatus } from './types';

const NBSP = ' ';

/** Kuruş (minor unit, tam sayı) → `1.450,00 ₺`. Float aritmetiği kullanılmaz. */
export function formatMoney(minor: number): string {
  const negative = minor < 0;
  const absolute = Math.abs(Math.trunc(minor));
  const whole = String(Math.trunc(absolute / 100)).replace(/\B(?=(\d{3})+(?!\d))/g, '.');
  const cents = String(absolute % 100).padStart(2, '0');
  return `${negative ? '−' : ''}${whole},${cents}${NBSP}₺`;
}

/** `450`, `450,5`, `1.450,00` → kuruş. Geçersiz girişte `null`. */
export function parseMoneyInput(text: string): number | null {
  const trimmed = text.trim().replace(/\s|₺/g, '');
  const match = /^(\d{1,3}(?:\.\d{3})+|\d+)(?:,(\d{1,2}))?$/.exec(trimmed);
  if (!match) return null;
  const whole = Number(match[1]!.replace(/\./g, ''));
  const cents = Number((match[2] ?? '').padEnd(2, '0'));
  const minor = whole * 100 + cents;
  return Number.isSafeInteger(minor) ? minor : null;
}

/** Kuruş → form alanı metni (`450,00`). */
export function moneyInputValue(minor: number): string {
  return `${Math.trunc(minor / 100)},${String(minor % 100).padStart(2, '0')}`;
}

interface ZonedParts {
  day: string;
  month: string;
  year: string;
  weekday: string;
  hour: string;
  minute: string;
  second: string;
}

const formatters = new Map<string, Intl.DateTimeFormat>();

function zonedParts(iso: string, timeZone: string): ZonedParts {
  let formatter = formatters.get(timeZone);
  if (!formatter) {
    formatter = new Intl.DateTimeFormat('tr-TR', {
      timeZone, day: 'numeric', month: 'short', year: 'numeric', weekday: 'short',
      hour: '2-digit', minute: '2-digit', second: '2-digit', hourCycle: 'h23',
    });
    formatters.set(timeZone, formatter);
  }
  const parts: Record<string, string> = {};
  for (const part of formatter.formatToParts(new Date(iso))) parts[part.type] = part.value;
  return {
    day: parts.day ?? '', month: parts.month ?? '', year: parts.year ?? '', weekday: parts.weekday ?? '',
    hour: parts.hour ?? '', minute: parts.minute ?? '', second: parts.second ?? '',
  };
}

/** `20 Kas 2026 Cum 20:30` — verilen saat diliminde. */
export function formatInZone(iso: string, timeZone: string): string {
  const p = zonedParts(iso, timeZone);
  return `${p.day} ${p.month} ${p.year} ${p.weekday} ${p.hour}:${p.minute}`;
}

/** `20 Kas 2026 Cum 20:30 · Europe/Istanbul` — seans saati her zaman seansın saat diliminde ve etiketiyle. */
export function formatSessionTime(iso: string, timeZone: string): string {
  return `${formatInZone(iso, timeZone)} · ${timeZone}`;
}

export function browserTimeZone(): string {
  return Intl.DateTimeFormat().resolvedOptions().timeZone;
}

/**
 * Tarayıcı saat dilimi seansınkinden farklıysa ikinci satır: `Senin saatinle 20 Kas 2026 Cum 18:30 · Europe/Berlin`.
 * Saat dilimi adı farklı olup duvar saati aynıysa (ör. aynı ofsetli iki bölge) satır eklenmez.
 */
export function formatViewerTime(iso: string, sessionTimeZone: string, viewerTimeZone: string = browserTimeZone()): string | null {
  if (viewerTimeZone === sessionTimeZone) return null;
  const local = formatInZone(iso, viewerTimeZone);
  if (local === formatInZone(iso, sessionTimeZone)) return null;
  return `Senin saatinle ${local} · ${viewerTimeZone}`;
}

/** `20:04:12` — kapı ekranı için saniyeli saat. */
export function formatClock(iso: string, timeZone: string): string {
  const p = zonedParts(iso, timeZone);
  return `${p.hour}:${p.minute}:${p.second}`;
}

/** Keşif satırındaki tarih sütunu. */
export function dateColumn(iso: string, timeZone: string): { day: string; month: string; weekday: string; time: string } {
  const p = zonedParts(iso, timeZone);
  return {
    day: p.day,
    month: p.month.toLocaleUpperCase('tr'),
    weekday: p.weekday.toLocaleUpperCase('tr'),
    time: `${p.hour}:${p.minute}`,
  };
}

/** UTC anı → mekanın yerel saatinde `yyyy-MM-ddTHH:mm` (datetime-local alanı ve organizatör API'si için). */
export function toLocalInput(iso: string, timeZone: string): string {
  const formatter = new Intl.DateTimeFormat('en-CA', {
    timeZone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
  });
  const parts: Record<string, string> = {};
  for (const part of formatter.formatToParts(new Date(iso))) parts[part.type] = part.value;
  return `${parts.year}-${parts.month}-${parts.day}T${parts.hour}:${parts.minute}`;
}

/** Kalan süre → `09:59`. Negatif değer `00:00` olur. */
export function formatCountdown(totalSeconds: number): string {
  const seconds = Math.max(0, Math.floor(totalSeconds));
  return `${String(Math.floor(seconds / 60)).padStart(2, '0')}:${String(seconds % 60).padStart(2, '0')}`;
}

/** `%61,2` */
export function formatShare(part: number, total: number): string {
  if (total <= 0) return '%0';
  const tenths = Math.round((part * 1000) / total);
  return tenths === 1000 ? '%100' : `%${Math.trunc(tenths / 10)},${tenths % 10}`;
}

export const CATEGORY_LABELS: Record<EventCategory, string> = {
  Concert: 'Konser', Theatre: 'Tiyatro', Festival: 'Festival', Sports: 'Spor', Standup: 'Stand-up', Conference: 'Konferans', Other: 'Diğer',
};

export const SALES_MODE_LABELS: Record<SalesMode, string> = { ReservedSeating: 'Koltuklu', GeneralAdmission: 'Genel giriş' };

export const SESSION_STATUS_LABELS: Record<SessionStatus, string> = { Draft: 'Taslak', Published: 'Yayında', Cancelled: 'İptal edildi' };

export const EVENT_STATUS_LABELS: Record<EventStatus, string> = { Draft: 'Taslak', Published: 'Yayında', Cancelled: 'İptal edildi' };

export const ORGANIZATION_STATUS_LABELS: Record<OrganizationStatus, string> = {
  PendingApproval: 'Onay bekliyor', Active: 'Aktif', Suspended: 'Askıda',
};
