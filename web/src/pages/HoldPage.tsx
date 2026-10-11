import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useCallback, useState } from 'react';
import { Link, Navigate, useNavigate, useParams } from 'react-router-dom';
import { publicKey, useUserKey } from '../auth/AuthContext';
import { Countdown } from '../components/Countdown';
import { Icon } from '../components/Icon';
import { Screen } from '../components/Layout';
import { DemoTag, ErrorAlert, ErrorState, Loading, PageHeading, SessionTime, Status } from '../components/ui';
import { api, ApiError, IdempotentAction, isOutcomeUnknown, runIdempotent } from '../lib/api';
import { describeError } from '../lib/errors';
import { formatMoney } from '../lib/format';
import { stamp, type Stamped } from '../lib/stamp';
import type { Hold, Order, PublicSessionDetail } from '../lib/types';

export const PAYMENT_SCENARIOS = [
  { token: 'tok_success', label: 'Başarılı', hint: 'Tahsilat onaylanır, biletler oluşur.' },
  { token: 'tok_decline', label: 'Ret', hint: 'Sağlayıcı ödemeyi reddeder; tahsilat yapılmaz.' },
  { token: 'tok_timeout_success', label: 'Belirsiz → kurtarılır', hint: 'Kesin yanıt gelmez; sonuç sonradan doğrulanır.' },
  { token: 'tok_async_success', label: 'Asenkron', hint: 'Sonuç gecikmeli bildirimle gelir.' },
  { token: 'tok_late_success', label: 'Geç başarı', hint: 'Tahsilat bildirimi süre dolduktan sonra gelir; tutar iade edilir.' },
] as const;

const DEFAULT_METHOD = 'tok_success';

export function HoldPage() {
  const { id = '' } = useParams();
  const userKey = useUserKey();
  const navigate = useNavigate();
  const queryClient = useQueryClient();

  const holdQuery = useQuery({
    queryKey: userKey('hold', id),
    queryFn: async (): Promise<Stamped<Hold>> => stamp(await api.get<Hold>(`/api/holds/${id}`)),
  });
  const hold = holdQuery.data?.data;
  const sessionQuery = useQuery({
    queryKey: publicKey('session', hold?.sessionId),
    queryFn: () => api.get<PublicSessionDetail>(`/api/catalog/sessions/${hold!.sessionId}`),
    enabled: hold !== undefined,
    retry: false,
  });
  const session = sessionQuery.data;

  const [method, setMethod] = useState<string>(DEFAULT_METHOD);
  const [busy, setBusy] = useState<'pay' | 'release' | null>(null);
  const [failure, setFailure] = useState<unknown>(null);
  const [checkoutAction] = useState(() => new IdempotentAction());
  const { refetch } = holdQuery;
  // Sayaç yalnız gösterimdir: süre bitince gerçek durum sunucudan okunur.
  const onExpire = useCallback(() => void refetch(), [refetch]);

  if (holdQuery.isPending) return <Screen><Loading label="Rezervasyon yükleniyor…" /></Screen>;
  if (holdQuery.isError) {
    return <Screen><ErrorState error={holdQuery.error} onRetry={() => void refetch()} notFoundText="Bu rezervasyon yok ya da sana ait değil." /></Screen>;
  }
  if (!hold) return null;
  if (hold.orderId) return <Navigate to={`/siparis/${hold.orderId}`} replace />;

  const simulator = session?.demoSimulatorEnabled === true;
  const reselect = `/seans/${hold.sessionId}`;

  const pay = async () => {
    if (busy) return;
    const paymentMethod = simulator ? method : DEFAULT_METHOD;
    setBusy('pay');
    setFailure(null);
    try {
      const order = await runIdempotent(checkoutAction, paymentMethod, (key) =>
        api.post<Order>(`/api/holds/${hold.id}/checkout`, { paymentMethod }, { idempotencyKey: key }));
      // 202: sipariş kaydedildi, ödeme HENÜZ alınmadı. Gerçek durum sipariş ekranında yoklanır.
      queryClient.setQueryData(userKey('order', order.id), order);
      void queryClient.invalidateQueries({ queryKey: userKey('orders') });
      navigate(`/siparis/${order.id}`, { replace: true });
    } catch (error) {
      setBusy(null);
      if (error instanceof ApiError && error.code === 'order_already_exists' && typeof error.problem.orderId === 'string') {
        navigate(`/siparis/${error.problem.orderId}`, { replace: true });
        return;
      }
      if (error instanceof ApiError && (error.code === 'hold_expired' || error.code === 'session_cancelled')) void refetch();
      setFailure(error);
    }
  };

  const release = async () => {
    if (busy) return;
    setBusy('release');
    setFailure(null);
    try {
      await api.delete(`/api/holds/${hold.id}`);
      queryClient.removeQueries({ queryKey: userKey('hold', hold.id) });
      void queryClient.invalidateQueries({ queryKey: userKey('my-hold', hold.sessionId) });
      navigate(reselect);
    } catch (error) {
      setBusy(null);
      if (error instanceof ApiError && (error.code === 'hold_not_active' || error.code === 'order_already_exists')) void refetch();
      setFailure(error);
    }
  };

  return (
    <Screen>
      <p className="eyebrow">
        {session?.isDemo && <DemoTag label="Demo verisi" />}
        <span><Link to={reselect}>Seçime dön</Link> / Rezervasyon ve ödeme</span>
      </p>
      <PageHeading title="Ödeme" />
      {session && (
        <p className="ctx">
          <strong>{session.eventTitle}</strong>
          <SessionTime startsAt={session.startsAt} timezone={session.timezone} />
          <span className="venue">{session.venueName}, {session.city}</span>
        </p>
      )}

      <div className="pay-grid">
        <div>
          {hold.status === 'Active' && (
            <>
              <Countdown expiresAt={hold.expiresAt} serverTime={hold.serverTime} receivedAt={holdQuery.data.receivedAt} onExpire={onExpire} />
              <p className="note" style={{ marginTop: 8 }}>Sayaç yalnız gösterimdir; süre sunucu saatine göre biter.</p>
            </>
          )}
          <table className="lines">
            <caption>Rezervasyon satırları</caption>
            <thead>
              <tr>
                <th scope="col">Satır</th>
                <th scope="col" className="num">Adet</th>
                <th scope="col" className="num">Birim</th>
                <th scope="col" className="num">Tutar</th>
              </tr>
            </thead>
            <tbody>
              {hold.lines.map((line) => (
                <tr key={line.lineNo}>
                  <th scope="row">{line.label}</th>
                  <td className="num">{line.quantity}</td>
                  <td className="num">{formatMoney(line.unitPriceMinor)}</td>
                  <td className="num">{formatMoney(line.unitPriceMinor * line.quantity)}</td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr>
                <th scope="row" colSpan={3}>Toplam (TRY)</th>
                <td className="num" data-testid="hold-total">{formatMoney(hold.totalMinor)}</td>
              </tr>
            </tfoot>
          </table>
        </div>

        <div className="pay-form">
          {hold.status === 'Active' ? (
            <>
              <h2>Ödeme yöntemi</h2>
              <div className="method">
                <Icon name="info" />
                <p><strong>Simüle kart</strong> — demo ödeme sağlayıcısı. Gerçek kart bilgisi istenmez, gerçek tahsilat yapılmaz.</p>
              </div>
              {simulator && (
                <fieldset className="demo-set">
                  <legend><DemoTag /> Demo ödeme senaryosu</legend>
                  <p className="note">Bu seansta demo simülatörü açık. Ödeme sağlayıcısının nasıl yanıt vereceğini seç.</p>
                  <div className="scn">
                    {PAYMENT_SCENARIOS.map((scenario) => (
                      <label key={scenario.token}>
                        <input
                          type="radio" name="scenario" value={scenario.token}
                          checked={method === scenario.token} onChange={() => setMethod(scenario.token)} disabled={busy !== null}
                        />
                        <span>{scenario.label}<small>{scenario.hint}</small></span>
                      </label>
                    ))}
                  </div>
                </fieldset>
              )}
              <ErrorAlert error={failure ? (isOutcomeUnknown(failure)
                ? 'Sunucuya ulaşılamadı. Ödemenin başlatılıp başlatılmadığı bilinmiyor; yeniden denediğinde aynı istek güvenle tekrarlanır (çift ödeme oluşmaz).'
                : describeError(failure)) : null} />
              <div className="pay-actions">
                <button type="button" className="btn btn-primary" onClick={() => void pay()} disabled={busy !== null}>
                  {busy === 'pay' ? 'Ödeme başlatılıyor…' : <>Ödemeyi başlat · <span className="mono">{formatMoney(hold.totalMinor)}</span></>}
                </button>
                <button type="button" className="btn" onClick={() => void release()} disabled={busy !== null}>
                  {busy === 'release' ? 'Bırakılıyor…' : 'Rezervasyonu bırak'}
                </button>
              </div>
              <p className="note">“Ödemeyi başlat” ödemenin alındığı anlamına gelmez; sonuç sipariş durumu ekranında izlenir.</p>
            </>
          ) : (
            <div role="status">
              <h2>
                {hold.status === 'Expired'
                  ? <Status tone="warn" icon="clock">Süre doldu</Status>
                  : <Status tone="mute" icon="cross">{hold.status === 'Released' ? 'Rezervasyon bırakıldı' : 'Rezervasyon kapandı'}</Status>}
              </h2>
              <p style={{ marginTop: 8 }}>
                {hold.status === 'Expired'
                  ? 'Rezervasyon süresi ödeme başlamadan bitti. Seçimin serbest bırakıldı; tahsilat yapılmadı.'
                  : 'Bu rezervasyon artık aktif değil. Seçimin serbest bırakıldı; tahsilat yapılmadı.'}
              </p>
              <div className="pay-actions" style={{ marginTop: 14 }}>
                <Link className="btn btn-primary" to={reselect}>Yeniden seç</Link>
              </div>
            </div>
          )}
        </div>
      </div>
    </Screen>
  );
}
