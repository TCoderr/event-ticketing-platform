import '../organizer/organizer.css';

import { useQuery, useQueryClient } from '@tanstack/react-query';
import { useState, type ReactNode } from 'react';
import { Navigate, NavLink, Outlet, Route, Routes } from 'react-router-dom';
import { useAuth, useUserKey } from '../../auth/AuthContext';
import { Screen } from '../../components/Layout';
import { ConfirmPanel, DemoTag, EmptyState, ErrorAlert, ErrorState, Loading, OkNote, PageHeading, Status } from '../../components/ui';
import { api, ApiError } from '../../lib/api';
import { EVENT_STATUS_LABELS, formatMoney, ORGANIZATION_STATUS_LABELS } from '../../lib/format';
import type { AdminEvent, AdminOrganization, DeadLetter, DemoPspCharge } from '../../lib/types';
import { OperationsView } from '../organizer/OperationsView';
import { formatLocalStamp, useAction } from '../organizer/shared';

const TABS = [
  { to: 'organizasyonlar', label: 'Organizasyonlar' },
  { to: 'etkinlikler', label: 'Etkinlikler' },
  { to: 'operasyon', label: 'Operasyon' },
  { to: 'dead-letter', label: 'Dead-letter' },
  { to: 'demo-psp', label: 'Demo PSP' },
] as const;

function AdminLayout() {
  const { user } = useAuth();
  if (!user?.isPlatformAdmin) {
    return (
      <Screen>
        <ErrorState error={new ApiError({ status: 403, code: 'forbidden' })} />
      </Screen>
    );
  }
  return (
    <Screen>
      <PageHeading title="Yönetim" />
      <p className="lede">Platform yöneticisi araçları: organizasyon onayı, yayından kaldırma, operasyon kuyruğu ve mesaj kuyruğu.</p>
      <nav className="tabs" aria-label="Yönetim bölümleri">
        {TABS.map((tab) => <NavLink key={tab.to} to={`/yonetim/${tab.to}`}>{tab.label}</NavLink>)}
      </nav>
      <Outlet />
    </Screen>
  );
}

function Section({ title, tag, note, children }: { title: string; tag?: boolean; note: string; children: ReactNode }) {
  return (
    <div style={{ marginTop: 22 }}>
      <h2>{title} {tag && <DemoTag />}</h2>
      <p className="note">{note}</p>
      {children}
    </div>
  );
}

// --- Organizasyonlar ---------------------------------------------------------------------------

function OrganizationsTab() {
  const userKey = useUserKey();
  const queryClient = useQueryClient();
  const key = userKey('admin', 'organizations');
  const query = useQuery({ queryKey: key, queryFn: () => api.get<AdminOrganization[]>('/api/admin/organizations') });
  const [confirming, setConfirming] = useState<string | null>(null);
  const action = useAction();

  const transition = (organization: AdminOrganization, verb: 'approve' | 'suspend' | 'reinstate') =>
    void action.run(organization.id, async () => {
      await api.post(`/api/admin/organizations/${organization.id}/${verb}`);
      setConfirming(null);
      await queryClient.invalidateQueries({ queryKey: key });
      return `${organization.name}: ${verb === 'approve' ? 'onaylandı' : verb === 'suspend' ? 'askıya alındı' : 'yeniden etkinleştirildi'}.`;
    }).then((ok) => {
      if (!ok) {
        setConfirming(null);
        void queryClient.invalidateQueries({ queryKey: key });
      }
    });

  return (
    <Section title="Organizasyonlar" note="Yalnız aktif organizasyon yayın yapabilir. Askıya alınan organizasyonun seansları keşiften kalkar.">
      {query.isPending ? <Loading label="Organizasyonlar yükleniyor…" />
        : query.isError ? <ErrorState error={query.error} onRetry={() => void query.refetch()} />
          : query.data.length === 0 ? <EmptyState title="Kayıtlı organizasyon yok." />
            : (
              <>
                <ErrorAlert error={action.error} />
                {action.done && <OkNote>{action.done}</OkNote>}
                <ul className="list" data-testid="admin-organizations">
                  {query.data.map((organization) => (
                    <li key={organization.id} data-status={organization.status}>
                      <div>
                        <p className="l-title break">{organization.name} {organization.isDemo && <DemoTag />}</p>
                        <p className="l-meta">{organization.memberCount} üye · başvuru {formatLocalStamp(organization.createdAt)}</p>
                      </div>
                      <div className="l-side">
                        {organization.status === 'Active' && <Status tone="ok" icon="check">{ORGANIZATION_STATUS_LABELS.Active}</Status>}
                        {organization.status === 'PendingApproval' && <Status tone="info" icon="clock">{ORGANIZATION_STATUS_LABELS.PendingApproval}</Status>}
                        {organization.status === 'Suspended' && <Status tone="err" icon="cross">{ORGANIZATION_STATUS_LABELS.Suspended}</Status>}
                        {organization.status === 'PendingApproval' && (
                          <button type="button" className="btn btn-sm btn-primary" disabled={action.busy !== null} onClick={() => transition(organization, 'approve')}>
                            {action.busy === organization.id ? 'Onaylanıyor…' : 'Onayla'}<span className="sr-only"> — {organization.name}</span>
                          </button>
                        )}
                        {organization.status === 'Active' && confirming !== organization.id && (
                          <button type="button" className="btn btn-sm" disabled={action.busy !== null} onClick={() => { setConfirming(organization.id); action.reset(); }}>
                            Askıya al<span className="sr-only"> — {organization.name}</span>
                          </button>
                        )}
                        {organization.status === 'Suspended' && (
                          <button type="button" className="btn btn-sm" disabled={action.busy !== null} onClick={() => transition(organization, 'reinstate')}>
                            {action.busy === organization.id ? 'Geri alınıyor…' : 'Geri al'}<span className="sr-only"> — {organization.name}</span>
                          </button>
                        )}
                      </div>
                      {confirming === organization.id && (
                        <div className="span-all">
                          <ConfirmPanel title="Organizasyon askıya alınsın mı?" confirmLabel="Evet, askıya al" busy={action.busy !== null}
                            onConfirm={() => transition(organization, 'suspend')} onCancel={() => setConfirming(null)}>
                            <p>{organization.name} yayın yapamaz ve yayındaki seansları keşiften kalkar. Mevcut siparişler ve biletler etkilenmez; işlem geri alınabilir.</p>
                          </ConfirmPanel>
                        </div>
                      )}
                    </li>
                  ))}
                </ul>
              </>
            )}
    </Section>
  );
}

// --- Etkinlikler -------------------------------------------------------------------------------

function EventsTab() {
  const userKey = useUserKey();
  const queryClient = useQueryClient();
  const key = userKey('admin', 'events');
  const query = useQuery({ queryKey: key, queryFn: () => api.get<AdminEvent[]>('/api/admin/events') });
  const [confirming, setConfirming] = useState<string | null>(null);
  const action = useAction();

  const moderate = (event: AdminEvent, verb: 'takedown' | 'clear-takedown') =>
    void action.run(event.id, async () => {
      await api.post(`/api/admin/events/${event.id}/${verb}`);
      setConfirming(null);
      await queryClient.invalidateQueries({ queryKey: key });
      return `${event.title}: ${verb === 'takedown' ? 'yayından kaldırıldı' : 'kaldırma geri alındı'}.`;
    }).then((ok) => {
      if (!ok) setConfirming(null);
    });

  return (
    <Section title="Etkinlikler" note="Yayından kaldırılan etkinlik ve seansları keşifte görünmez; organizatör yeniden yayınlayamaz.">
      {query.isPending ? <Loading label="Etkinlikler yükleniyor…" />
        : query.isError ? <ErrorState error={query.error} onRetry={() => void query.refetch()} />
          : query.data.length === 0 ? <EmptyState title="Kayıtlı etkinlik yok." />
            : (
              <>
                <ErrorAlert error={action.error} />
                {action.done && <OkNote>{action.done}</OkNote>}
                <ul className="list" data-testid="admin-events">
                  {query.data.map((event) => (
                    <li key={event.id} data-blocked={event.moderationBlocked}>
                      <div>
                        <p className="l-title break">{event.title} {event.isDemo && <DemoTag />}</p>
                        <p className="l-meta break">{event.organizationName} · {EVENT_STATUS_LABELS[event.status]}</p>
                      </div>
                      <div className="l-side">
                        {event.moderationBlocked
                          ? <Status tone="err" icon="cross">Yayından kaldırıldı</Status>
                          : <Status tone="mute" icon="planned">Kısıtlama yok</Status>}
                        {event.moderationBlocked ? (
                          <button type="button" className="btn btn-sm" disabled={action.busy !== null} onClick={() => moderate(event, 'clear-takedown')}>
                            {action.busy === event.id ? 'Geri alınıyor…' : 'Geri al'}<span className="sr-only"> — {event.title}</span>
                          </button>
                        ) : confirming !== event.id && (
                          <button type="button" className="btn btn-sm" disabled={action.busy !== null} onClick={() => { setConfirming(event.id); action.reset(); }}>
                            Yayından kaldır<span className="sr-only"> — {event.title}</span>
                          </button>
                        )}
                      </div>
                      {confirming === event.id && (
                        <div className="span-all">
                          <ConfirmPanel title="Etkinlik yayından kaldırılsın mı?" confirmLabel="Evet, yayından kaldır" busy={action.busy !== null}
                            onConfirm={() => moderate(event, 'takedown')} onCancel={() => setConfirming(null)}>
                            <p>“{event.title}” ve seansları keşiften kalkar; yeni rezervasyon alınamaz. Mevcut biletler geçerli kalır; işlem geri alınabilir.</p>
                          </ConfirmPanel>
                        </div>
                      )}
                    </li>
                  ))}
                </ul>
              </>
            )}
    </Section>
  );
}

// --- Dead-letter -------------------------------------------------------------------------------

function DeadLettersTab() {
  const userKey = useUserKey();
  const queryClient = useQueryClient();
  const key = userKey('admin', 'dead-letters');
  const query = useQuery({ queryKey: key, queryFn: () => api.get<DeadLetter[]>('/api/admin/outbox/dead-letters') });
  const action = useAction();

  const replay = (message: DeadLetter) =>
    void action.run(message.id, async () => {
      await api.post(`/api/admin/outbox/${message.id}/replay`);
      await queryClient.invalidateQueries({ queryKey: key });
      // Yeniden kuyruğa alındı: işlenmesinin başarısı ayrıca izlenir, burada başarı varsayılmaz.
      return `${message.type} mesajı yeniden kuyruğa alındı. İşlenip işlenmediği bu listeden ve operasyon kuyruğundan izlenir.`;
    }).then((ok) => {
      if (!ok) void queryClient.invalidateQueries({ queryKey: key });
    });

  return (
    <Section title="Dead-letter mesajları" note="Deneme bütçesi tükenen giden kutusu (outbox) mesajları. Yeniden oynatma mesajı kuyruğa geri alır; sonucu garanti etmez.">
      {query.isPending ? <Loading label="Mesajlar yükleniyor…" />
        : query.isError ? <ErrorState error={query.error} onRetry={() => void query.refetch()} />
          : (
            <>
              <ErrorAlert error={action.error} />
              {action.done && <OkNote>{action.done}</OkNote>}
              {query.data.length === 0 ? <EmptyState title="Dead-letter kuyruğu boş."><p>İşlenemeyen mesaj yok.</p></EmptyState> : (
                <div className="table-wrap">
                  <table className="ops" data-testid="admin-dead-letters">
                    <caption className="sr-only">Dead-letter mesajları</caption>
                    <thead>
                      <tr>
                        <th scope="col">Tür</th>
                        <th scope="col">Deneme</th>
                        <th scope="col">Son hata kodu</th>
                        <th scope="col">Oluşturuldu</th>
                        <th scope="col">Dead-letter</th>
                        <th scope="col">Eylem</th>
                      </tr>
                    </thead>
                    <tbody>
                      {query.data.map((message) => (
                        <tr key={message.id}>
                          <th scope="row" className="kind" data-l="Tür"><span className="mono break">{message.type}</span></th>
                          <td className="num" data-l="Deneme">{message.attemptCount}</td>
                          <td data-l="Son hata"><span className="mono break">{message.lastErrorCode ?? '—'}</span></td>
                          <td data-l="Oluşturuldu">{formatLocalStamp(message.createdAt)}</td>
                          <td data-l="Dead-letter">{message.deadLetteredAt ? formatLocalStamp(message.deadLetteredAt) : '—'}</td>
                          <td className="act">
                            <button type="button" className="btn btn-sm" disabled={action.busy !== null} onClick={() => replay(message)}>
                              {action.busy === message.id ? 'Kuyruğa alınıyor…' : 'Yeniden oynat'}<span className="sr-only"> — {message.type}</span>
                            </button>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </>
          )}
    </Section>
  );
}

// --- Demo PSP ----------------------------------------------------------------------------------

function DemoPspTab() {
  const userKey = useUserKey();
  const queryClient = useQueryClient();
  const key = userKey('admin', 'demo-psp', 'pending');
  const query = useQuery({
    queryKey: key,
    queryFn: () => api.get<DemoPspCharge[]>('/api/admin/demo-psp/charges?status=pending'),
    retry: false,
    refetchInterval: (q) => (q.state.error ? false : 5000),
  });
  const action = useAction();

  const resolve = (charge: DemoPspCharge, outcome: 'succeeded' | 'declined') =>
    void action.run(`${charge.merchantReference}:${outcome}`, async () => {
      const result = await api.post<DemoPspCharge>(
        `/api/admin/demo-psp/charges/${encodeURIComponent(charge.merchantReference)}/resolve`, { outcome });
      await queryClient.invalidateQueries({ queryKey: key });
      return `Simülatör tahsilatı “${result.status}” olarak işaretledi. Siparişin durumu bildirim (webhook) ya da mutabakatla güncellenir.`;
    }).then((ok) => {
      if (!ok) void queryClient.invalidateQueries({ queryKey: key });
    });

  const disabled = query.isError && query.error instanceof ApiError && query.error.status === 404;
  return (
    <Section title="Demo ödeme sağlayıcısı paneli" tag
      note="Simüle PSP'nin yönetim paneli karşılığı: sonucu bekleyen sentetik tahsilatlar. Gerçek ödeme ya da kart verisi yoktur.">
      {query.isPending ? <Loading label="Bekleyen tahsilatlar yükleniyor…" />
        : disabled ? (
          <EmptyState title="Demo PSP bu kurulumda kapalı."><p>Simülatör etkin olmadığı için panel kullanılamıyor.</p></EmptyState>
        ) : query.isError ? <ErrorState error={query.error} onRetry={() => void query.refetch()} />
          : (
            <>
              <ErrorAlert error={action.error} />
              {action.done && <OkNote>{action.done}</OkNote>}
              {query.data.length === 0 ? (
                <EmptyState title="Sonucu bekleyen tahsilat yok."><p>Asenkron ya da geç başarı senaryolu ödemeler burada görünür.</p></EmptyState>
              ) : (
                <div className="table-wrap">
                  <table className="ops" data-testid="demo-psp-charges">
                    <caption className="sr-only">Sonucu bekleyen simüle tahsilatlar</caption>
                    <thead>
                      <tr>
                        <th scope="col">Üye işyeri referansı</th>
                        <th scope="col">Tutar</th>
                        <th scope="col">Durum</th>
                        <th scope="col">Sıra no</th>
                        <th scope="col">Eylem</th>
                      </tr>
                    </thead>
                    <tbody>
                      {query.data.map((charge) => (
                        <tr key={charge.id}>
                          <th scope="row" data-l="Referans"><span className="mono break">{charge.merchantReference}</span></th>
                          <td className="num" data-l="Tutar">{formatMoney(charge.amountMinor)}</td>
                          <td data-l="Durum"><span className="mono">{charge.status}</span>{charge.declineCode ? <span className="res-note mono">{charge.declineCode}</span> : null}</td>
                          <td className="num" data-l="Sıra no">{charge.sequence}</td>
                          <td className="act">
                            <span className="cell-actions">
                              <button type="button" className="btn btn-sm" disabled={action.busy !== null} onClick={() => resolve(charge, 'succeeded')}>
                                Başarılı olarak sonuçlandır<span className="sr-only"> — {charge.merchantReference}</span>
                              </button>
                              <button type="button" className="btn btn-sm" disabled={action.busy !== null} onClick={() => resolve(charge, 'declined')}>
                                Reddedildi olarak sonuçlandır<span className="sr-only"> — {charge.merchantReference}</span>
                              </button>
                            </span>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </>
          )}
    </Section>
  );
}

export function AdminRoutes() {
  return (
    <Routes>
      <Route element={<AdminLayout />}>
        <Route index element={<Navigate to="/yonetim/organizasyonlar" replace />} />
        <Route path="organizasyonlar" element={<OrganizationsTab />} />
        <Route path="etkinlikler" element={<EventsTab />} />
        <Route path="operasyon" element={<OperationsView scope="admin" />} />
        <Route path="dead-letter" element={<DeadLettersTab />} />
        <Route path="demo-psp" element={<DemoPspTab />} />
        <Route path="*" element={<Navigate to="/yonetim/organizasyonlar" replace />} />
      </Route>
    </Routes>
  );
}
