import { useState, type ReactNode } from 'react';
import { Link, Navigate, NavLink, Outlet, useLocation, useNavigate } from 'react-router-dom';
import { isOrganizerAdmin, useAuth } from '../auth/AuthContext';
import { DemoTag, ErrorAlert, Loading } from './ui';

/** Üst gezinme role göre: herkes Etkinlikler; girişliyse Biletlerim/Siparişlerim; üyeliğe göre Organizatör/Kapı/Yönetim. */
export function AppLayout() {
  const { user, status, logout } = useAuth();
  const navigate = useNavigate();
  const [logoutError, setLogoutError] = useState<unknown>(null);

  const onLogout = async () => {
    setLogoutError(null);
    try {
      await logout();
      navigate('/');
    } catch (error) {
      setLogoutError(error);
    }
  };

  return (
    <>
      <a className="skip" href="#main">İçeriğe geç</a>
      <header className="app-head">
        <div className="wrap app-head-in">
          <Link className="wordmark" to="/">Loca</Link>
          <nav className="app-nav" aria-label="Ana gezinme">
            <NavLink to="/" end>Etkinlikler</NavLink>
            {user && <NavLink to="/biletlerim">Biletlerim</NavLink>}
            {user && <NavLink to="/siparislerim">Siparişlerim</NavLink>}
            {isOrganizerAdmin(user) && <NavLink to="/organizator">Organizatör</NavLink>}
            {user && user.memberships.length > 0 && <NavLink to="/kapi">Kapı</NavLink>}
            {user?.isPlatformAdmin && <NavLink to="/yonetim">Yönetim</NavLink>}
          </nav>
          <div className="who">
            {status === 'loading' && <span>Oturum denetleniyor…</span>}
            {status === 'anonymous' && (
              <>
                <Link className="btn btn-sm" to="/giris">Giriş yap</Link>
                <Link className="btn btn-sm btn-link" to="/kayit">Kayıt ol</Link>
              </>
            )}
            {user && (
              <>
                <span className="who-name">{user.displayName}</span>
                {user.isDemo && <DemoTag label="Demo hesap" />}
                <button type="button" className="btn btn-sm" onClick={() => void onLogout()}>Çıkış yap</button>
              </>
            )}
          </div>
        </div>
      </header>
      <main id="main" tabIndex={-1}>
        {logoutError !== null && <div className="wrap"><ErrorAlert error={logoutError} /></div>}
        <Outlet />
      </main>
      <footer className="app-foot">
        <div className="wrap app-foot-in">
          <span>Loca · portföy ürünü. Tüm etkinlikler, kişiler ve ödemeler sentetiktir.</span>
          {user && !isOrganizerAdmin(user) && <Link to="/organizator">Organizatör başvurusu</Link>}
        </div>
      </footer>
    </>
  );
}

/** Standart ekran gövdesi. */
export function Screen({ children, pad = true }: { children: ReactNode; pad?: boolean }) {
  return (
    <section className={`screen${pad ? ' screen-pad' : ''}`}>
      <div className="wrap">{children}</div>
    </section>
  );
}

/** Korumalı rota: oturum yoksa giriş ekranına yönlendirir ve girişten sonra aynı adrese döner. */
export function RequireAuth({ children }: { children: ReactNode }) {
  const { status, bootstrapFailed, reload } = useAuth();
  const location = useLocation();
  if (status === 'loading') return <Screen><Loading label="Oturum denetleniyor…" /></Screen>;
  if (status === 'anonymous') {
    if (bootstrapFailed) {
      return (
        <Screen>
          <div className="state" role="alert">
            <p className="state-title">Oturum durumu okunamadı</p>
            <p>Sunucuya ulaşılamadı. Bağlantını kontrol edip yeniden dene.</p>
            <p><button type="button" className="btn" onClick={() => void reload()}>Yeniden dene</button></p>
          </div>
        </Screen>
      );
    }
    return <Navigate to="/giris" replace state={{ from: location.pathname + location.search, carry: location.state as unknown }} />;
  }
  return <>{children}</>;
}
