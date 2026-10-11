import { useQuery } from '@tanstack/react-query';
import { useState } from 'react';
import { useUserKey } from '../../auth/AuthContext';
import { Icon } from '../../components/Icon';
import { EmptyState, ErrorAlert, ErrorState, Loading } from '../../components/ui';
import { api } from '../../lib/api';
import { describeError } from '../../lib/errors';
import { formatMoney } from '../../lib/format';
import type { OperationItem, Operations, OperatorRun } from '../../lib/types';
import { formatLocalStamp, OutcomeBadge } from './shared';

type OperationsViewProps = { scope: 'admin' } | { scope: 'organizer'; organizationId: string };

const STATUS_LABELS: Record<string, string> = {
  Initiated: 'Başlatıldı', Pending: 'Bekliyor', Unknown: 'Belirsiz', Succeeded: 'Başarılı', Declined: 'Reddedildi', Failed: 'Başarısız',
};

const REFUND_REASONS: Record<string, string> = {
  CustomerCancellation: 'Müşteri iptali', SessionCancellation: 'Seans iptali', LatePaymentCompensation: 'Geç başarı telafisi',
};

function kindLabel(item: OperationItem): string {
  if (item.kind === 'cancellation') return 'Seans iptal işi';
  if (item.kind === 'refund') {
    if (item.lateSuccess) return 'Geç başarı telafisi';
    return item.status === 'Failed' ? 'Başarısız iade' : 'Bekleyen iade';
  }
  if (item.lateSuccess) return 'Geç başarı';
  if (item.status === 'Unknown') return 'Belirsiz ödeme';
  if (item.status === 'Pending' || item.status === 'Initiated') return 'Bekleyen ödeme';
  return 'İnceleme gereken ödeme';
}

function Detail({ item }: { item: OperationItem }) {
  if (item.kind === 'cancellation') {
    const processed = /^processed:(\d+)$/.exec(item.detail ?? '');
    return <>Siparişler sırayla iptal ediliyor{processed ? <> · işlenen rezervasyon: <span className="mono">{processed[1]}</span></> : null}.</>;
  }
  if (!item.detail) return <span className="t-mute">Ek ayrıntı yok</span>;
  const reason = REFUND_REASONS[item.detail];
  return reason ? <>{reason}</> : <span className="mono break">{item.detail}</span>;
}

/**
 * Operatör eyleminin sonucu AYRI adımlarla: planlandı → denendi → sonuç. Başarısız ya da sonuçsuz deneme
 * hiçbir zaman "başarılı" olarak gösterilmez.
 */
export function RunSteps({ run }: { run: OperatorRun }) {
  return (
    <span data-testid="run-steps">
      <span className="steps">
        {run.planned && <OutcomeBadge outcome="Planned" />}
        {run.attempted
          ? <OutcomeBadge outcome="Attempted" />
          : <span className="res-note" style={{ marginTop: 0 }}>denenmedi</span>}
        {run.outcome === 'Succeeded' && <OutcomeBadge outcome="Succeeded" />}
        {run.outcome === 'Failed' && <OutcomeBadge outcome="Failed" />}
      </span>
      <span className="res-note">
        {run.outcome === 'Succeeded' ? 'Sonuç: başarılı.'
          : run.outcome === 'Failed' ? 'Sonuç: başarısız.'
            : run.outcome === 'Attempted' ? 'Denendi, kesin sonuç yok.'
              : 'Planlandı; henüz denenmedi.'}{' '}
        <span className="mono break">{run.detailCode}</span>
      </span>
    </span>
  );
}

const itemKey = (item: OperationItem) => `${item.kind}:${item.id}`;

interface LastAction {
  label: string;
  target: string;
  run?: OperatorRun;
  error?: unknown;
}

/** Operasyon kuyruğu: organizatör kendi organizasyonunu, platform yöneticisi tümünü görür. */
export function OperationsView(props: OperationsViewProps) {
  const userKey = useUserKey();
  const organizationId = props.scope === 'organizer' ? props.organizationId : null;
  const query = useQuery({
    queryKey: userKey('operations', organizationId ?? 'admin'),
    queryFn: () => api.get<Operations>(organizationId ? `/api/organizer/organizations/${organizationId}/operations` : '/api/admin/operations'),
    refetchInterval: (q) => (q.state.error ? false : 10_000),
  });
  const [busy, setBusy] = useState<string | null>(null);
  const [runs, setRuns] = useState<Record<string, OperatorRun>>({});
  const [last, setLast] = useState<LastAction | null>(null);
  const { refetch } = query;

  const act = async (item: OperationItem, action: 'reconcile' | 'retry') => {
    if (busy !== null) return;
    const key = itemKey(item);
    const label = action === 'reconcile' ? 'Mutabakat' : 'İade yeniden deneme';
    const target = item.orderNumber ?? item.eventTitle;
    setBusy(key);
    try {
      const run = await api.post<OperatorRun>(
        action === 'reconcile' ? `/api/organizer/payments/${item.id}/reconcile` : `/api/organizer/refunds/${item.id}/retry`);
      setRuns((previous) => ({ ...previous, [key]: run }));
      setLast({ label, target, run });
    } catch (error) {
      setLast({ label, target, error });
    } finally {
      setBusy(null);
      void refetch(); // Kuyruğun gerçek durumu her eylemden sonra sunucudan okunur.
    }
  };

  if (query.isPending) return <Loading label="Operasyon kuyruğu yükleniyor…" />;
  if (query.isError && !query.data) return <ErrorState error={query.error} onRetry={() => void refetch()} />;
  const data = query.data!;
  const c = data.counts;

  return (
    <>
      <p className="lede">
        Elle izlenmesi gereken kayıtlar: belirsiz ödemeler, başarısız iadeler, geç başarı telafileri ve seans iptal işleri.
        Her eylemin sonucu ayrı gösterilir: planlandı, denendi, başarılı, başarısız.
      </p>
      {query.isRefetchError && (
        <p className="note-box is-warn" role="status">Kuyruk şu anda yenilenemiyor ({describeError(query.error)}); gösterilen liste son başarılı sorguya ait.</p>
      )}

      <div className="blk">
        <h2>Kuyruk sayaçları</h2>
        <dl className="tiles4 is-counts" data-testid="ops-counts">
          <div><dt>Belirsiz ödeme</dt><dd>{c.unknownPayments}</dd></div>
          <div><dt>Bekleyen ödeme</dt><dd>{c.pendingPayments}</dd></div>
          <div><dt>İnceleme gereken ödeme</dt><dd>{c.reviewPayments}</dd></div>
          <div><dt>Geç başarı</dt><dd>{c.lateSuccesses}</dd></div>
          <div><dt>Bekleyen iade</dt><dd>{c.pendingRefunds}</dd></div>
          <div><dt>Başarısız iade</dt><dd>{c.failedRefunds}</dd></div>
          <div><dt>Bekleyen iptal işi</dt><dd>{c.pendingCancellationJobs}</dd></div>
        </dl>
        <div className="sub">
          <h3>Operatör eylemleri</h3>
          <p className="note">Yalnız gerçekten başarıyla sonuçlanan eylemler “başarılı” sayılır.</p>
          <dl className="tiles4 is-counts" data-testid="ops-run-counts">
            <div><dt>Planlandı (denenmedi)</dt><dd>{c.runsPlanned}</dd></div>
            <div><dt>Denendi (kesin sonuç yok)</dt><dd>{c.runsAttempted}</dd></div>
            <div><dt>Başarılı</dt><dd data-testid="runs-succeeded">{c.runsSucceeded}</dd></div>
            <div><dt>Başarısız</dt><dd data-testid="runs-failed">{c.runsFailed}</dd></div>
          </dl>
        </div>
      </div>

      <div className="blk">
        <h2 id="ops-title">Kuyruktaki kayıtlar</h2>
        <div role="status" data-testid="ops-last-action">
          {last && (
            <div className="note-box" style={{ marginBottom: 12 }}>
              <p><strong>Son eylem:</strong> {last.label} · <span className="mono break">{last.target}</span></p>
              {last.run && <RunSteps run={last.run} />}
              {last.error !== undefined && <ErrorAlert error={`Eylem tamamlanamadı: ${describeError(last.error)}`} />}
            </div>
          )}
        </div>
        {data.items.length === 0 ? (
          <EmptyState title="Kuyruk boş."><p>İzlenmesi gereken ödeme, iade ya da iptal işi yok.</p></EmptyState>
        ) : (
          <div className="table-wrap">
            <table className="ops" aria-labelledby="ops-title">
              <thead>
                <tr>
                  <th scope="col">Tür</th>
                  <th scope="col">Sipariş</th>
                  <th scope="col">Tutar</th>
                  <th scope="col">Ayrıntı</th>
                  <th scope="col">Son eylem ve sonucu</th>
                  <th scope="col">Eylem</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((item) => {
                  const key = itemKey(item);
                  const run = runs[key];
                  return (
                    <tr key={key} data-kind={item.kind}>
                      <th scope="row" className="kind" data-l="Tür">
                        {kindLabel(item)}
                        <span className="res-note" style={{ fontWeight: 400 }}>Durum: {STATUS_LABELS[item.status] ?? item.status}</span>
                      </th>
                      <td data-l="Sipariş">
                        <span className="mono break">{item.orderNumber ?? '—'}</span>
                        <span className="res-note">{item.eventTitle}</span>
                      </td>
                      <td className="num" data-l="Tutar">{item.kind === 'cancellation' ? '—' : formatMoney(item.amountMinor)}</td>
                      <td data-l="Ayrıntı">
                        <Detail item={item} />
                        <span className="res-note">
                          {item.kind !== 'cancellation' && <>Deneme: <span className="mono">{item.attemptCount}</span> · </>}
                          Güncelleme: {formatLocalStamp(item.updatedAt)}
                        </span>
                      </td>
                      <td data-l="Son eylem">
                        {run ? <RunSteps run={run} /> : <span className="t-mute">Bu oturumda eylem yapılmadı</span>}
                      </td>
                      <td className="act">
                        {item.actions.length > 0 && (
                          <span className="cell-actions">
                            {item.actions.includes('reconcile') && (
                              <button type="button" className="btn btn-sm" onClick={() => void act(item, 'reconcile')} disabled={busy !== null}>
                                {busy === key ? 'Deneniyor…' : 'Mutabakat yap'}
                                <span className="sr-only"> — {item.orderNumber}</span>
                              </button>
                            )}
                            {item.actions.includes('retry') && (
                              <button type="button" className="btn btn-sm" onClick={() => void act(item, 'retry')} disabled={busy !== null}>
                                {busy === key ? 'Deneniyor…' : 'İadeyi yeniden dene'}
                                <span className="sr-only"> — {item.orderNumber}</span>
                              </button>
                            )}
                          </span>
                        )}
                      </td>
                    </tr>
                  );
                })}
              </tbody>
            </table>
          </div>
        )}
      </div>

      <div className="blk">
        <h2>Son operatör eylemleri</h2>
        {data.recentRuns.length === 0 ? (
          <p className="note">Henüz operatör eylemi yok.</p>
        ) : (
          <ul className="list" data-testid="ops-recent-runs">
            {data.recentRuns.map((run) => (
              <li key={run.id}>
                <div>
                  <p style={{ fontWeight: 600 }}>{run.targetType === 'payment' ? 'Ödeme mutabakatı' : 'İade yeniden deneme'}</p>
                  <p className="l-meta"><span className="mono break">{run.detailCode}</span> · {formatLocalStamp(run.createdAt)}</p>
                </div>
                <div className="l-side"><OutcomeBadge outcome={run.outcome} /></div>
              </li>
            ))}
          </ul>
        )}
        <p className="note" style={{ marginTop: 10 }}>
          <Icon name="info" /> Saatler tarayıcının saat dilimindedir. Sunucu saati: {formatLocalStamp(data.serverTime)}.
        </p>
      </div>
    </>
  );
}
