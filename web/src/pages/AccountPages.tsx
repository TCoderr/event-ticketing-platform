import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { useUserKey } from '../auth/AuthContext';
import { Icon } from '../components/Icon';
import { Screen } from '../components/Layout';
import { OrderStatusPanel } from '../components/OrderStatusPanel';
import { QrCode } from '../components/QrCode';
import { ConfirmPanel, DemoTag, EmptyState, ErrorAlert, ErrorState, Loading, PageHeading, SessionTime, Status } from '../components/ui';
import { api, ApiError, IdempotentAction, isOutcomeUnknown, runIdempotent } from '../lib/api';
import { describeError } from '../lib/errors';
import { formatInZone, formatMoney } from '../lib/format';
import { orderNeedsPolling, orderPollDelay, orderStatusLabel, orderView } from '../lib/orderStatus';
import type { Order, TicketDetail, TicketListItem, TicketStatus } from '../lib/types';

// --- Sipariş durumu ----------------------------------------------------------------------------

export function OrderPage() {
  const { id = '' } = useParams();
  const userKey = useUserKey();
  const queryClient = useQueryClient();

  const orderQuery = useQuery({
    queryKey: userKey('order', id),
    queryFn: () => api.get<Order>(`/api/orders/${id}`),
    // Checkout 202 "ödendi" değildir: sonuç kesinleşene kadar yoklanır (1 sn → geri çekilerek 5 sn).
    refetchInterval: (query) => {
      const order = query.state.data;
      if (!order || !orderNeedsPolling(order)) return false;
      return order.status === 'PendingPayment' ? orderPollDelay(query.state.dataUpdateCount - 1) : 5000;
    },
  });
  const order = orderQuery.data;

  const ticketsQuery = useQuery({
    queryKey: userKey('tickets'),
    queryFn: () => api.get<TicketListItem[]>('/api/tickets'),
    enabled: order?.status === 'Paid',
    // Biletler ödeme onayından hemen sonra, ayrı bir adımda üretilir.
    refetchInterval: (query) => {
      if (order?.status !== 'Paid') return false;
      const ready = (query.state.data ?? []).filter((t) => t.orderId === order.id).length;
      return ready < order.ticketCount && query.state.dataUpdateCount < 30 ? 1500 : false;
    },
  });

  const [confirming, setConfirming] = useState(false);
  const [cancelling, setCancelling] = useState(false);
  const [failure, setFailure] = useState<unknown>(null);
  const [cancelAction] = useState(() => new IdempotentAction());

  if (orderQuery.isPending) return <Screen><Loading label="Sipariş yükleniyor…" /></Screen>;
  if (orderQuery.isError) {
    return <Screen><ErrorState error={orderQuery.error} onRetry={() => void orderQuery.refetch()} notFoundText="Bu sipariş yok ya da sana ait değil." /></Screen>;
  }
  if (!order) return null;

  const view = orderView(order);
  const tickets = (ticketsQuery.data ?? []).filter((t) => t.orderId === order.id);
  const reselect = `/seans/${order.sessionId}`;

  const cancel = async () => {
    setCancelling(true);
    setFailure(null);
    try {
      const updated = await runIdempotent(cancelAction, order.id, (key) =>
        api.post<Order>(`/api/orders/${order.id}/cancel`, undefined, { idempotencyKey: key }));
      queryClient.setQueryData(userKey('order', order.id), updated);
      void queryClient.invalidateQueries({ queryKey: userKey('tickets') });
      void queryClient.invalidateQueries({ queryKey: userKey('orders') });
      void queryClient.invalidateQueries({ queryKey: userKey('ticket') });
      setConfirming(false);
    } catch (error) {
      setFailure(error);
      if (error instanceof ApiError && !isOutcomeUnknown(error)) {
        setConfirming(false);
        void orderQuery.refetch();
      }
    } finally {
      setCancelling(false);
    }
  };

  return (
    <Screen>
      <p className="eyebrow"><span><Link to="/siparislerim">Siparişlerim</Link> / Sipariş durumu</span></p>
      <PageHeading title={`Sipariş ${order.orderNumber}`}>
        Sipariş <span className="mono" style={{ fontSize: '.72em' }}>{order.orderNumber}</span>
      </PageHeading>

      <div className="order-grid">
        <div>
          <OrderStatusPanel order={order}>
            {view.kind === 'paid' && (
              <>
                <p style={{ marginTop: 8 }} role="status">
                  {tickets.length >= order.ticketCount
                    ? `${tickets.length} bilet hazır.`
                    : ticketsQuery.isError
                      ? 'Bilet listesi okunamadı; Biletlerim sayfasından denetleyebilirsin.'
                      : `Biletler hazırlanıyor… (${tickets.length} / ${order.ticketCount})`}
                </p>
                <div className="ost-actions">
                  <Link className="btn btn-primary" to="/biletlerim">Biletleri gör<Icon name="arrow" /></Link>
                  {order.canCancel && !confirming && (
                    <button type="button" className="btn" onClick={() => { setConfirming(true); setFailure(null); }}>Siparişi iptal et</button>
                  )}
                </div>
                <p className="note" style={{ marginTop: 12 }}>
                  {order.canCancel
                    ? `Bu sipariş ${formatInZone(order.cancelDeadline, order.timezone)} (${order.timezone}) anına kadar, hiçbir bilet kullanılmadıysa tamamen iptal edilebilir.`
                    : `İptal süresi ${formatInZone(order.cancelDeadline, order.timezone)} (${order.timezone}) itibarıyla doldu ya da sipariş iptale uygun değil.`}
                </p>
              </>
            )}
            {(view.kind === 'declined' || view.kind === 'payment-failed' || view.kind === 'expired') && order.sessionStatus === 'Published' && (
              <div className="ost-actions"><Link className="btn btn-primary" to={reselect}>Yeniden seç</Link></div>
            )}
            {confirming && (
              <ConfirmPanel
                title="Sipariş iptal edilsin mi?"
                confirmLabel="Evet, siparişi iptal et"
                onConfirm={() => void cancel()}
                onCancel={() => setConfirming(false)}
                busy={cancelling}
              >
                <p>
                  Siparişin tamamı iptal edilir; {order.ticketCount} bilet hemen geçersiz olur ve {formatMoney(order.totalMinor)} için iade başlatılır.
                  Bu işlem geri alınamaz.
                </p>
                <p className="note">Son iptal zamanı: {formatInZone(order.cancelDeadline, order.timezone)} · {order.timezone}</p>
              </ConfirmPanel>
            )}
            {failure !== null && (
              <div style={{ marginTop: 12 }}>
                <ErrorAlert error={isOutcomeUnknown(failure)
                  ? 'Sunucuya ulaşılamadı. İptalin işlenip işlenmediği bilinmiyor; yeniden denediğinde aynı istek güvenle tekrarlanır.'
                  : describeError(failure)} />
              </div>
            )}
          </OrderStatusPanel>
          {orderQuery.isRefetchError && (
            <p className="note-box is-warn" role="status">Durum şu anda yenilenemiyor; gösterilen bilgi son başarılı sorguya ait. Yeniden denenecek.</p>
          )}
        </div>

        <div className="side">
          <h2>Sipariş özeti</h2>
          <dl className="facts">
            <dt>Etkinlik</dt><dd>{order.eventTitle} {order.isDemo && <DemoTag />}</dd>
            <dt>Seans</dt><dd><SessionTime startsAt={order.startsAt} timezone={order.timezone} /></dd>
            <dt>Mekan</dt><dd>{order.venueName}, {order.city}</dd>
            <dt>Satırlar</dt>
            <dd>
              <ul>
                {order.lines.map((line) => (
                  <li key={line.lineNo}>{line.quantity} × {line.label} — {formatMoney(line.quantity * line.unitPriceMinor)}</li>
                ))}
              </ul>
            </dd>
            <dt>Toplam</dt><dd className="mono" style={{ fontWeight: 600 }}>{formatMoney(order.totalMinor)}</dd>
            <dt>Oluşturuldu</dt><dd className="when">{formatInZone(order.createdAt, order.timezone)}</dd>
          </dl>
        </div>
      </div>
    </Screen>
  );
}

export function OrdersPage() {
  const userKey = useUserKey();
  const orders = useQuery({ queryKey: userKey('orders'), queryFn: () => api.get<Order[]>('/api/orders') });
  return (
    <Screen>
      <PageHeading title="Siparişlerim" />
      {orders.isPending ? <Loading label="Siparişler yükleniyor…" />
        : orders.isError ? <ErrorState error={orders.error} onRetry={() => void orders.refetch()} />
          : orders.data.length === 0 ? (
            <EmptyState title="Henüz siparişin yok.">
              <p><Link className="btn" to="/">Etkinliklere göz at</Link></p>
            </EmptyState>
          ) : (
            <ul className="list">
              {orders.data.map((order) => {
                const view = orderView(order);
                return (
                  <li key={order.id}>
                    <div>
                      <p className="l-title"><Link to={`/siparis/${order.id}`}>{order.eventTitle}</Link> {order.isDemo && <DemoTag />}</p>
                      <p className="l-meta">
                        <span className="mono">{order.orderNumber}</span> · {order.ticketCount} bilet · {order.venueName}, {order.city}
                      </p>
                      <p><SessionTime startsAt={order.startsAt} timezone={order.timezone} /></p>
                    </div>
                    <div className="l-side">
                      <Status tone={view.tone} icon={view.icon}>{orderStatusLabel(order)}</Status>
                      <span className="mono" style={{ fontWeight: 600 }}>{formatMoney(order.totalMinor)}</span>
                      <Link className="btn btn-sm" to={`/siparis/${order.id}`}>Durumu gör<span className="sr-only"> — {order.orderNumber}</span></Link>
                    </div>
                  </li>
                );
              })}
            </ul>
          )}
    </Screen>
  );
}

// --- Biletler ----------------------------------------------------------------------------------

export function TicketStatusLabel({ status, checkedInAt, timezone }: { status: TicketStatus; checkedInAt: string | null; timezone: string }) {
  if (status === 'Valid') return <Status tone="ok" icon="check">Geçerli · henüz kullanılmadı</Status>;
  if (status === 'CheckedIn') {
    return <Status tone="mute" icon="repeat">Kullanıldı{checkedInAt ? ` · ${formatInZone(checkedInAt, timezone)}` : ''}</Status>;
  }
  return <Status tone="err" icon="cross">İptal · geçersiz</Status>;
}

export function TicketsPage() {
  const userKey = useUserKey();
  const tickets = useQuery({ queryKey: userKey('tickets'), queryFn: () => api.get<TicketListItem[]>('/api/tickets') });
  return (
    <Screen>
      <PageHeading title="Biletlerim" />
      <p className="lede">Giriş kodu (QR) bilet ayrıntısında gösterilir. Her bilet kapıda yalnız bir kez kabul edilir.</p>
      {tickets.isPending ? <Loading label="Biletler yükleniyor…" />
        : tickets.isError ? <ErrorState error={tickets.error} onRetry={() => void tickets.refetch()} />
          : tickets.data.length === 0 ? (
            <EmptyState title="Henüz biletin yok.">
              <p>Ödemesi onaylanan siparişlerin biletleri burada görünür.</p>
              <p><Link className="btn" to="/">Etkinliklere göz at</Link></p>
            </EmptyState>
          ) : (
            <ul className="list" data-testid="ticket-list">
              {tickets.data.map((ticket) => (
                <li key={ticket.id}>
                  <div>
                    <p className="l-title"><Link to={`/bilet/${ticket.id}`}>{ticket.eventTitle}</Link> {ticket.isDemo && <DemoTag />}</p>
                    <p className="mono" style={{ fontWeight: 600 }}>{ticket.label}</p>
                    <p className="l-meta">{ticket.venueName}, {ticket.city}</p>
                    <p><SessionTime startsAt={ticket.startsAt} timezone={ticket.timezone} /></p>
                  </div>
                  <div className="l-side">
                    <TicketStatusLabel status={ticket.status} checkedInAt={ticket.checkedInAt} timezone={ticket.timezone} />
                    <Link className="btn btn-sm" to={`/bilet/${ticket.id}`}>
                      Bileti aç<span className="sr-only"> — {ticket.eventTitle}, {ticket.label}</span>
                    </Link>
                  </div>
                </li>
              ))}
            </ul>
          )}
    </Screen>
  );
}

export function TicketPage() {
  const { id = '' } = useParams();
  const userKey = useUserKey();
  const [showCode, setShowCode] = useState(false);
  // Token yalnız bellekte (sorgu önbelleğinde) tutulur; çıkışta önbellekle birlikte silinir.
  const query = useQuery({ queryKey: userKey('ticket', id), queryFn: () => api.get<TicketDetail>(`/api/tickets/${id}`), gcTime: 0 });

  if (query.isPending) return <Screen><Loading label="Bilet yükleniyor…" /></Screen>;
  if (query.isError) {
    return <Screen><ErrorState error={query.error} onRetry={() => void query.refetch()} notFoundText="Bu bilet yok ya da sana ait değil." /></Screen>;
  }
  const ticket = query.data;
  const usable = ticket.status === 'Valid' && Boolean(ticket.token);

  return (
    <Screen>
      <p className="eyebrow"><span><Link to="/biletlerim">Biletlerim</Link> / Bilet ayrıntısı</span></p>
      <PageHeading title="Bilet" />
      <article className="ticket" aria-labelledby="tk-title">
        <div className="tk-main">
          <p className="tk-kicker">LOCA · GİRİŞ BİLETİ</p>
          <h2 className="tk-title" id="tk-title">{ticket.eventTitle} {ticket.isDemo && <DemoTag />}</h2>
          <p className="tk-label">{ticket.label}</p>
          <dl className="facts tk-facts">
            <dt>Seans</dt><dd><SessionTime startsAt={ticket.startsAt} timezone={ticket.timezone} /></dd>
            <dt>Mekan</dt><dd>{ticket.venueName}, {ticket.city} · {ticket.venueAddress}</dd>
            <dt>Durum</dt>
            <dd data-testid="ticket-status"><TicketStatusLabel status={ticket.status} checkedInAt={ticket.checkedInAt} timezone={ticket.timezone} /></dd>
            <dt>Kapı kontrolü</dt>
            <dd className="when">
              {formatInZone(ticket.checkInOpensAt, ticket.timezone)} – {formatInZone(ticket.checkInClosesAt, ticket.timezone)} · {ticket.timezone}
            </dd>
            <dt>Sipariş</dt><dd><Link to={`/siparis/${ticket.orderId}`}>Sipariş durumunu gör</Link></dd>
          </dl>
        </div>
        <div className="tk-stub">
          {usable ? (
            <>
              <QrCode token={ticket.token!} label={`Giriş QR kodu: ${ticket.label}`} />
              <p className="note">Tek kullanımlık giriş kodu; kişisel veri içermez.</p>
              <button type="button" className="btn btn-sm" aria-expanded={showCode} onClick={() => setShowCode((v) => !v)}>
                {showCode ? 'Kodu gizle' : 'Kodu metin olarak göster'}
              </button>
              {showCode && <p className="tk-code" data-testid="ticket-token">{ticket.token}</p>}
            </>
          ) : (
            <div className="qr-off" data-testid="ticket-no-qr">
              <span>{ticket.status === 'CheckedIn' ? 'Bilet kullanıldı; giriş kodu artık gösterilmez.' : 'Bilet geçersiz; giriş kodu yok.'}</span>
            </div>
          )}
        </div>
      </article>
      <p className="note" style={{ marginTop: 12, maxWidth: 820 }}>
        Kod taşıyana giriş hakkı verir: ekran görüntüsünü paylaşma. Her bilet kapıda yalnız bir kez kabul edilir.
      </p>
    </Screen>
  );
}
