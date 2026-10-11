import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link, useNavigate, useParams } from 'react-router-dom';
import { useUserKey } from '../../auth/AuthContext';
import { DemoTag, EmptyState, ErrorAlert, ErrorState, Field, Loading, OkNote, PageHeading, SessionTime, Status } from '../../components/ui';
import { api } from '../../lib/api';
import { CATEGORY_LABELS, EVENT_STATUS_LABELS, SALES_MODE_LABELS, SESSION_STATUS_LABELS } from '../../lib/format';
import type { EventCategory, OrganizerEvent, OrganizerSession, OrganizerSessionSummary, SalesMode, Venue } from '../../lib/types';
import { Breadcrumb, notFoundError, orgPath, SelectField, serverFieldErrors, TextField, useAction } from './shared';

const CATEGORIES = Object.keys(CATEGORY_LABELS) as EventCategory[];

interface EventValues {
  title: string;
  description: string;
  category: EventCategory;
}

type EventErrors = Partial<Record<keyof EventValues, string>>;

function validateEvent(values: EventValues): EventErrors {
  const errors: EventErrors = {};
  const title = values.title.trim();
  const description = values.description.trim();
  if (title.length < 3 || title.length > 160) errors.title = 'Başlık 3–160 karakter olmalı.';
  if (description.length < 10 || description.length > 4000) errors.description = 'Açıklama 10–4000 karakter olmalı.';
  return errors;
}

function EventForm({ initial, submitLabel, busyLabel, disabled = false, onSubmit }: {
  initial: EventValues;
  submitLabel: string;
  busyLabel: string;
  disabled?: boolean;
  onSubmit: (values: EventValues) => Promise<string | void>;
}) {
  const [values, setValues] = useState(initial);
  const [errors, setErrors] = useState<EventErrors>({});
  const action = useAction();

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const next = validateEvent(values);
    setErrors(next);
    if (Object.keys(next).length > 0) return;
    void action.run('save', async () => {
      try {
        return await onSubmit({ title: values.title.trim(), description: values.description.trim(), category: values.category });
      } catch (error) {
        setErrors(serverFieldErrors(error, {
          title: 'Başlık geçersiz (3–160 karakter).', description: 'Açıklama geçersiz (10–4000 karakter).', category: 'Kategori geçersiz.',
        }));
        throw error;
      }
    });
  };

  return (
    <form className="form form-wide" onSubmit={submit} noValidate>
      <ErrorAlert error={action.error} />
      {action.done && <OkNote>{action.done}</OkNote>}
      <TextField label="Başlık" value={values.title} onChange={(title) => setValues({ ...values, title })} error={errors.title} maxLength={160} disabled={disabled} />
      <Field label="Açıklama" error={errors.description}>
        {(control) => (
          <textarea {...control} value={values.description} maxLength={4000} disabled={disabled}
            onChange={(e) => setValues({ ...values, description: e.target.value })} />
        )}
      </Field>
      <SelectField label="Kategori" value={values.category} onChange={(category) => setValues({ ...values, category: category as EventCategory })}
        error={errors.category} disabled={disabled}>
        {CATEGORIES.map((category) => <option key={category} value={category}>{CATEGORY_LABELS[category]}</option>)}
      </SelectField>
      <div className="row-actions">
        <button type="submit" className="btn btn-primary" disabled={disabled || action.busy !== null}>
          {action.busy ? busyLabel : submitLabel}
        </button>
      </div>
    </form>
  );
}

function SessionLinks({ orgId, eventTitle, sessions }: { orgId: string; eventTitle: string; sessions: OrganizerSessionSummary[] }) {
  if (sessions.length === 0) return <p className="note" style={{ marginTop: 6 }}>Henüz seans yok.</p>;
  return (
    <ul className="org-sessions" aria-label={`${eventTitle} seansları`}>
      {sessions.map((session) => (
        <li key={session.id}>
          <Link to={orgPath(orgId, `seans/${session.id}`)}>
            <SessionTime startsAt={session.startsAt} timezone={session.timezone} />
            <span className="sr-only"> — seansı düzenle</span>
          </Link>
          <span className="t-mute">{session.venueName}, {session.city} · {SALES_MODE_LABELS[session.salesMode]}</span>
          <span className="chip">{SESSION_STATUS_LABELS[session.status]}</span>
          <Link to={orgPath(orgId, `seans/${session.id}/ozet`)}>
            Özet<span className="sr-only"> — {eventTitle}, {session.startsAtLocal}</span>
          </Link>
        </li>
      ))}
    </ul>
  );
}

export function EventsPage() {
  const { orgId = '' } = useParams();
  const userKey = useUserKey();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const events = useQuery({
    queryKey: userKey('org', orgId, 'events'),
    queryFn: () => api.get<OrganizerEvent[]>(`/api/organizer/organizations/${orgId}/events`),
  });

  const create = async (values: EventValues) => {
    const created = await api.post<OrganizerEvent>(`/api/organizer/organizations/${orgId}/events`, values);
    void queryClient.invalidateQueries({ queryKey: userKey('org', orgId, 'events') });
    navigate(orgPath(orgId, `etkinlik/${created.id}`));
  };

  return (
    <>
      <PageHeading title="Etkinlikler" />
      <p className="lede">Yalnız bu organizasyonun etkinlikleri ve seansları listelenir.</p>
      {events.isPending ? <Loading label="Etkinlikler yükleniyor…" />
        : events.isError ? <ErrorState error={events.error} onRetry={() => void events.refetch()} />
          : events.data.length === 0 ? (
            <EmptyState title="Henüz etkinlik yok."><p>İlk etkinliği aşağıdaki formla oluştur.</p></EmptyState>
          ) : (
            <ul className="list" data-testid="org-events">
              {events.data.map((event) => (
                <li key={event.id}>
                  <div>
                    <p className="l-title">
                      <Link to={orgPath(orgId, `etkinlik/${event.id}`)}>{event.title}</Link>{' '}
                      {event.isDemo && <DemoTag />}
                    </p>
                    <p className="l-meta">{CATEGORY_LABELS[event.category]} · {event.sessions.length} seans</p>
                    {event.moderationBlocked && (
                      <p><Status tone="err" icon="warn">Platform yönetimi tarafından yayından kaldırıldı</Status></p>
                    )}
                    <SessionLinks orgId={orgId} eventTitle={event.title} sessions={event.sessions} />
                  </div>
                  <div className="l-side">
                    <span className="chip">{EVENT_STATUS_LABELS[event.status]}</span>
                    <Link className="btn btn-sm" to={orgPath(orgId, `etkinlik/${event.id}`)}>
                      Düzenle<span className="sr-only"> — {event.title}</span>
                    </Link>
                  </div>
                </li>
              ))}
            </ul>
          )}

      <div className="blk">
        <h2>Yeni etkinlik</h2>
        <p className="note">Etkinlik taslak olarak oluşturulur; seans eklemek ve yayınlamak için sonraki sayfaya geçilir.</p>
        <EventForm initial={{ title: '', description: '', category: 'Concert' }} submitLabel="Etkinliği oluştur" busyLabel="Oluşturuluyor…" onSubmit={create} />
      </div>
    </>
  );
}

// --- Etkinlik ayrıntısı ------------------------------------------------------------------------

interface SessionDraft {
  venueId: string;
  startsAtLocal: string;
  salesStartAtLocal: string;
  salesEndAtLocal: string;
  salesMode: SalesMode;
  holdTtlSeconds: string;
}

type SessionDraftErrors = Partial<Record<keyof SessionDraft, string>>;

const SESSION_FIELD_MESSAGES: Record<keyof SessionDraft, string> = {
  venueId: 'Mekan bu organizasyona ait değil.',
  startsAtLocal: 'Başlangıç geçersiz: gelecekte bir yerel saat seç.',
  salesStartAtLocal: 'Satış başlangıcı geçersiz: satış bitişinden önce olmalı.',
  salesEndAtLocal: 'Satış bitişi geçersiz: seans başlangıcından sonra olamaz.',
  salesMode: 'Satış modu geçersiz.',
  holdTtlSeconds: 'Hold süresi platformun izin verdiği aralıkta olmalı.',
};

function NewSessionForm({ orgId, eventId, disabled }: { orgId: string; eventId: string; disabled: boolean }) {
  const userKey = useUserKey();
  const navigate = useNavigate();
  const queryClient = useQueryClient();
  const venues = useQuery({
    queryKey: userKey('org', orgId, 'venues'),
    queryFn: () => api.get<Venue[]>(`/api/organizer/organizations/${orgId}/venues`),
  });
  const [draft, setDraft] = useState<SessionDraft>({
    venueId: '', startsAtLocal: '', salesStartAtLocal: '', salesEndAtLocal: '', salesMode: 'ReservedSeating', holdTtlSeconds: '',
  });
  const [errors, setErrors] = useState<SessionDraftErrors>({});
  const action = useAction();

  if (venues.isPending) return <Loading label="Mekanlar yükleniyor…" />;
  if (venues.isError) return <ErrorState error={venues.error} onRetry={() => void venues.refetch()} />;
  if (venues.data.length === 0) {
    return (
      <EmptyState title="Seans eklemek için önce bir mekan gerekli.">
        <p><Link className="btn" to={orgPath(orgId, 'mekanlar')}>Mekan ekle</Link></p>
      </EmptyState>
    );
  }

  const venueId = draft.venueId || venues.data[0]!.id;
  const venue = venues.data.find((v) => v.id === venueId);
  const set = (patch: Partial<SessionDraft>) => setDraft({ ...draft, ...patch });

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const next: SessionDraftErrors = {};
    if (!draft.startsAtLocal) next.startsAtLocal = 'Seansın yerel başlangıç saatini seç.';
    const ttl = draft.holdTtlSeconds.trim();
    if (ttl && !/^\d+$/.test(ttl)) next.holdTtlSeconds = 'Hold süresini saniye olarak tam sayı yaz.';
    setErrors(next);
    if (Object.keys(next).length > 0) return;
    void action.run('create', async () => {
      try {
        const created = await api.post<OrganizerSession>(`/api/organizer/events/${eventId}/sessions`, {
          venueId,
          startsAtLocal: draft.startsAtLocal,
          salesStartAtLocal: draft.salesStartAtLocal || null,
          salesEndAtLocal: draft.salesEndAtLocal || null,
          salesMode: draft.salesMode,
          holdTtlSeconds: ttl ? Number(ttl) : null,
        });
        queryClient.setQueryData(userKey('org-session', created.id), created);
        void queryClient.invalidateQueries({ queryKey: userKey('org', orgId, 'events') });
        void queryClient.invalidateQueries({ queryKey: userKey('org-event', eventId) });
        navigate(orgPath(orgId, `seans/${created.id}`));
      } catch (error) {
        setErrors(serverFieldErrors(error, SESSION_FIELD_MESSAGES));
        throw error;
      }
    });
  };

  return (
    <form className="form form-wide" onSubmit={submit} noValidate>
      <ErrorAlert error={action.error} />
      <div className="form-grid">
        <SelectField label="Mekan" value={venueId} onChange={(value) => set({ venueId: value })} error={errors.venueId} disabled={disabled}
          hint={venue ? `Saat dilimi: ${venue.timezone}` : undefined}>
          {venues.data.map((v) => <option key={v.id} value={v.id}>{v.name} — {v.city}</option>)}
        </SelectField>
        <SelectField label="Satış modu" value={draft.salesMode} onChange={(value) => set({ salesMode: value as SalesMode })} error={errors.salesMode}
          disabled={disabled} hint="Mod sonradan değiştirilemez.">
          <option value="ReservedSeating">{SALES_MODE_LABELS.ReservedSeating}</option>
          <option value="GeneralAdmission">{SALES_MODE_LABELS.GeneralAdmission}</option>
        </SelectField>
        <TextField label="Başlangıç (mekanın yerel saati)" type="datetime-local" value={draft.startsAtLocal}
          onChange={(value) => set({ startsAtLocal: value })} error={errors.startsAtLocal} disabled={disabled} required />
        <TextField label="Hold süresi (saniye, isteğe bağlı)" inputMode="numeric" value={draft.holdTtlSeconds}
          onChange={(value) => set({ holdTtlSeconds: value })} error={errors.holdTtlSeconds} disabled={disabled}
          hint="Boş bırakılırsa platform varsayılanı (10 dakika) kullanılır." />
        <TextField label="Satış başlangıcı (isteğe bağlı)" type="datetime-local" value={draft.salesStartAtLocal}
          onChange={(value) => set({ salesStartAtLocal: value })} error={errors.salesStartAtLocal} disabled={disabled}
          hint="Boş bırakılırsa satış hemen açılır." />
        <TextField label="Satış bitişi (isteğe bağlı)" type="datetime-local" value={draft.salesEndAtLocal}
          onChange={(value) => set({ salesEndAtLocal: value })} error={errors.salesEndAtLocal} disabled={disabled}
          hint="Boş bırakılırsa seans başlangıcında kapanır." />
      </div>
      <div className="row-actions">
        <button type="submit" className="btn btn-primary" disabled={disabled || action.busy !== null}>
          {action.busy ? 'Seans oluşturuluyor…' : 'Taslak seans oluştur'}
        </button>
      </div>
    </form>
  );
}

export function EventPage() {
  const { orgId = '', eventId = '' } = useParams();
  const userKey = useUserKey();
  const queryClient = useQueryClient();
  const query = useQuery({
    queryKey: userKey('org-event', eventId),
    queryFn: () => api.get<OrganizerEvent>(`/api/organizer/events/${eventId}`),
  });
  const action = useAction();

  if (query.isPending) return <Loading label="Etkinlik yükleniyor…" />;
  if (query.isError) return <ErrorState error={query.error} onRetry={() => void query.refetch()} notFoundText="Bu etkinlik yok ya da organizasyonuna ait değil." />;
  const event = query.data;
  // Başka organizasyonumun etkinliği bu organizasyonun adresi altında gösterilmez.
  if (event.organizationId !== orgId) return <ErrorState error={notFoundError()} notFoundText="Bu etkinlik bu organizasyona ait değil." />;

  const refresh = () => {
    void queryClient.invalidateQueries({ queryKey: userKey('org-event', eventId) });
    void queryClient.invalidateQueries({ queryKey: userKey('org', orgId, 'events') });
  };

  const save = async (values: EventValues) => {
    const updated = await api.put<OrganizerEvent>(`/api/organizer/events/${eventId}`, values);
    queryClient.setQueryData(userKey('org-event', eventId), updated);
    void queryClient.invalidateQueries({ queryKey: userKey('org', orgId, 'events') });
    return 'Etkinlik kaydedildi.';
  };

  const transition = (name: 'publish' | 'unpublish') =>
    void action.run(name, async () => {
      await api.post(`/api/organizer/events/${eventId}/${name}`);
      refresh();
      return name === 'publish' ? 'Etkinlik yayınlandı.' : 'Etkinlik taslağa alındı.';
    });

  const cancelled = event.status === 'Cancelled';
  return (
    <>
      <Breadcrumb orgId={orgId} tail="Etkinlik" />
      <PageHeading title={event.title} />
      <ul className="chips" aria-label="Etkinlik özellikleri">
        <li className="chip">{EVENT_STATUS_LABELS[event.status]}</li>
        <li className="chip">{CATEGORY_LABELS[event.category]}</li>
        {event.isDemo && <li><DemoTag /></li>}
      </ul>
      {event.moderationBlocked && (
        <div className="note-box is-err" role="status">
          <Status tone="err" icon="warn">Platform yönetimi tarafından yayından kaldırıldı</Status>
          <p>Etkinlik keşifte görünmez ve yeniden yayınlanamaz.</p>
        </div>
      )}

      <div className="blk">
        <h2>Yayın</h2>
        <p className="note">
          {event.status === 'Draft' ? 'Taslak etkinlik keşifte görünmez. Seansların yayınlanabilmesi için önce etkinlik yayınlanmalı.'
            : event.status === 'Published' ? 'Etkinlik yayında. Yayınlanmış seansı olan etkinlik taslağa alınamaz.'
              : 'Etkinlik iptal edildi.'}
        </p>
        <ErrorAlert error={action.error} />
        {action.done && <OkNote>{action.done}</OkNote>}
        <div className="row-actions" style={{ marginTop: 10 }}>
          {event.status === 'Draft' && (
            <button type="button" className="btn btn-primary" onClick={() => transition('publish')} disabled={action.busy !== null}>
              {action.busy === 'publish' ? 'Yayınlanıyor…' : 'Etkinliği yayınla'}
            </button>
          )}
          {event.status === 'Published' && (
            <button type="button" className="btn" onClick={() => transition('unpublish')} disabled={action.busy !== null}>
              {action.busy === 'unpublish' ? 'Taslağa alınıyor…' : 'Taslağa al'}
            </button>
          )}
        </div>
      </div>

      <div className="blk">
        <h2>Seanslar</h2>
        <SessionLinks orgId={orgId} eventTitle={event.title} sessions={event.sessions} />
        <div className="sub">
          <h3>Yeni seans</h3>
          {cancelled
            ? <p className="note">İptal edilmiş etkinliğe seans eklenemez.</p>
            : <NewSessionForm orgId={orgId} eventId={eventId} disabled={false} />}
        </div>
      </div>

      <div className="blk">
        <h2>Etkinlik bilgileri</h2>
        {cancelled && <p className="note">İptal edilmiş etkinlik düzenlenemez.</p>}
        <EventForm
          key={event.id}
          initial={{ title: event.title, description: event.description, category: event.category }}
          submitLabel="Değişiklikleri kaydet" busyLabel="Kaydediliyor…" disabled={cancelled} onSubmit={save}
        />
      </div>
    </>
  );
}
