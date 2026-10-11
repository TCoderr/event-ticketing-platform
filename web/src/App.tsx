import { QueryClient, QueryClientProvider } from '@tanstack/react-query';
import { lazy, Suspense, useState, type ReactNode } from 'react';
import { BrowserRouter, Link, Route, Routes } from 'react-router-dom';
import { AuthProvider } from './auth/AuthContext';
import { AppLayout, RequireAuth, Screen } from './components/Layout';
import { Loading, PageHeading } from './components/ui';
import { ApiError, NetworkError } from './lib/api';
import { OrderPage, OrdersPage, TicketPage, TicketsPage } from './pages/AccountPages';
import { LoginPage, RegisterPage } from './pages/AuthPages';
import { DiscoverPage, EventPage } from './pages/CatalogPages';
import { GateLayout, GateScanPage, GateSessionsPage } from './pages/GatePages';
import { HoldPage } from './pages/HoldPage';
import { SessionPage } from './pages/SessionPage';

// Organizatör ve yönetim ekranları ayrı parçalarda yüklenir: müşteri akışı bunların kodunu indirmez.
const OrganizerRoutes = lazy(() => import('./pages/organizer/OrganizerRoutes').then((m) => ({ default: m.OrganizerRoutes })));
const AdminRoutes = lazy(() => import('./pages/admin/AdminRoutes').then((m) => ({ default: m.AdminRoutes })));

function Deferred({ children }: { children: ReactNode }) {
  return <Suspense fallback={<Screen><Loading /></Screen>}>{children}</Suspense>;
}

function createQueryClient(): QueryClient {
  return new QueryClient({
    defaultOptions: {
      queries: {
        // Yalnız sonucu bilinmeyen (ağ / 5xx) okuma hataları yeniden denenir; 4xx yanıtlar kesindir.
        retry: (failureCount, error) =>
          error instanceof NetworkError ? failureCount < 2 : error instanceof ApiError && error.status >= 500 && failureCount < 1,
        staleTime: 0,
      },
      mutations: { retry: false },
    },
  });
}

function NotFoundPage() {
  return (
    <Screen>
      <PageHeading title="Sayfa bulunamadı" />
      <p className="lede">Bu adreste bir sayfa yok.</p>
      <p style={{ marginTop: 16 }}><Link className="btn" to="/">Etkinliklere dön</Link></p>
    </Screen>
  );
}

export function App() {
  const [queryClient] = useState(createQueryClient);
  return (
    <QueryClientProvider client={queryClient}>
      <BrowserRouter>
        <AuthProvider>
          <Routes>
            <Route element={<AppLayout />}>
              <Route index element={<DiscoverPage />} />
              <Route path="etkinlik/:slug" element={<EventPage />} />
              <Route path="seans/:id" element={<SessionPage />} />
              <Route path="giris" element={<LoginPage />} />
              <Route path="kayit" element={<RegisterPage />} />
              <Route path="hold/:id" element={<RequireAuth><HoldPage /></RequireAuth>} />
              <Route path="siparis/:id" element={<RequireAuth><OrderPage /></RequireAuth>} />
              <Route path="siparislerim" element={<RequireAuth><OrdersPage /></RequireAuth>} />
              <Route path="biletlerim" element={<RequireAuth><TicketsPage /></RequireAuth>} />
              <Route path="bilet/:id" element={<RequireAuth><TicketPage /></RequireAuth>} />
              <Route path="organizator/*" element={<RequireAuth><Deferred><OrganizerRoutes /></Deferred></RequireAuth>} />
              <Route path="yonetim/*" element={<RequireAuth><Deferred><AdminRoutes /></Deferred></RequireAuth>} />
              <Route path="*" element={<NotFoundPage />} />
            </Route>
            <Route path="kapi" element={<RequireAuth><GateLayout /></RequireAuth>}>
              <Route index element={<GateSessionsPage />} />
              <Route path=":id" element={<GateScanPage />} />
            </Route>
          </Routes>
        </AuthProvider>
      </BrowserRouter>
    </QueryClientProvider>
  );
}
