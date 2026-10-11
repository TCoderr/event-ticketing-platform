import { useEffect, useId, useRef, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { ApiError } from '../lib/api';
import { describeError } from '../lib/errors';
import { formatSessionTime, formatViewerTime } from '../lib/format';
import { Icon, type IconName } from './Icon';

let firstHeading = true;

/** Sayfa başlığı. Rota değişiminde odağı başlığa taşır (ilk yüklemede değil) ve belge başlığını günceller. */
export function PageHeading({ title, children }: { title: string; children?: ReactNode }) {
  const ref = useRef<HTMLHeadingElement>(null);
  useEffect(() => {
    document.title = `${title} — Loca`;
  }, [title]);
  useEffect(() => {
    if (firstHeading) {
      firstHeading = false;
      return;
    }
    ref.current?.focus({ preventScroll: true });
    window.scrollTo(0, 0);
  }, []);
  return (
    <h1 ref={ref} tabIndex={-1}>
      {children ?? title}
    </h1>
  );
}

export function DemoTag({ label = 'Demo' }: { label?: string }) {
  return <span className="demo-tag">{label}</span>;
}

/** Seans saati: her zaman seansın saat diliminde ve etiketiyle; tarayıcı saati farklıysa ikinci satır. */
export function SessionTime({ startsAt, timezone }: { startsAt: string; timezone: string }) {
  const viewer = formatViewerTime(startsAt, timezone);
  return (
    <span className="when">
      <time dateTime={startsAt}>{formatSessionTime(startsAt, timezone)}</time>
      {viewer && <span className="when-local">{viewer}</span>}
    </span>
  );
}

export type Tone = 'ok' | 'warn' | 'err' | 'info' | 'mute';

/** Durum etiketi: renk tek başına anlam taşımaz, her zaman ikon + metin. */
export function Status({ tone, icon, children }: { tone: Tone; icon: IconName; children: ReactNode }) {
  return (
    <span className={`status t-${tone}`}>
      <Icon name={icon} />
      <span>{children}</span>
    </span>
  );
}

export function Loading({ label = 'Yükleniyor…' }: { label?: string }) {
  return (
    <div className="state" role="status" aria-busy="true">
      <p>{label}</p>
    </div>
  );
}

export function EmptyState({ title, children }: { title: string; children?: ReactNode }) {
  return (
    <div className="state">
      <p className="state-title">{title}</p>
      {children}
    </div>
  );
}

/** Sorgu hatası: bulunamadı / yetkisiz / genel hata + yeniden dene. */
export function ErrorState({ error, onRetry, notFoundText }: { error: unknown; onRetry?: () => void; notFoundText?: string }) {
  const status = error instanceof ApiError ? error.status : 0;
  if (status === 404) {
    return (
      <div className="state" role="alert">
        <p className="state-title"><Icon name="cross" />Bulunamadı</p>
        <p>{notFoundText ?? 'Bu kayıt yok ya da buna erişimin yok.'}</p>
        <p><Link className="btn" to="/">Etkinliklere dön</Link></p>
      </div>
    );
  }
  if (status === 403) {
    return (
      <div className="state" role="alert">
        <p className="state-title"><Icon name="cross" />Erişim yok</p>
        <p>Bu sayfayı görmek için yetkin yok.</p>
        <p><Link className="btn" to="/">Etkinliklere dön</Link></p>
      </div>
    );
  }
  return (
    <div className="state" role="alert">
      <p className="state-title"><Icon name="warn" />Yüklenemedi</p>
      <p>{describeError(error)}</p>
      {onRetry && (
        <p><button type="button" className="btn" onClick={onRetry}>Yeniden dene</button></p>
      )}
    </div>
  );
}

/** Eylem (mutation) hatası. `role="alert"` ile duyurulur. */
export function ErrorAlert({ error, id }: { error: unknown; id?: string }) {
  if (!error) return null;
  return (
    <p className="form-alert" role="alert" id={id}>
      <Icon name="warn" />
      <span>{typeof error === 'string' ? error : describeError(error)}</span>
    </p>
  );
}

export function OkNote({ children }: { children: ReactNode }) {
  return (
    <p className="form-ok" role="status">
      <Icon name="check" />
      <span>{children}</span>
    </p>
  );
}

interface FieldProps {
  label: string;
  error?: string | null;
  hint?: string;
  /** Alanı çizen fonksiyon: `id`, `aria-describedby` ve `aria-invalid` verilen nesneyle bağlanır. */
  children: (control: { id: string; 'aria-describedby'?: string; 'aria-invalid'?: true }) => ReactNode;
  className?: string;
}

/** Etiket + alan + ipucu + hata. Hata metni alana `aria-describedby` ile bağlıdır ve `role="alert"` taşır. */
export function Field({ label, error, hint, children, className }: FieldProps) {
  const id = useId();
  const hintId = hint ? `${id}-hint` : undefined;
  const errorId = error ? `${id}-err` : undefined;
  const describedBy = [hintId, errorId].filter(Boolean).join(' ') || undefined;
  return (
    <div className={`field${className ? ` ${className}` : ''}`}>
      <label htmlFor={id}>{label}</label>
      {children({ id, 'aria-describedby': describedBy, ...(error ? { 'aria-invalid': true as const } : {}) })}
      {hint && <span className="field-hint" id={hintId}>{hint}</span>}
      {error && (
        <span className="field-err" id={errorId} role="alert">
          <Icon name="warn" />
          <span>{error}</span>
        </span>
      )}
    </div>
  );
}

/** Geri alınamaz eylemler için satır içi onay adımı. */
export function ConfirmPanel({ title, children, confirmLabel, cancelLabel = 'Vazgeç', onConfirm, onCancel, busy }: {
  title: string;
  children: ReactNode;
  confirmLabel: string;
  cancelLabel?: string;
  onConfirm: () => void;
  onCancel: () => void;
  busy?: boolean;
}) {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    ref.current?.focus();
  }, []);
  return (
    <div className="confirm" role="group" aria-label={title} tabIndex={-1} ref={ref}>
      <p><strong>{title}</strong></p>
      {children}
      <div className="row-actions">
        <button type="button" className="btn btn-danger" onClick={onConfirm} disabled={busy}>{busy ? 'Gönderiliyor…' : confirmLabel}</button>
        <button type="button" className="btn" onClick={onCancel} disabled={busy}>{cancelLabel}</button>
      </div>
    </div>
  );
}
