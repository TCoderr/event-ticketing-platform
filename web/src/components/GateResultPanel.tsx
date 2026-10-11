import { ApiError, isOutcomeUnknown } from '../lib/api';
import { describeError } from '../lib/errors';
import { formatClock, formatInZone } from '../lib/format';
import type { CheckInResponse } from '../lib/types';
import { Icon } from './Icon';

export type InvalidCode = 'ticket_not_found' | 'wrong_session' | 'ticket_void' | 'outside_checkin_window' | 'session_cancelled';

export type GateResult =
  | { kind: 'idle' }
  | { kind: 'pending' }
  | { kind: 'accepted'; label: string; checkedInAt: string; gate: string | null }
  | { kind: 'used'; label: string | null; checkedInAt: string | null; gate: string | null }
  | { kind: 'invalid'; code: InvalidCode; opensAt?: string; closesAt?: string }
  /** İstek gönderildi ama yanıt alınamadı: sonuç bilinmiyor; aynı Idempotency-Key ile yeniden denenir. */
  | { kind: 'offline'; attempts: number }
  /** Cihaz çevrimdışı: istek hiç gönderilmedi. Offline check-in yoktur. */
  | { kind: 'device-offline' }
  | { kind: 'error'; message: string };

const INVALID_CODES: readonly InvalidCode[] = ['ticket_not_found', 'wrong_session', 'ticket_void', 'outside_checkin_window', 'session_cancelled'];

const INVALID_REASON: Record<InvalidCode, string> = {
  ticket_not_found: 'Bu kodla bilet bulunamadı.',
  wrong_session: 'Bilet bu seansa ait değil.',
  ticket_void: 'Bilet iptal veya iade edilmiş.',
  outside_checkin_window: 'Giriş penceresi dışında.',
  session_cancelled: 'Seans iptal edildi.',
};

const text = (value: unknown): string | null => (typeof value === 'string' && value.length > 0 ? value : null);

export function gateResultFromResponse(response: CheckInResponse): GateResult {
  return { kind: 'accepted', label: response.label, checkedInAt: response.checkedInAt, gate: response.gate };
}

/** Check-in hatasını panel durumuna çevirir. Ağ hatası / 503 asla "geçersiz" ya da "kabul" sayılmaz. */
export function gateResultFromError(error: unknown, attempts: number): GateResult {
  if (isOutcomeUnknown(error)) return { kind: 'offline', attempts };
  if (error instanceof ApiError) {
    const p = error.problem;
    if (error.code === 'already_checked_in') {
      return { kind: 'used', label: text(p.label), checkedInAt: text(p.checkedInAt), gate: text(p.gate) };
    }
    if ((INVALID_CODES as readonly string[]).includes(error.code)) {
      return { kind: 'invalid', code: error.code as InvalidCode, opensAt: text(p.opensAt) ?? undefined, closesAt: text(p.closesAt) ?? undefined };
    }
    if (error.code === 'not_found') return { kind: 'error', message: 'Bu seans için kapı görevin yok ya da görevin kaldırıldı. Giriş kaydedilmedi.' };
  }
  return { kind: 'error', message: `${describeError(error)} Giriş kaydedilmedi.` };
}

interface GateResultPanelProps {
  result: GateResult;
  timezone: string;
  onNext: () => void;
  onRetry: () => void;
}

/**
 * Tarama sonucu. Her durum ikon + büyük metin + farklı doku/çerçeve ile ayrılır; renk tek başına anlam taşımaz:
 * KABUL düz dolgu, ZATEN KULLANILDI çapraz tarama, GEÇERSİZ kalın çerçeve, BAĞLANTI YOK kesik çerçeve.
 */
export function GateResultPanel({ result, timezone, onNext, onRetry }: GateResultPanelProps) {
  const next = (
    <button type="button" className="g-btn" onClick={onNext} data-gate-focus>Sıradaki bilet</button>
  );
  return (
    <div className="gres" data-r={result.kind} role="status" aria-live="assertive" aria-atomic="true" data-testid="gate-result">
      {result.kind === 'idle' && (
        <>
          <span className="gres-ico"><Icon name="scan" /></span>
          <p className="gres-word">Taramaya hazır</p>
          <p className="gres-info">Bileti okut veya kodu elle gir.</p>
        </>
      )}
      {result.kind === 'pending' && (
        <>
          <span className="gres-ico"><Icon name="dots" /></span>
          <p className="gres-word">Kontrol ediliyor…</p>
          <p className="gres-info">Sunucu yanıtı bekleniyor. Henüz sonuç yok.</p>
        </>
      )}
      {result.kind === 'accepted' && (
        <>
          <span className="gres-ico"><Icon name="check" /></span>
          <p className="gres-word">KABUL</p>
          <p className="gres-label">{result.label}</p>
          <p className="gres-info">
            Giriş kaydedildi · {formatClock(result.checkedInAt, timezone)}{result.gate ? ` · Kapı ${result.gate}` : ''}
          </p>
          {next}
        </>
      )}
      {result.kind === 'used' && (
        <>
          <span className="gres-ico"><Icon name="repeat" /></span>
          <p className="gres-word">ZATEN KULLANILDI</p>
          {result.label && <p className="gres-label">{result.label}</p>}
          <p className="gres-info">
            {result.checkedInAt
              ? `İlk giriş: ${formatInZone(result.checkedInAt, timezone)} (${formatClock(result.checkedInAt, timezone)})${result.gate ? ` · Kapı ${result.gate}` : ''}. `
              : ''}
            İçeri alma.
          </p>
          <p className="gres-code">already_checked_in</p>
          {next}
        </>
      )}
      {result.kind === 'invalid' && (
        <>
          <span className="gres-ico"><Icon name="cross" /></span>
          <p className="gres-word">GEÇERSİZ</p>
          <p className="gres-info">
            {INVALID_REASON[result.code]}
            {result.code === 'outside_checkin_window' && result.opensAt && result.closesAt
              ? ` Pencere: ${formatInZone(result.opensAt, timezone)} – ${formatInZone(result.closesAt, timezone)}.`
              : ''}{' '}
            İçeri alma.
          </p>
          <p className="gres-code">{result.code}</p>
          {next}
        </>
      )}
      {result.kind === 'offline' && (
        <>
          <span className="gres-ico"><Icon name="warn" /></span>
          <p className="gres-word">BAĞLANTI YOK</p>
          <p className="gres-info">Sonuç bilinmiyor — aynı tarama yeniden denenecek. Giriş onaylanmadı; içeri alma.</p>
          <p className="gres-code">Deneme {result.attempts} · aynı istek anahtarı</p>
          <button type="button" className="g-btn" onClick={onRetry} data-gate-focus><Icon name="repeat" />Yeniden dene</button>
          <button type="button" className="g-btn" onClick={onNext}>Vazgeç, sıradaki bilet</button>
        </>
      )}
      {result.kind === 'device-offline' && (
        <>
          <span className="gres-ico"><Icon name="offline" /></span>
          <p className="gres-word">ÇEVRİMDIŞI</p>
          <p className="gres-info">Çevrimdışı — check-in yapılamaz. Tarama gönderilmedi; içeri alma. Bağlantı gelince yeniden okut.</p>
          {next}
        </>
      )}
      {result.kind === 'error' && (
        <>
          <span className="gres-ico"><Icon name="warn" /></span>
          <p className="gres-word">KONTROL EDİLEMEDİ</p>
          <p className="gres-info">{result.message}</p>
          {next}
        </>
      )}
    </div>
  );
}
