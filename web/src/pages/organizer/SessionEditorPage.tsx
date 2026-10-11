import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useUserKey } from '../../auth/AuthContext';
import { ConfirmPanel, DemoTag, ErrorAlert, ErrorState, Loading, OkNote, PageHeading, Status } from '../../components/ui';
import { api, IdempotentAction, isApiError, isOutcomeUnknown, runIdempotent } from '../../lib/api';
import { describeError } from '../../lib/errors';
import { formatInZone, formatMoney, moneyInputValue, parseMoneyInput, toLocalInput } from '../../lib/format';
import type { Layout, OrganizerCategory, OrganizerSession, SessionCancellation, Venue } from '../../lib/types';
import { GateStaffAssign } from './GateStaffAssign';
import { Breadcrumb, orgPath, SelectField, serverFieldErrors, SessionContext, TextField, useAction } from './shared';

/** Seans DTO'su döndüren her eylemden sonra önbelleği günceller ve türeyen sorguları tazeler. */
function useSessionCache(orgId: string, sessionId: string) {
  const userKey = useUserKey();
  const queryClient = useQueryClient();
  return (updated?: OrganizerSession) => {
    if (updated) queryClient.setQueryData(userKey('org-session', sessionId), updated);
    else void queryClient.invalidateQueries({ queryKey: userKey('org-session', sessionId), exact: true });
    void queryClient.invalidateQueries({ queryKey: userKey('org-session', sessionId, 'summary') });
    void queryClient.invalidateQueries({ queryKey: userKey('org', orgId, 'events') });
    if (updated) void queryClient.invalidateQueries({ queryKey: userKey('org-event', updated.eventId) });
  };
}

interface SectionProps {
  session: OrganizerSession;
  orgId: string;
}

// --- (a) Temel alanlar -------------------------------------------------------------------------

type BasicsErrors = Partial<Record<'venueId' | 'startsAtLocal' | 'salesStartAtLocal' | 'salesEndAtLocal' | 'holdTtlSeconds', string>>;

function BasicsSection({ session, orgId }: SectionProps) {
  const userKey = useUserKey();
  const store = useSessionCache(orgId, session.id);
  const venues = useQuery({
    queryKey: userKey('org', orgId, 'venues'),
    queryFn: () => api.get<Venue[]>(`/api/organizer/organizations/${orgId}/venues`),
  });
  const [venueId, setVenueId] = useState(session.venueId);
  const [startsAt, setStartsAt] = useState(session.startsAtLocal);
  const [salesStart, setSalesStart] = useState(() => toLocalInput(session.salesStartAt, session.timezone));
  const [salesEnd, setSalesEnd] = useState(() => toLocalInput(session.salesEndAt, session.timezone));
  const [ttl, setTtl] = useState(String(session.holdTtlSeconds));
  const [errors, setErrors] = useState<BasicsErrors>({});
  const action = useAction();
  const editable = session.status === 'Draft';

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const next: BasicsErrors = {};
    if (!startsAt) next.startsAtLocal = 'Seansın yerel başlangıç saatini seç.';
    if (ttl.trim() && !/^\d+$/.test(ttl.trim())) next.holdTtlSeconds = 'Hold süresini saniye olarak tam sayı yaz.';
    setErrors(next);
    if (Object.keys(next).length > 0) return;
    void action.run('save', async () => {
      try {
        const updated = await api.put<OrganizerSession>(`/api/organizer/sessions/${session.id}`, {
          venueId,
          startsAtLocal: startsAt,
          salesStartAtLocal: salesStart || null,
          salesEndAtLocal: salesEnd || null,
          holdTtlSeconds: ttl.trim() ? Number(ttl.trim()) : null,
        });
        store(updated);
        return 'Seans bilgileri kaydedildi.';
      } catch (error) {
        setErrors(serverFieldErrors(error, {
          venueId: 'Mekan bu organizasyona ait değil.',
          startsAtLocal: 'Başlangıç geçersiz: gelecekte bir yerel saat seç.',
          salesStartAtLocal: 'Satış başlangıcı geçersiz: satış bitişinden önce olmalı.',
          salesEndAtLocal: 'Satış bitişi geçersiz: seans başlangıcından sonra olamaz.',
          holdTtlSeconds: 'Hold süresi platformun izin verdiği aralıkta olmalı.',
        }));
        throw error;
      }
    });
  };

  if (!editable) {
    return (
      <div className="blk">
        <h2>Seans bilgileri</h2>
        <p className="note">Yalnız taslak seanslar düzenlenebilir. Bu seans {session.status === 'Published' ? 'yayında' : 'iptal edildi'}.</p>
        <dl className="facts">
          <dt>Mekan</dt><dd>{session.venueName}, {session.city}</dd>
          <dt>Başlangıç</dt><dd className="when">{formatInZone(session.startsAt, session.timezone)} · {session.timezone}</dd>
          <dt>Satış penceresi</dt>
          <dd className="when">{formatInZone(session.salesStartAt, session.timezone)} – {formatInZone(session.salesEndAt, session.timezone)}</dd>
          <dt>Hold süresi</dt><dd className="mono">{session.holdTtlSeconds} sn</dd>
        </dl>
      </div>
    );
  }

  return (
    <div className="blk">
      <h2>Seans bilgileri</h2>
      <p className="note">Saatler mekanın saat diliminde ({session.timezone}) girilir. Satış modu oluşturulduktan sonra değişmez.</p>
      <form className="form form-wide" onSubmit={submit} noValidate>
        <ErrorAlert error={action.error} />
        {action.done && <OkNote>{action.done}</OkNote>}
        <div className="form-grid">
          <SelectField label="Mekan" value={venueId} onChange={setVenueId} error={errors.venueId} disabled={venues.isPending}
            hint={session.layoutId ? 'Koltuk planı mevcut mekana bağlı; mekanı değiştirmeden önce planı değiştir.' : undefined}>
            {(venues.data ?? [{ id: session.venueId, name: session.venueName, city: session.city } as Venue]).map((v) => (
              <option key={v.id} value={v.id}>{v.name} — {v.city}</option>
            ))}
          </SelectField>
          <TextField label="Hold süresi (saniye)" inputMode="numeric" value={ttl} onChange={setTtl} error={errors.holdTtlSeconds}
            hint="Boş bırakılırsa platform varsayılanı kullanılır." />
          <TextField className="span-2" label="Başlangıç (mekanın yerel saati)" type="datetime-local" value={startsAt} onChange={setStartsAt}
            error={errors.startsAtLocal} required />
          <TextField label="Satış başlangıcı" type="datetime-local" value={salesStart} onChange={setSalesStart} error={errors.salesStartAtLocal}
            hint="Boş bırakılırsa satış hemen açılır." />
          <TextField label="Satış bitişi" type="datetime-local" value={salesEnd} onChange={setSalesEnd} error={errors.salesEndAtLocal}
            hint="Boş bırakılırsa seans başlangıcında kapanır." />
        </div>
        {venues.isError && <p className="note">Mekan listesi okunamadı; mekan değiştirilemiyor.</p>}
        <div className="row-actions">
          <button type="submit" className="btn btn-primary" disabled={action.busy !== null}>{action.busy ? 'Kaydediliyor…' : 'Bilgileri kaydet'}</button>
        </div>
      </form>
    </div>
  );
}

// --- (b) Koltuk planı ve kademe fiyatları ------------------------------------------------------

function SeatingSection({ session, orgId }: SectionProps) {
  const userKey = useUserKey();
  const store = useSessionCache(orgId, session.id);
  const layouts = useQuery({
    queryKey: userKey('org-venue', session.venueId, 'layouts'),
    queryFn: () => api.get<Layout[]>(`/api/organizer/venues/${session.venueId}/layouts`),
  });
  const [chosen, setChosen] = useState(session.layoutId ?? '');
  const [prices, setPrices] = useState<Record<string, string>>(() =>
    Object.fromEntries(session.tiers.map((t) => [t.tierCode, moneyInputValue(t.priceMinor)])));
  const [errors, setErrors] = useState<Record<string, string>>({});
  const action = useAction();

  if (layouts.isPending) return <div className="blk"><h2>Koltuk planı ve fiyatlar</h2><Loading label="Planlar yükleniyor…" /></div>;
  if (layouts.isError) return <div className="blk"><h2>Koltuk planı ve fiyatlar</h2><ErrorState error={layouts.error} onRetry={() => void layouts.refetch()} /></div>;

  // Seçim mekanın planlarından biri değilse (ör. mekan değişti) en yeni sürüme düşülür.
  const layoutId = layouts.data.some((l) => l.id === chosen) ? chosen : (layouts.data[0]?.id ?? '');
  const layout = layouts.data.find((l) => l.id === layoutId);
  const samePlan = session.layoutId !== null && layoutId === session.layoutId;
  const tierCodes = layout?.tierCodes ?? session.tiers.map((t) => t.tierCode);
  const current = new Map(session.tiers.map((t) => [t.tierCode, t.priceMinor]));
  const cancelled = session.status === 'Cancelled';

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const parsed: Record<string, number> = {};
    const next: Record<string, string> = {};
    for (const tier of tierCodes) {
      const minor = parseMoneyInput(prices[tier] ?? '');
      if (minor === null) next[tier] = 'Fiyatı TL olarak yaz (örnek: 450 ya da 450,00).';
      else parsed[tier] = minor;
    }
    setErrors(next);
    if (Object.keys(next).length > 0) return;
    void action.run('save', async () => {
      try {
        if (samePlan) {
          // Yalnız değişen kademeler gönderilir: tutulmuş/satılmış koltuğu olan kademe yeniden fiyatlanamaz.
          const changed = Object.fromEntries(Object.entries(parsed).filter(([tier, minor]) => current.get(tier) !== minor));
          if (Object.keys(changed).length === 0) return 'Fiyatlarda değişiklik yok.';
          store(await api.put<OrganizerSession>(`/api/organizer/sessions/${session.id}/tier-prices`, { tierPrices: changed }));
          return 'Kademe fiyatları güncellendi.';
        }
        store(await api.put<OrganizerSession>(`/api/organizer/sessions/${session.id}/seating`, { layoutId, tierPrices: parsed }));
        return 'Koltuk planı ve fiyatlar kaydedildi.';
      } catch (error) {
        const locked = isApiError(error, 'price_locked') && Array.isArray(error.problem.tiers) ? (error.problem.tiers as unknown[]) : [];
        setErrors(Object.fromEntries(locked.filter((t): t is string => typeof t === 'string')
          .map((tier) => [tier, 'Bu kademede tutulmuş veya satılmış koltuk var; fiyat değiştirilemez.'])));
        throw error;
      }
    });
  };

  return (
    <div className="blk">
      <h2>Koltuk planı ve fiyatlar</h2>
      {session.seats ? (
        <p className="note">
          Mevcut envanter: {session.seats.total} koltuk · {session.seats.sold} satıldı · {session.seats.held} tutuluyor · {session.seats.available} boş.
          Rezervasyon oluştuktan sonra plan değiştirilemez.
        </p>
      ) : <p className="note">Bu seans için henüz plan seçilmedi. Yayınlamak için plan ve her kademe için fiyat gerekir.</p>}
      {layouts.data.length === 0 ? (
        <div className="state">
          <p className="state-title">Mekanın koltuk planı yok.</p>
          <p><Link className="btn" to={orgPath(orgId, 'mekanlar')}>Mekanlar sayfasında plan oluştur</Link></p>
        </div>
      ) : (
        <form className="form form-wide" onSubmit={submit} noValidate>
          <ErrorAlert error={action.error} />
          {action.done && <OkNote>{action.done}</OkNote>}
          <SelectField label="Plan sürümü" value={layoutId} onChange={(value) => { setChosen(value); setErrors({}); }} disabled={cancelled}>
            {layouts.data.map((l) => <option key={l.id} value={l.id}>v{l.version} · {l.name} · {l.seatCount} koltuk</option>)}
          </SelectField>
          <fieldset>
            <legend className="lbl">Kademe fiyatları (TL)</legend>
            <div className="form-grid" style={{ marginTop: 6 }}>
              {tierCodes.map((tier) => (
                <TextField key={tier} label={`Kademe ${tier}`} inputMode="decimal" value={prices[tier] ?? ''} disabled={cancelled}
                  onChange={(value) => setPrices({ ...prices, [tier]: value })} error={errors[tier]}
                  hint={current.has(tier) && samePlan ? `Şu an: ${formatMoney(current.get(tier)!)}` : undefined} />
              ))}
            </div>
          </fieldset>
          <div className="row-actions">
            <button type="submit" className="btn btn-primary" disabled={cancelled || action.busy !== null}>
              {action.busy ? 'Kaydediliyor…' : samePlan ? 'Fiyatları güncelle' : 'Planı ve fiyatları kaydet'}
            </button>
          </div>
        </form>
      )}
    </div>
  );
}

// --- (c) Genel giriş kategorileri --------------------------------------------------------------

type CategoryErrors = Partial<Record<'name' | 'priceMinor' | 'capacity', string>>;

function CategoryForm({ session, orgId, category }: SectionProps & { category?: OrganizerCategory }) {
  const store = useSessionCache(orgId, session.id);
  const [name, setName] = useState(category?.name ?? '');
  const [price, setPrice] = useState(category ? moneyInputValue(category.priceMinor) : '');
  const [capacity, setCapacity] = useState(category ? String(category.capacity) : '');
  const [errors, setErrors] = useState<CategoryErrors>({});
  const action = useAction();
  const cancelled = session.status === 'Cancelled';

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const next: CategoryErrors = {};
    const priceMinor = parseMoneyInput(price);
    const cap = /^\d+$/.test(capacity.trim()) ? Number(capacity.trim()) : null;
    if (name.trim().length < 2 || name.trim().length > 80) next.name = 'Kategori adı 2–80 karakter olmalı.';
    if (priceMinor === null) next.priceMinor = 'Fiyatı TL olarak yaz (örnek: 450 ya da 450,00).';
    if (cap === null || cap < 1 || cap > 100_000) next.capacity = 'Kapasite 1–100000 arasında tam sayı olmalı.';
    setErrors(next);
    if (Object.keys(next).length > 0 || priceMinor === null || cap === null) return;
    void action.run('save', async () => {
      try {
        const body = { name: name.trim(), priceMinor, capacity: cap };
        if (category) {
          store(await api.put<OrganizerSession>(`/api/organizer/sessions/${session.id}/categories/${category.id}`, body));
          return 'Kategori güncellendi.';
        }
        store(await api.post<OrganizerSession>(`/api/organizer/sessions/${session.id}/categories`, body));
        setName('');
        setPrice('');
        setCapacity('');
        return 'Kategori eklendi.';
      } catch (error) {
        const mapped = serverFieldErrors<'name' | 'priceMinor' | 'capacity'>(error, {
          name: 'Kategori adı geçersiz.', priceMinor: 'Fiyat geçersiz.', capacity: 'Kapasite geçersiz.',
        });
        if (isApiError(error, 'conflict')) mapped.name = 'Bu adla bir kategori zaten var.';
        if (isApiError(error, 'capacity_below_committed')) mapped.capacity = describeError(error);
        setErrors(mapped);
        throw error;
      }
    });
  };

  const title = category ? `${category.name} kategorisi` : 'Yeni kategori';
  return (
    <form className="box" onSubmit={submit} noValidate aria-label={title}>
      {category && (
        <p className="note" style={{ marginBottom: 8 }}>
          <strong style={{ color: 'var(--ink)' }}>{category.name}</strong> · {category.sold} satıldı · {category.held} tutuluyor · {category.available} boş
        </p>
      )}
      {/* Alan hatası zaten alanın altında gösterilir; burada yalnız alanla eşleşmeyen hatalar duyurulur. */}
      <ErrorAlert error={action.error !== null && Object.keys(errors).length === 0 ? action.error : null} />
      {action.done && <OkNote>{action.done}</OkNote>}
      <div className="inline-form">
        <TextField label="Kategori adı" value={name} onChange={setName} error={errors.name} maxLength={80} disabled={cancelled} />
        <TextField label="Fiyat (TL)" inputMode="decimal" value={price} onChange={setPrice} error={errors.priceMinor} disabled={cancelled} />
        <TextField label="Kapasite" inputMode="numeric" value={capacity} onChange={setCapacity} error={errors.capacity} disabled={cancelled} />
        <button type="submit" className="btn" disabled={cancelled || action.busy !== null}>
          {action.busy ? 'Kaydediliyor…' : category ? 'Kaydet' : 'Kategori ekle'}
          {category && <span className="sr-only"> — {category.name}</span>}
        </button>
      </div>
    </form>
  );
}

function CategoriesSection({ session, orgId }: SectionProps) {
  return (
    <div className="blk">
      <h2>Bilet kategorileri</h2>
      <p className="note">Numarasız (genel giriş) seans. Kapasite, tutulmuş ve satılmış bilet sayısının altına indirilemez.</p>
      {session.categories.length === 0 && <p className="note">Henüz kategori yok. Yayınlamak için en az bir kategori gerekir.</p>}
      <div data-testid="categories">
        {session.categories.map((category) => <CategoryForm key={category.id} session={session} orgId={orgId} category={category} />)}
      </div>
      {session.status !== 'Cancelled' && session.categories.length < 10 && (
        <div className="sub">
          <h3>Yeni kategori</h3>
          <CategoryForm session={session} orgId={orgId} />
        </div>
      )}
    </div>
  );
}

// --- (d) Yayın, (e) demo simülatörü ------------------------------------------------------------

function PublishSection({ session, orgId }: SectionProps) {
  const store = useSessionCache(orgId, session.id);
  const action = useAction();
  const publish = () =>
    void action.run('publish', async () => {
      store(await api.post<OrganizerSession>(`/api/organizer/sessions/${session.id}/publish`));
      return 'Seans yayınlandı.';
    });
  return (
    <div className="blk">
      <h2>Yayın</h2>
      <p className="note">
        {session.status === 'Draft' ? 'Taslak seans keşifte görünmez ve satılamaz. Yayın için: etkinlik yayında, başlangıç gelecekte ve envanter tanımlı olmalı.'
          : session.status === 'Published' ? 'Seans yayında. Yayınlanmış seans taslağa dönmez; gerekirse iptal edilir.'
            : 'Seans iptal edildi; yeniden yayınlanamaz.'}
      </p>
      <ErrorAlert error={action.error} />
      {action.done && <OkNote>{action.done}</OkNote>}
      {session.status === 'Draft' && (
        <div className="row-actions" style={{ marginTop: 10 }}>
          <button type="button" className="btn btn-primary" onClick={publish} disabled={action.busy !== null}>
            {action.busy ? 'Yayınlanıyor…' : 'Yayınla'}
          </button>
        </div>
      )}
    </div>
  );
}

function DemoSimulatorSection({ session, orgId }: SectionProps) {
  const store = useSessionCache(orgId, session.id);
  const action = useAction();
  const enabled = session.demoSimulatorEnabled;
  const toggle = () =>
    void action.run('toggle', async () => {
      store(await api.put<OrganizerSession>(`/api/organizer/sessions/${session.id}/demo-simulator`, { enabled: !enabled }));
      return enabled ? 'Demo simülatörü kapatıldı.' : 'Demo simülatörü açıldı.';
    });
  return (
    <div className="blk">
      <fieldset className="demo-set">
        <legend><DemoTag /> Demo simülatörü</legend>
        <p>
          {enabled
            ? <Status tone="info" icon="info">Açık</Status>
            : <Status tone="mute" icon="planned">Kapalı</Status>}
        </p>
        <p className="note" style={{ marginTop: 6 }}>
          Açıkken müşteriler ödeme adımında sağlayıcı senaryosu (başarı, ret, belirsiz, asenkron, geç başarı) seçebilir. Kapalıyken yalnız
          varsayılan başarı senaryosu kabul edilir. Kurulumda simülatör kapalıysa açılamaz.
        </p>
        <ErrorAlert error={isApiError(action.error, 'conflict') ? 'Demo simülatörü bu kurulumda kapalı; seans bazında açılamaz.' : action.error} />
        {action.done && <OkNote>{action.done}</OkNote>}
        <div className="row-actions" style={{ marginTop: 10 }}>
          <button type="button" className="btn" onClick={toggle} disabled={action.busy !== null} aria-pressed={enabled}>
            {action.busy ? 'Kaydediliyor…' : enabled ? 'Demo simülatörünü kapat' : 'Demo simülatörünü aç'}
          </button>
        </div>
      </fieldset>
    </div>
  );
}

// --- (g) Seans iptali --------------------------------------------------------------------------

function CancelSection({ session, orgId }: SectionProps) {
  const userKey = useUserKey();
  const store = useSessionCache(orgId, session.id);
  const [confirming, setConfirming] = useState(false);
  const [cancelAction] = useState(() => new IdempotentAction());
  const action = useAction();
  const cancelled = session.status === 'Cancelled';
  const job = useQuery({
    queryKey: userKey('org-session', session.id, 'cancellation'),
    queryFn: () => api.get<SessionCancellation>(`/api/organizer/sessions/${session.id}/cancellation`),
    enabled: cancelled,
    // İade işi arka planda sipariş sipariş ilerler; bitene kadar yoklanır.
    refetchInterval: (query) => (query.state.data?.jobStatus === 'Pending' ? 2000 : false),
  });

  const cancel = () =>
    void action.run('cancel', async () => {
      // Tek Idempotency-Key: ağ hatası / 503'te aynı anahtarla yeniden denenir.
      await runIdempotent(cancelAction, session.id, (key) =>
        api.post<SessionCancellation>(`/api/organizer/sessions/${session.id}/cancel`, undefined, { idempotencyKey: key }));
      setConfirming(false);
      store();
    }).then((ok) => {
      // Kesin ret (ör. zaten iptal): onay kapanır ve gerçek durum yeniden okunur.
      if (!ok) store();
    });

  return (
    <div className="blk">
      <h2>Seans iptali</h2>
      {cancelled ? (
        <div role="status" data-testid="cancellation-job">
          <p><Status tone="err" icon="cross">Seans iptal edildi</Status></p>
          {job.isPending ? <p className="note">İptal işi durumu okunuyor…</p>
            : job.isError ? <p className="note">İptal işi durumu okunamadı: {describeError(job.error)}</p>
              : (
                <>
                  <p style={{ marginTop: 6 }}>
                    {job.data.jobStatus === 'Completed'
                      ? <Status tone="ok" icon="check">İptal işi tamamlandı</Status>
                      : <Status tone="info" icon="dots">İptal işi sürüyor</Status>}
                  </p>
                  <p className="note">
                    İşlenen rezervasyon: <span className="mono">{job.data.processedCount}</span> · başlatılan iade:{' '}
                    <span className="mono">{job.data.refundCount}</span>. İadelerin sonucu Operasyon kuyruğunda izlenir.
                  </p>
                </>
              )}
        </div>
      ) : (
        <>
          <p className="note">
            İptal geri alınamaz: seans yeniden satışa açılmaz, tüm biletler geçersiz olur ve ödenmiş siparişler için iade başlatılır.
          </p>
          {action.error !== null && (
            <ErrorAlert error={isOutcomeUnknown(action.error)
              ? 'Sunucuya ulaşılamadı. İptalin işlenip işlenmediği bilinmiyor; yeniden denediğinde aynı istek güvenle tekrarlanır.'
              : action.error} />
          )}
          {confirming ? (
            <ConfirmPanel title="Seans iptal edilsin mi?" confirmLabel="Evet, seansı iptal et" onConfirm={cancel}
              onCancel={() => setConfirming(false)} busy={action.busy !== null}>
              <p>
                “{session.eventTitle}” — {formatInZone(session.startsAt, session.timezone)} seansı kalıcı olarak iptal edilir. Satılmış tüm biletler
                geçersiz olur ve iadeler başlatılır. Bu işlem geri alınamaz.
              </p>
            </ConfirmPanel>
          ) : (
            <div className="row-actions" style={{ marginTop: 10 }}>
              <button type="button" className="btn btn-danger" onClick={() => { setConfirming(true); action.reset(); }}>Seansı iptal et</button>
            </div>
          )}
        </>
      )}
    </div>
  );
}

// --- Sayfa -------------------------------------------------------------------------------------

export function SessionEditorPage() {
  const { orgId = '', sessionId = '' } = useParams();
  const userKey = useUserKey();
  const query = useQuery({
    queryKey: userKey('org-session', sessionId),
    queryFn: () => api.get<OrganizerSession>(`/api/organizer/sessions/${sessionId}`),
  });

  if (query.isPending) return <Loading label="Seans yükleniyor…" />;
  if (query.isError) {
    return <ErrorState error={query.error} onRetry={() => void query.refetch()} notFoundText="Bu seans yok ya da organizasyonuna ait değil." />;
  }
  const session = query.data;

  return (
    <>
      <Breadcrumb orgId={orgId} eventId={session.eventId} eventTitle={session.eventTitle} tail="Seans" />
      <PageHeading title="Seans" />
      <SessionContext session={session} />
      <p style={{ marginTop: 12 }}>
        <Link className="btn" to={orgPath(orgId, `seans/${session.id}/ozet`)}>Seans özeti</Link>
      </p>

      <BasicsSection key={`basics:${session.id}`} session={session} orgId={orgId} />
      {session.salesMode === 'ReservedSeating'
        ? <SeatingSection key={`seating:${session.id}`} session={session} orgId={orgId} />
        : <CategoriesSection session={session} orgId={orgId} />}
      <PublishSection session={session} orgId={orgId} />
      <DemoSimulatorSection session={session} orgId={orgId} />
      <div className="blk">
        <h2>Kapı ekibi</h2>
        <p className="note">Yalnız bu seansa atanan üyeler kapıda bilet okutabilir. Üye eklemek için Ekip sayfasını kullan.</p>
        <GateStaffAssign sessionId={session.id} />
      </div>
      <CancelSection session={session} orgId={orgId} />
    </>
  );
}
