import { useInfiniteQuery, useQuery } from '@tanstack/react-query';
import { Link, useParams, useSearchParams } from 'react-router-dom';
import { publicKey } from '../auth/AuthContext';
import { Icon } from '../components/Icon';
import { Screen } from '../components/Layout';
import { DemoTag, EmptyState, ErrorState, Loading, PageHeading, SessionTime, Status } from '../components/ui';
import { api } from '../lib/api';
import { CATEGORY_LABELS, dateColumn, formatInZone, formatMoney, formatSessionTime, SALES_MODE_LABELS } from '../lib/format';
import type { EventCategory, PublicEventDetail, PublicFilters, PublicSessionListItem, PublicSessionPage } from '../lib/types';

export function SaleStatus({ item }: { item: Pick<PublicSessionListItem, 'salesState' | 'salesStartAt' | 'soldOut' | 'timezone'> }) {
  if (item.salesState === 'NotStarted')
    return <Status tone="info" icon="clock">Satış {formatInZone(item.salesStartAt, item.timezone)} itibarıyla açılır</Status>;
  if (item.salesState === 'Closed') return <Status tone="mute" icon="cross">Satış kapandı</Status>;
  if (item.soldOut) return <Status tone="mute" icon="cross">Tükendi</Status>;
  return <Status tone="ok" icon="check">Satışta</Status>;
}

function SessionRow({ item, showEvent = true }: { item: PublicSessionListItem; showEvent?: boolean }) {
  const date = dateColumn(item.startsAt, item.timezone);
  const onSale = item.salesState === 'Open' && !item.soldOut;
  const when = formatSessionTime(item.startsAt, item.timezone);
  return (
    <li>
      <div className="r-date" aria-hidden="true">
        <span className="r-day">{date.day}</span>
        <span className="r-mon">{date.month} · {date.weekday}</span>
        <span className="r-time">{date.time}</span>
      </div>
      <div className="r-main">
        <h2 className="r-title">
          <Link to={`/seans/${item.sessionId}`}>{item.eventTitle}</Link>{' '}
          {item.isDemo && <DemoTag />}
        </h2>
        <p className="r-meta">
          {item.venueName}, {item.city} · {CATEGORY_LABELS[item.category]} · {SALES_MODE_LABELS[item.salesMode]}
          {showEvent && <> · <Link to={`/etkinlik/${item.eventSlug}`}>Etkinliğin tüm seansları</Link></>}
        </p>
        <p><SessionTime startsAt={item.startsAt} timezone={item.timezone} /></p>
      </div>
      <div className="r-sale">
        <SaleStatus item={item} />
        {item.minPriceMinor !== null && (
          <span className="r-price">{formatMoney(item.minPriceMinor)} <small>en düşük fiyat</small></span>
        )}
      </div>
      <div className="r-act">
        <Link className="btn" to={`/seans/${item.sessionId}`}>
          {onSale ? (item.salesMode === 'ReservedSeating' ? 'Koltuk seç' : 'Bilet seç') : 'Ayrıntı'}
          <span className="sr-only"> — {item.eventTitle}, {when}</span>
          <Icon name="arrow" />
        </Link>
      </div>
    </li>
  );
}

const dayStart = (date: string) => new Date(`${date}T00:00:00`);

function listUrl(filters: { city: string; category: string; from: string; to: string }, cursor: string | null): string {
  const query = new URLSearchParams({ limit: '20' });
  if (filters.city) query.set('city', filters.city);
  if (filters.category) query.set('category', filters.category);
  if (filters.from) query.set('from', dayStart(filters.from).toISOString());
  if (filters.to) {
    const end = dayStart(filters.to);
    end.setDate(end.getDate() + 1); // Bitiş günü dahil.
    query.set('to', end.toISOString());
  }
  if (cursor) query.set('cursor', cursor);
  return `/api/catalog/sessions?${query.toString()}`;
}

export function DiscoverPage() {
  const [params, setParams] = useSearchParams();
  const filters = {
    city: params.get('sehir') ?? '',
    category: params.get('kategori') ?? '',
    from: params.get('baslangic') ?? '',
    to: params.get('bitis') ?? '',
  };
  const rangeInvalid = Boolean(filters.from && filters.to && filters.to < filters.from);

  const options = useQuery({ queryKey: publicKey('filters'), queryFn: () => api.get<PublicFilters>('/api/catalog/filters'), staleTime: 60_000 });
  const sessions = useInfiniteQuery({
    queryKey: publicKey('sessions', filters.city, filters.category, filters.from, filters.to),
    queryFn: ({ pageParam }) => api.get<PublicSessionPage>(listUrl(filters, pageParam)),
    initialPageParam: null as string | null,
    getNextPageParam: (last) => last.nextCursor,
    enabled: !rangeInvalid,
  });

  const setFilter = (key: string, value: string) => {
    const next = new URLSearchParams(params);
    if (value) next.set(key, value);
    else next.delete(key);
    setParams(next, { replace: true });
  };
  const clear = () => setParams(new URLSearchParams(), { replace: true });
  const hasFilter = Boolean(filters.city || filters.category || filters.from || filters.to);
  const items = sessions.data?.pages.flatMap((page) => page.items) ?? [];

  return (
    <Screen>
      <PageHeading title="Yaklaşan seanslar" />
      <p className="lede">Saatler seansın kendi saat diliminde gösterilir.</p>

      <form className="filters" aria-label="Seans filtreleri" onSubmit={(e) => e.preventDefault()}>
        <div className="field">
          <label htmlFor="f-city">Şehir</label>
          <select id="f-city" value={filters.city} onChange={(e) => setFilter('sehir', e.target.value)}>
            <option value="">Tüm şehirler</option>
            {/* URL'deki değer listede yoksa da seçili görünsün. */}
            {[...new Set([...(options.data?.cities ?? []), ...(filters.city ? [filters.city] : [])])].map((city) => (
              <option key={city} value={city}>{city}</option>
            ))}
          </select>
        </div>
        <div className="field">
          <label htmlFor="f-cat">Kategori</label>
          <select id="f-cat" value={filters.category} onChange={(e) => setFilter('kategori', e.target.value)}>
            <option value="">Tüm kategoriler</option>
            {[...new Set<string>([...(options.data?.categories ?? []), ...(filters.category ? [filters.category] : [])])].map((category) => (
              <option key={category} value={category}>{CATEGORY_LABELS[category as EventCategory] ?? category}</option>
            ))}
          </select>
        </div>
        <div className="field">
          <label htmlFor="f-from">Başlangıç tarihi</label>
          <input type="date" id="f-from" value={filters.from} onChange={(e) => setFilter('baslangic', e.target.value)} />
        </div>
        <div className="field">
          <label htmlFor="f-to">Bitiş tarihi</label>
          <input
            type="date" id="f-to" value={filters.to} min={filters.from || undefined}
            aria-invalid={rangeInvalid || undefined} aria-describedby={rangeInvalid ? 'f-range-err' : undefined}
            onChange={(e) => setFilter('bitis', e.target.value)}
          />
        </div>
        <button type="button" className="btn" onClick={clear} disabled={!hasFilter}>Filtreleri temizle</button>
      </form>
      {rangeInvalid && (
        <p className="field-err" id="f-range-err" role="alert" style={{ marginTop: 10 }}>
          <Icon name="warn" /><span>Bitiş tarihi başlangıç tarihinden önce olamaz.</span>
        </p>
      )}

      {rangeInvalid ? null : sessions.isPending ? (
        <Loading label="Seanslar yükleniyor…" />
      ) : sessions.isError && items.length === 0 ? (
        <ErrorState error={sessions.error} onRetry={() => void sessions.refetch()} />
      ) : items.length === 0 ? (
        <EmptyState title={hasFilter ? 'Bu filtrelerle seans bulunamadı.' : 'Şu anda yayında seans yok.'}>
          {hasFilter && (
            <>
              <p>Tarih aralığını genişletmeyi veya şehir ve kategori seçimini kaldırmayı dene.</p>
              <p><button type="button" className="btn" onClick={clear}>Filtreleri temizle</button></p>
            </>
          )}
        </EmptyState>
      ) : (
        <>
          <p className="count" role="status">
            {items.length} seans listeleniyor{sessions.hasNextPage ? ' · daha fazlası var' : ''}
          </p>
          <ol className="rows" data-testid="session-rows">
            {items.map((item) => <SessionRow key={item.sessionId} item={item} />)}
          </ol>
          {sessions.hasNextPage && (
            <p className="more">
              <button type="button" className="btn" onClick={() => void sessions.fetchNextPage()} disabled={sessions.isFetchingNextPage}>
                {sessions.isFetchingNextPage ? 'Yükleniyor…' : 'Daha fazla seans göster'}
              </button>
            </p>
          )}
          {sessions.isError && <ErrorState error={sessions.error} onRetry={() => void sessions.fetchNextPage()} />}
        </>
      )}
    </Screen>
  );
}

export function EventPage() {
  const { slug = '' } = useParams();
  const event = useQuery({
    queryKey: publicKey('event', slug),
    queryFn: () => api.get<PublicEventDetail>(`/api/catalog/events/${encodeURIComponent(slug)}`),
  });

  if (event.isPending) return <Screen><Loading label="Etkinlik yükleniyor…" /></Screen>;
  if (event.isError) {
    return <Screen><ErrorState error={event.error} onRetry={() => void event.refetch()} notFoundText="Bu etkinlik yayında değil ya da adresi değişti." /></Screen>;
  }
  const data = event.data;
  return (
    <Screen>
      <p className="eyebrow">
        {data.isDemo && <DemoTag label="Demo verisi" />}
        <span><Link to="/">Etkinlikler</Link> / {CATEGORY_LABELS[data.category]}</span>
      </p>
      <PageHeading title={data.title} />
      <p className="ctx"><span className="venue">Organizatör: {data.organizerName}</span></p>
      <p className="prose">{data.description}</p>
      <hr className="cut" />
      <h2>Seanslar</h2>
      {data.sessions.length === 0 ? (
        <EmptyState title="Bu etkinliğin yaklaşan seansı yok." />
      ) : (
        <ol className="rows" data-testid="session-rows">
          {data.sessions.map((item) => <SessionRow key={item.sessionId} item={item} showEvent={false} />)}
        </ol>
      )}
    </Screen>
  );
}
