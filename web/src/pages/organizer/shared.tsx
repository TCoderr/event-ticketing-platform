import { useState, type InputHTMLAttributes, type ReactNode } from 'react';
import { Link } from 'react-router-dom';
import { Icon, type IconName } from '../../components/Icon';
import { DemoTag, Field, SessionTime } from '../../components/ui';
import { ApiError } from '../../lib/api';
import { invalidFields } from '../../lib/errors';
import { browserTimeZone, formatInZone, SALES_MODE_LABELS, SESSION_STATUS_LABELS } from '../../lib/format';
import type { OrganizerSession, ReconciliationOutcome } from '../../lib/types';

export const orgPath = (orgId: string, rest = '') => `/organizator/${orgId}${rest ? `/${rest}` : ''}`;

/** İstemci tarafında "bulunamadı" durumu üretmek için (ör. URL'deki organizasyon üyeliklerde yok). */
export const notFoundError = () => new ApiError({ status: 404, code: 'not_found' });

/** Tek seferde bir eylem: meşgul adı, hata ve başarı metni. Başarı yalnız sunucu yanıtından sonra gösterilir. */
export function useAction() {
  const [busy, setBusy] = useState<string | null>(null);
  const [error, setError] = useState<unknown>(null);
  const [done, setDone] = useState<string | null>(null);

  const run = async (name: string, work: () => Promise<string | void>): Promise<boolean> => {
    if (busy !== null) return false;
    setBusy(name);
    setError(null);
    setDone(null);
    try {
      const message = await work();
      setDone(typeof message === 'string' ? message : null);
      return true;
    } catch (failure) {
      setError(failure);
      return false;
    } finally {
      setBusy(null);
    }
  };

  const reset = () => {
    setError(null);
    setDone(null);
  };
  return { busy, error, done, run, reset };
}

/** Sunucunun `validation_failed` yanıtındaki alanları form alanlarına Türkçe genel mesajla eşler. */
export function serverFieldErrors<K extends string>(error: unknown, messages: Record<K, string>): Partial<Record<K, string>> {
  const fields = invalidFields(error);
  const result: Partial<Record<K, string>> = {};
  for (const name of Object.keys(messages) as K[]) {
    if (fields.some((field) => field === name || field.endsWith(`.${name}`))) result[name] = messages[name];
  }
  return result;
}

type InputProps = Omit<InputHTMLAttributes<HTMLInputElement>, 'value' | 'onChange' | 'id' | 'className'>;

export function TextField({ label, value, onChange, error, hint, className, ...input }: InputProps & {
  label: string;
  value: string;
  onChange: (value: string) => void;
  error?: string | null;
  hint?: string;
  className?: string;
}) {
  return (
    <Field label={label} error={error} hint={hint} className={className}>
      {(control) => <input type="text" {...input} {...control} value={value} onChange={(e) => onChange(e.target.value)} />}
    </Field>
  );
}

export function SelectField({ label, value, onChange, error, hint, className, disabled, children }: {
  label: string;
  value: string;
  onChange: (value: string) => void;
  error?: string | null;
  hint?: string;
  className?: string;
  disabled?: boolean;
  children: ReactNode;
}) {
  return (
    <Field label={label} error={error} hint={hint} className={className}>
      {(control) => (
        <select {...control} value={value} disabled={disabled} onChange={(e) => onChange(e.target.value)}>{children}</select>
      )}
    </Field>
  );
}

const OUTCOMES: Record<ReconciliationOutcome, { className: string; icon: IconName; label: string }> = {
  Planned: { className: 'res-planned', icon: 'planned', label: 'planlandı' },
  Attempted: { className: 'res-attempted', icon: 'half', label: 'denendi' },
  Succeeded: { className: 'res-succeeded', icon: 'check', label: 'başarılı' },
  Failed: { className: 'res-failed', icon: 'cross', label: 'başarısız' },
};

/** Operatör eylemi sonucu rozeti: renk + ikon + metin + çerçeve dokusu. */
export function OutcomeBadge({ outcome }: { outcome: ReconciliationOutcome }) {
  const view = OUTCOMES[outcome];
  return (
    <span className={`res ${view.className}`}>
      <Icon name={view.icon} />
      {view.label}
    </span>
  );
}

/** Yönetim/operasyon listelerindeki anlar: seans bağlamı olmadığından tarayıcı saat diliminde, etiketiyle. */
export function formatLocalStamp(iso: string): string {
  const zone = browserTimeZone();
  return `${formatInZone(iso, zone)} · ${zone}`;
}

/** Seans editörü ve özeti için ortak başlık altı: etkinlik, saat, mekan, durum çipleri. */
export function SessionContext({ session, isDemo = false }: { session: OrganizerSession; isDemo?: boolean }) {
  return (
    <>
      <p className="ctx">
        <strong>{session.eventTitle} {isDemo && <DemoTag />}</strong>
        <SessionTime startsAt={session.startsAt} timezone={session.timezone} />
        <span className="venue">{session.venueName}, {session.city}</span>
      </p>
      <ul className="chips" aria-label="Seans özellikleri">
        <li className="chip">{SALES_MODE_LABELS[session.salesMode]}</li>
        <li className="chip">{SESSION_STATUS_LABELS[session.status]}</li>
        <li className={`chip${session.demoSimulatorEnabled ? '' : ' is-off'}`}>
          Demo simülatörü {session.demoSimulatorEnabled ? 'açık' : 'kapalı'}
        </li>
      </ul>
    </>
  );
}

export function Breadcrumb({ orgId, eventId, eventTitle, tail }: { orgId: string; eventId?: string; eventTitle?: string; tail: string }) {
  return (
    <p className="eyebrow">
      <span>
        <Link to={orgPath(orgId, 'etkinlikler')}>Etkinlikler</Link>
        {eventId && eventTitle && <> / <Link to={orgPath(orgId, `etkinlik/${eventId}`)}>{eventTitle}</Link></>}
        {' '}/ {tail}
      </span>
    </p>
  );
}
