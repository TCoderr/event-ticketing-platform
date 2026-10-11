import './organizer.css';

import { useQuery } from '@tanstack/react-query';
import { useState, type FormEvent } from 'react';
import { Link, Navigate, Outlet, Route, Routes, useLocation, useParams } from 'react-router-dom';
import { useAuth, useUserKey } from '../../auth/AuthContext';
import { Screen } from '../../components/Layout';
import { DemoTag, ErrorAlert, ErrorState, PageHeading, Status } from '../../components/ui';
import { api } from '../../lib/api';
import { ORGANIZATION_STATUS_LABELS } from '../../lib/format';
import type { Organization } from '../../lib/types';
import { EventPage, EventsPage } from './EventsPages';
import { OperationsView } from './OperationsView';
import { SessionEditorPage } from './SessionEditorPage';
import { SessionSummaryPage } from './SessionSummaryPage';
import { notFoundError, orgPath, TextField, useAction } from './shared';
import { TeamPage } from './TeamPage';
import { VenuesPage } from './VenuesPage';

function useAdminMemberships() {
  const { user } = useAuth();
  return (user?.memberships ?? []).filter((m) => m.role === 'OrganizerAdmin');
}

function useMyOrganizations() {
  const userKey = useUserKey();
  return useQuery({
    queryKey: userKey('organizations'),
    queryFn: () => api.get<Organization[]>('/api/organizer/organizations'),
    staleTime: 30_000,
  });
}

/** Organizatör üyeliği olmayan kullanıcı için başvuru. Başvuru platform yöneticisi onaylayana kadar "Onay bekliyor" kalır. */
function ApplyForm() {
  const { reload } = useAuth();
  const [name, setName] = useState('');
  const [nameError, setNameError] = useState<string | null>(null);
  const action = useAction();

  const submit = (event: FormEvent) => {
    event.preventDefault();
    const trimmed = name.trim();
    if (trimmed.length < 3 || trimmed.length > 160) {
      setNameError('Organizasyon adı 3–160 karakter olmalı.');
      return;
    }
    setNameError(null);
    void action.run('apply', async () => {
      await api.post<Organization>('/api/organizations', { name: trimmed });
      // Üyelik sunucuda oluştu; kullanıcı yeniden okununca bu sayfa organizasyona yönlenir.
      await reload();
    });
  };

  return (
    <Screen>
      <PageHeading title="Organizatör başvurusu" />
      <p className="lede">
        Etkinlik yayınlamak için bir organizasyon başvurusu yap. Başvuru “Onay bekliyor” durumunda açılır; platform yöneticisi
        onaylayana kadar taslak hazırlayabilirsin ama yayın yapamazsın.
      </p>
      <form className="form" style={{ marginTop: 20 }} onSubmit={submit} noValidate>
        <ErrorAlert error={action.error} />
        <TextField label="Organizasyon adı" value={name} onChange={setName} error={nameError} maxLength={160} autoComplete="organization" />
        <div className="row-actions">
          <button type="submit" className="btn btn-primary" disabled={action.busy !== null}>
            {action.busy ? 'Başvuru gönderiliyor…' : 'Başvuruyu gönder'}
          </button>
        </div>
      </form>
    </Screen>
  );
}

function OrganizationPicker() {
  const admins = useAdminMemberships();
  const organizations = useMyOrganizations();
  return (
    <Screen>
      <PageHeading title="Organizatör" />
      <p className="lede">Birden çok organizasyonun yöneticisisin. Çalışmak istediğin organizasyonu seç.</p>
      <ul className="list" data-testid="org-picker">
        {admins.map((membership) => {
          const isDemo = organizations.data?.find((o) => o.id === membership.organizationId)?.isDemo === true;
          return (
            <li key={membership.organizationId}>
              <div>
                <p className="l-title">
                  <Link to={orgPath(membership.organizationId, 'etkinlikler')}>{membership.organizationName}</Link>{' '}
                  {isDemo && <DemoTag />}
                </p>
              </div>
              <div className="l-side">
                <span className="chip">{ORGANIZATION_STATUS_LABELS[membership.organizationStatus]}</span>
                <Link className="btn btn-sm" to={orgPath(membership.organizationId, 'etkinlikler')}>
                  Aç<span className="sr-only"> — {membership.organizationName}</span>
                </Link>
              </div>
            </li>
          );
        })}
      </ul>
    </Screen>
  );
}

function OrganizerIndex() {
  const admins = useAdminMemberships();
  if (admins.length === 0) return <ApplyForm />;
  if (admins.length === 1) return <Navigate to={orgPath(admins[0]!.organizationId, 'etkinlikler')} replace />;
  return <OrganizationPicker />;
}

const SECTIONS = [
  { to: 'etkinlikler', label: 'Etkinlikler', segments: ['etkinlikler', 'etkinlik', 'seans'] },
  { to: 'mekanlar', label: 'Mekanlar', segments: ['mekanlar'] },
  { to: 'ekip', label: 'Ekip', segments: ['ekip'] },
  { to: 'operasyon', label: 'Operasyon', segments: ['operasyon'] },
] as const;

/** Sol gezinme (masaüstü) / üst sekmeler (mobil). Yetkinin kesin kararı API'dedir; buradaki denetim yalnız gösterimdir. */
function OrganizerLayout() {
  const { orgId = '' } = useParams();
  const location = useLocation();
  const admins = useAdminMemberships();
  const organizations = useMyOrganizations();
  const membership = admins.find((m) => m.organizationId === orgId);

  if (!membership) {
    return (
      <Screen>
        <ErrorState error={notFoundError()} notFoundText="Bu organizasyon yok ya da yöneticisi değilsin." />
      </Screen>
    );
  }

  const segment = location.pathname.split('/')[3] ?? '';
  const isDemo = organizations.data?.find((o) => o.id === orgId)?.isDemo === true;
  const status = membership.organizationStatus;

  return (
    <section className="screen screen-pad" style={{ paddingTop: 0 }}>
      <div className="wrap org">
        <nav className="org-nav" aria-label="Organizatör gezinmesi">
          <p className="org-name">
            Organizatör
            <strong className="break">{membership.organizationName} {isDemo && <DemoTag />}</strong>
            {admins.length > 1 && <Link className="org-switch" to="/organizator">Organizasyon değiştir</Link>}
          </p>
          <ul>
            {SECTIONS.map((section) => (
              <li key={section.to}>
                <Link
                  to={orgPath(orgId, section.to)}
                  aria-current={(section.segments as readonly string[]).includes(segment) ? 'page' : undefined}
                >
                  {section.label}
                </Link>
              </li>
            ))}
          </ul>
        </nav>
        <div className="org-main">
          {status !== 'Active' && (
            <div className="note-box is-warn" role="status" style={{ marginTop: 0, marginBottom: 18 }}>
              <Status tone="warn" icon="warn">
                {status === 'PendingApproval' ? 'Onay bekliyor' : 'Organizasyon askıda'}
              </Status>
              <p>
                {status === 'PendingApproval'
                  ? 'Başvurun platform yöneticisinin onayını bekliyor. Taslak hazırlayabilirsin; onaylanana kadar yayın yapılamaz.'
                  : 'Organizasyon platform yönetimi tarafından askıya alındı. Yayın yapılamaz; yayındaki seanslar keşifte görünmez.'}
              </p>
            </div>
          )}
          <Outlet />
        </div>
      </div>
    </section>
  );
}

function OrganizationHome() {
  const { orgId = '' } = useParams();
  return <Navigate to={orgPath(orgId, 'etkinlikler')} replace />;
}

function OperationsPage() {
  const { orgId = '' } = useParams();
  return (
    <>
      <PageHeading title="Operasyon kuyruğu" />
      <OperationsView scope="organizer" organizationId={orgId} />
    </>
  );
}

function OrganizerNotFound() {
  return <ErrorState error={notFoundError()} notFoundText="Bu adreste bir organizatör sayfası yok." />;
}

export function OrganizerRoutes() {
  return (
    <Routes>
      <Route index element={<OrganizerIndex />} />
      <Route path=":orgId" element={<OrganizerLayout />}>
        <Route index element={<OrganizationHome />} />
        <Route path="etkinlikler" element={<EventsPage />} />
        <Route path="etkinlik/:eventId" element={<EventPage />} />
        <Route path="mekanlar" element={<VenuesPage />} />
        <Route path="seans/:sessionId" element={<SessionEditorPage />} />
        <Route path="seans/:sessionId/ozet" element={<SessionSummaryPage />} />
        <Route path="ekip" element={<TeamPage />} />
        <Route path="operasyon" element={<OperationsPage />} />
        <Route path="*" element={<OrganizerNotFound />} />
      </Route>
    </Routes>
  );
}
