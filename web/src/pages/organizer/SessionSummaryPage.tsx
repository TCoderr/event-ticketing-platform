import { useQuery } from '@tanstack/react-query';
import { Link, useParams } from 'react-router-dom';
import { useUserKey } from '../../auth/AuthContext';
import { CapacityBar } from '../../components/CapacityBar';
import { ErrorState, Loading, PageHeading } from '../../components/ui';
import { api } from '../../lib/api';
import { formatInZone, formatMoney } from '../../lib/format';
import type { OrganizerSession, SessionSummary } from '../../lib/types';
import { Breadcrumb, orgPath, SessionContext } from './shared';

// Tanımlar: docs/01-product-requirements.md §5.
const MONEY_TILES = [
  { key: 'grossCapturedMinor', id: 'summary-gross', label: 'Brüt tahsilat', note: 'başarılı ödemelerin tutar toplamı; geç gelen başarılar dahildir.' },
  { key: 'refundedMinor', id: 'summary-refunded', label: 'İade edilen', note: 'başarıyla tamamlanan iadelerin tutar toplamı.' },
  { key: 'refundPendingMinor', id: 'summary-refund-pending', label: 'Bekleyen iade', note: 'beklemede veya başarısız iadelerin toplamı; henüz geri ödenmedi.' },
  { key: 'netMinor', id: 'summary-net', label: 'Net', note: 'brüt tahsilat − iade edilen. Bekleyen iadeler tamamlanana kadar net tutardan düşülmez.' },
] as const;

export function SessionSummaryPage() {
  const { orgId = '', sessionId = '' } = useParams();
  const userKey = useUserKey();
  const session = useQuery({
    queryKey: userKey('org-session', sessionId),
    queryFn: () => api.get<OrganizerSession>(`/api/organizer/sessions/${sessionId}`),
  });
  const summary = useQuery({
    queryKey: userKey('org-session', sessionId, 'summary'),
    queryFn: () => api.get<SessionSummary>(`/api/organizer/sessions/${sessionId}/summary`),
    refetchInterval: (query) => (query.state.error ? false : 10_000),
  });

  if (session.isPending || summary.isPending) return <Loading label="Seans özeti yükleniyor…" />;
  if (session.isError) {
    return <ErrorState error={session.error} onRetry={() => void session.refetch()} notFoundText="Bu seans yok ya da organizasyonuna ait değil." />;
  }
  if (summary.isError && !summary.data) {
    return <ErrorState error={summary.error} onRetry={() => void summary.refetch()} notFoundText="Bu seans yok ya da organizasyonuna ait değil." />;
  }
  const s = session.data;
  const data = summary.data!;
  const unit = data.salesMode === 'ReservedSeating' ? 'koltuk' : 'bilet';

  return (
    <>
      <Breadcrumb orgId={orgId} eventId={s.eventId} eventTitle={s.eventTitle} tail="Seans özeti" />
      <PageHeading title="Seans özeti" />
      <SessionContext session={s} />
      <p style={{ marginTop: 12 }}>
        <Link className="btn" to={orgPath(orgId, `seans/${s.id}`)}>Seansı düzenle</Link>
      </p>
      {summary.isRefetchError && (
        <p className="note-box is-warn" role="status">Özet şu anda yenilenemiyor; gösterilen sayılar son başarılı sorguya ait.</p>
      )}

      <div className="blk">
        <h2>Kapasite</h2>
        <p className="note">
          {data.capacity} {unit} · anlık envanter sayaçları · sunucu saati {formatInZone(data.serverTime, s.timezone)}
        </p>
        <CapacityBar capacity={data.capacity} sold={data.sold} held={data.held} available={data.available} unit={unit} />
        <p className="note" style={{ marginTop: 8, maxWidth: 860 }}>
          “Tutuluyor” yalnız süresi dolmamış rezervasyonları sayar. Giriş yapan bilet:{' '}
          <span className="mono" data-testid="summary-checked-in">{data.ticketsCheckedIn} / {data.ticketsSold}</span>.
        </p>
      </div>

      <div className="blk">
        <h2>Tahsilat ve iade</h2>
        <p className="note">{data.currency} · vergi, komisyon ve hakediş hesaplanmaz</p>
        <dl className="tiles4">
          {MONEY_TILES.map((tile, index) => (
            <div key={tile.key}>
              <dt>{tile.label}<sup>{index + 1}</sup></dt>
              <dd aria-describedby={`fn-${tile.key}`} data-testid={tile.id}>{formatMoney(data[tile.key])}</dd>
            </div>
          ))}
        </dl>
        <ol className="fn" aria-label="Metrik tanımları">
          {MONEY_TILES.map((tile, index) => (
            <li key={tile.key} id={`fn-${tile.key}`}>
              <span>{index + 1}</span>
              <span><strong>{tile.label}:</strong> {tile.note}</span>
            </li>
          ))}
        </ol>
      </div>

      <div className="blk">
        <h2>Bilet ve sipariş sayıları</h2>
        <p className="note">Satılan bilet: geçerli + giriş yapmış biletler. İptal edilen biletler sayılmaz.</p>
        <dl className="tiles4 is-counts">
          <div><dt>Satılan bilet</dt><dd data-testid="summary-tickets-sold">{data.ticketsSold}</dd></div>
          <div><dt>Giriş yapan bilet</dt><dd>{data.ticketsCheckedIn}</dd></div>
          <div><dt>Ödenmiş sipariş</dt><dd data-testid="summary-orders-paid">{data.ordersPaid}</dd></div>
          <div><dt>İptal edilen sipariş</dt><dd data-testid="summary-orders-cancelled">{data.ordersCancelled}</dd></div>
          <div><dt>Ödeme bekleyen sipariş</dt><dd>{data.ordersPendingPayment}</dd></div>
        </dl>
      </div>
    </>
  );
}
