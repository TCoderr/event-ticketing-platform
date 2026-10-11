import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useMemo, useState } from 'react';
import { Link, useLocation, useNavigate, useParams } from 'react-router-dom';
import { publicKey, useAuth, useUserKey } from '../auth/AuthContext';
import { Icon } from '../components/Icon';
import { Screen } from '../components/Layout';
import { changeQuantity, QuantityPicker, totalQuantity } from '../components/QuantityPicker';
import { seatLabel, SeatLegend, SeatPicker, toggleSeat } from '../components/SeatPicker';
import { DemoTag, ErrorState, Loading, PageHeading, SessionTime, Status } from '../components/ui';
import { api, ApiError, IdempotentAction, isOutcomeUnknown, runIdempotent } from '../lib/api';
import { describeError } from '../lib/errors';
import { CATEGORY_LABELS, formatInZone, formatMoney, SALES_MODE_LABELS } from '../lib/format';
import { stamp } from '../lib/stamp';
import type { Hold, PublicCategory, PublicSeat, PublicSeatMap, PublicSessionDetail } from '../lib/types';

/** Giriş ekranına gidip dönerken taşınan seçim. */
interface CarriedSelection {
  seats?: string[];
  quantities?: Record<string, number>;
}

const POLL_MS = 5000; // Müsaitlik ipucudur; plan ~5 sn'de bir yenilenir.

export function SessionPage() {
  const { id = '' } = useParams();
  const { user } = useAuth();
  const userKey = useUserKey();
  const navigate = useNavigate();
  const location = useLocation();
  const queryClient = useQueryClient();
  const carried = (location.state ?? {}) as CarriedSelection;

  const detail = useQuery({
    queryKey: publicKey('session', id),
    queryFn: () => api.get<PublicSessionDetail>(`/api/catalog/sessions/${id}`),
    refetchInterval: (query) => (query.state.error ? false : POLL_MS),
  });
  const seated = detail.data?.salesMode === 'ReservedSeating';
  const seatMap = useQuery({
    queryKey: publicKey('seats', id),
    queryFn: () => api.get<PublicSeatMap>(`/api/catalog/sessions/${id}/seats`),
    enabled: seated,
    refetchInterval: (query) => (query.state.error ? false : POLL_MS),
  });
  const myHold = useQuery({
    queryKey: userKey('my-hold', id),
    queryFn: async () => (await api.get<Hold | undefined>(`/api/sessions/${id}/my-hold`)) ?? null,
    enabled: Boolean(user) && detail.isSuccess,
  });

  const [picked, setPicked] = useState<string[]>(() => (Array.isArray(carried.seats) ? carried.seats : []));
  const [quantities, setQuantities] = useState<Record<string, number>>(() => carried.quantities ?? {});
  const [message, setMessage] = useState<string | null>(null);
  const [announcement, setAnnouncement] = useState('');
  const [busy, setBusy] = useState(false);
  const [holdAction] = useState(() => new IdempotentAction());

  const seats = useMemo(() => seatMap.data?.seats ?? [], [seatMap.data]);
  const seatsById = useMemo(() => new Map(seats.map((s) => [s.id, s])), [seats]);
  // Yoklama sırasında başkasına geçen koltuklar seçimden düşer; kullanıcıya açıkça söylenir.
  const selected = useMemo(() => picked.filter((seatId) => seatsById.get(seatId)?.available === true), [picked, seatsById]);
  const lost = useMemo(
    () => picked.map((seatId) => seatsById.get(seatId)).filter((s): s is PublicSeat => s !== undefined && !s.available),
    [picked, seatsById],
  );

  if (detail.isPending) return <Screen><Loading label="Seans yükleniyor…" /></Screen>;
  if (detail.isError) {
    return (
      <Screen>
        <ErrorState error={detail.error} onRetry={() => void detail.refetch()}
          notFoundText="Bu seans yayında değil: iptal edilmiş, başlamış ya da kaldırılmış olabilir." />
      </Screen>
    );
  }

  const session = detail.data;
  const max = session.maxTicketsPerHold;
  const categories = session.categories;
  const gaLines = categories.filter((c) => (quantities[c.id] ?? 0) > 0);
  const count = seated ? selected.length : totalQuantity(quantities);
  const total = seated
    ? selected.reduce((sum, seatId) => sum + (seatsById.get(seatId)?.priceMinor ?? 0), 0)
    : gaLines.reduce((sum, c) => sum + c.priceMinor * (quantities[c.id] ?? 0), 0);
  const soldOut = seated ? session.seats?.available === 0 : categories.every((c) => c.available <= 0);
  const open = session.salesState === 'Open';
  const activeHold = myHold.data ?? null;

  const onToggleSeat = (seat: PublicSeat, want?: boolean) => {
    const result = toggleSeat(selected, seat, max, want);
    setPicked(result.selected);
    setMessage(result.message);
    if (result.announcement) {
      const sum = result.selected.reduce((t, seatId) => t + (seatsById.get(seatId)?.priceMinor ?? 0), 0);
      setAnnouncement(`${result.announcement} ${result.selected.length} koltuk, toplam ${formatMoney(sum)}.`);
    }
  };

  const onChangeQuantity = (category: PublicCategory, delta: 1 | -1) => {
    const result = changeQuantity(quantities, category, delta, max);
    setQuantities(result.quantities);
    setMessage(result.message);
  };

  const submit = async () => {
    if (busy) return;
    if (activeHold) {
      setMessage('Bu seansta zaten süresi dolmamış bir rezervasyonun var. Ona devam et ya da önce onu bırak.');
      return;
    }
    if (count === 0) {
      setMessage(seated ? 'Önce en az bir koltuk seç.' : 'Önce en az bir bilet seç.');
      return;
    }
    if (!user) {
      // Girişten sonra aynı seansa, aynı seçimle dönülür.
      navigate('/giris', { state: { from: location.pathname, carry: { seats: selected, quantities } satisfies CarriedSelection } });
      return;
    }
    const body = seated
      ? { seatIds: selected }
      : { items: gaLines.map((c) => ({ categoryId: c.id, quantity: quantities[c.id] ?? 0 })) };
    setBusy(true);
    setMessage(null);
    try {
      // Aynı seçim için tek Idempotency-Key: ağ hatası / 503'te aynı anahtarla yeniden denenir.
      const hold = await runIdempotent(holdAction, JSON.stringify(body), (key) =>
        api.post<Hold>(`/api/sessions/${id}/holds`, body, { idempotencyKey: key }));
      queryClient.setQueryData(userKey('hold', hold.id), stamp(hold));
      void queryClient.invalidateQueries({ queryKey: userKey('my-hold', id) });
      navigate(`/hold/${hold.id}`);
    } catch (error) {
      setBusy(false);
      onHoldError(error);
    }
  };

  const onHoldError = (error: unknown) => {
    if (isOutcomeUnknown(error)) {
      setMessage('Sunucuya ulaşılamadı. Rezervasyonun oluşup oluşmadığı bilinmiyor; yeniden denediğinde aynı istek güvenle tekrarlanır.');
      return;
    }
    if (error instanceof ApiError) {
      if (error.code === 'seats_unavailable') {
        const ids = Array.isArray(error.problem.seatIds) ? (error.problem.seatIds as unknown[]).filter((v): v is string => typeof v === 'string') : [];
        const labels = ids.map((seatId) => seatsById.get(seatId)).filter((s): s is PublicSeat => s !== undefined).map(seatLabel);
        setPicked(selected.filter((seatId) => !ids.includes(seatId)));
        setMessage(
          `${labels.length > 0 ? labels.join(', ') : 'Seçtiğin koltuklardan bazıları'} az önce başkası tarafından alındı. ` +
          'Hiçbir koltuk tutulmadı; dolu olanlar seçimden çıkarıldı ve plan yenilendi. Kalan seçimle yeniden deneyebilirsin.',
        );
        void seatMap.refetch();
        void detail.refetch();
        return;
      }
      if (error.code === 'insufficient_capacity') {
        const rows = Array.isArray(error.problem.categories) ? (error.problem.categories as { categoryId?: string; available?: number }[]) : [];
        const next = { ...quantities };
        const parts: string[] = [];
        for (const row of rows) {
          if (typeof row.categoryId !== 'string' || typeof row.available !== 'number') continue;
          const name = categories.find((c) => c.id === row.categoryId)?.name ?? 'Kategori';
          if ((next[row.categoryId] ?? 0) > row.available) {
            next[row.categoryId] = row.available;
            parts.push(`${name}: ${row.available} bilet kaldı`);
          }
        }
        setQuantities(next);
        setMessage(`Yeterli bilet kalmadı${parts.length > 0 ? ` (${parts.join('; ')})` : ''}. Hiçbir bilet tutulmadı; adetler güncellendi. Yeniden deneyebilirsin.`);
        void detail.refetch();
        return;
      }
      if (error.code === 'active_hold_exists') {
        void myHold.refetch();
        setMessage('Bu seansta zaten süresi dolmamış bir rezervasyonun var. Ona devam et ya da önce onu bırak.');
        return;
      }
      if (error.code === 'unauthenticated') {
        navigate('/giris', { state: { from: location.pathname, carry: { seats: selected, quantities } satisfies CarriedSelection } });
        return;
      }
      void detail.refetch();
    }
    setMessage(describeError(error));
  };

  const holdLabel = seated ? 'Koltukları tut' : 'Biletleri tut';
  const blocked = busy || activeHold !== null;

  return (
    <>
      <section className={`screen${open && !soldOut ? '' : ' screen-pad'}`}>
        <div className="wrap">
          <p className="eyebrow">
            {session.isDemo && <DemoTag label="Demo verisi" />}
            <span>
              <Link to="/">Etkinlikler</Link> / <Link to={`/etkinlik/${session.eventSlug}`}>{CATEGORY_LABELS[session.category]}</Link> ·{' '}
              {SALES_MODE_LABELS[session.salesMode]}
            </span>
          </p>
          <PageHeading title={session.eventTitle} />
          <p className="ctx">
            <SessionTime startsAt={session.startsAt} timezone={session.timezone} />
            <span className="venue">{session.venueName}, {session.city} · {session.venueAddress}</span>
            <span className="venue">Organizatör: {session.organizerName}</span>
          </p>

          {activeHold && (
            <div className="note-box" role="status">
              <p>
                <strong>Bu seansta süresi dolmamış bir rezervasyonun var.</strong>{' '}
                {activeHold.ticketCount} bilet · {formatMoney(activeHold.totalMinor)}
              </p>
              <p style={{ marginTop: 8 }}>
                <Link className="btn btn-primary" to={activeHold.orderId ? `/siparis/${activeHold.orderId}` : `/hold/${activeHold.id}`}>
                  {activeHold.orderId ? 'Sipariş durumuna git' : 'Ödemeye devam et'}<Icon name="arrow" />
                </Link>
              </p>
            </div>
          )}

          {!open && (
            <div className="state">
              <p>
                {session.salesState === 'NotStarted'
                  ? <Status tone="info" icon="clock">Satış {formatInZone(session.salesStartAt, session.timezone)} itibarıyla açılır</Status>
                  : <Status tone="mute" icon="cross">Satış kapandı</Status>}
              </p>
              <p className="note">
                {session.salesState === 'NotStarted'
                  ? 'Satış başladığında bu sayfadan seçim yapabilirsin. Satışın açılıp açılmadığına sunucu saati karar verir.'
                  : `Bu seansın satışı ${formatInZone(session.salesEndAt, session.timezone)} itibarıyla kapandı.`}
              </p>
              <PriceList session={session} />
            </div>
          )}

          {open && soldOut && (
            <div className="state">
              <p><Status tone="mute" icon="cross">Tükendi</Status></p>
              <p className="note">Şu anda boş yer görünmüyor. Süresi dolan rezervasyonlar yeniden satışa açılır; plan kendiliğinden yenilenir.</p>
            </div>
          )}

          {open && seated && (
            seatMap.isPending ? <Loading label="Salon planı yükleniyor…" />
              : seatMap.isError ? <ErrorState error={seatMap.error} onRetry={() => void seatMap.refetch()} />
                : (
                  <>
                    <SeatLegend max={max} />
                    <SeatPicker seats={seats} selected={selected} onToggle={onToggleSeat} disabled={blocked} />
                  </>
                )
          )}

          {open && !seated && !soldOut && (
            <>
              <p className="lede">Numarasız seans. Kategori başına adet seç; tek siparişte en fazla {max} bilet.</p>
              <QuantityPicker categories={categories} quantities={quantities} max={max} onChange={onChangeQuantity} disabled={blocked} />
              <p className="note" style={{ marginTop: 12 }}>Kalan adetler ipucudur; kesin sonuç “Biletleri tut” adımında belli olur.</p>
            </>
          )}
        </div>
      </section>

      {open && !soldOut && (
        <div className="sumbar" role="region" aria-label="Seçim özeti">
          <div className="wrap sumbar-in">
            <div className="sum-sel">
              <span className="sum-k">{seated ? 'Seçilen koltuklar' : 'Seçilen biletler'} · <span className="mono" data-testid="selection-count">{count} / {max}</span></span>
              <span className="sum-labels" data-testid="selection-labels">
                {count === 0
                  ? (seated ? 'Henüz koltuk seçmedin.' : 'Henüz bilet seçmedin.')
                  : seated
                    ? selected.map((seatId, i) => <span className="sl" key={seatId}>{seatLabel(seatsById.get(seatId)!)}{i < selected.length - 1 ? ',' : ''}</span>)
                    : gaLines.map((c, i) => <span className="sl" key={c.id}>{quantities[c.id]} × {c.name}{i < gaLines.length - 1 ? ',' : ''}</span>)}
              </span>
            </div>
            <div className="sum-total"><span className="sum-k">Toplam</span><strong data-testid="selection-total">{formatMoney(total)}</strong></div>
            <button type="button" className="btn btn-primary" onClick={() => void submit()} aria-disabled={count === 0 || blocked} aria-describedby="hold-message">
              {busy ? 'Tutuluyor…' : holdLabel}
            </button>
            <p className="sum-msg" id="hold-message" role="alert">
              {(message || lost.length > 0) && (
                <>
                  <Icon name="warn" />
                  <span>
                    {lost.length > 0 && `${lost.map(seatLabel).join(', ')} artık dolu; seçimden çıkarıldı. `}
                    {message}
                  </span>
                </>
              )}
            </p>
          </div>
        </div>
      )}
      <p className="sr-only" role="status">{announcement}</p>
    </>
  );
}

function PriceList({ session }: { session: PublicSessionDetail }) {
  const rows = session.salesMode === 'ReservedSeating'
    ? session.tiers.map((t) => ({ key: t.tierCode, name: `Kademe ${t.tierCode}`, price: t.priceMinor }))
    : session.categories.map((c) => ({ key: c.id, name: c.name, price: c.priceMinor }));
  if (rows.length === 0) return null;
  return (
    <dl className="facts" style={{ marginTop: 12 }}>
      {rows.map((row) => (
        <div key={row.key} style={{ display: 'contents' }}>
          <dt>{row.name}</dt>
          <dd className="mono">{formatMoney(row.price)}</dd>
        </div>
      ))}
    </dl>
  );
}
