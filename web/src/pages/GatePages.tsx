import { useQuery, useQueryClient } from '@tanstack/react-query';
import { lazy, Suspense, useCallback, useEffect, useRef, useState, type FormEvent } from 'react';
import { Link, Outlet, useNavigate, useParams } from 'react-router-dom';
import { useAuth, useUserKey } from '../auth/AuthContext';
import { GateResultPanel, gateResultFromError, gateResultFromResponse, type GateResult } from '../components/GateResultPanel';
import { Icon } from '../components/Icon';
import { DemoTag, PageHeading, SessionTime } from '../components/ui';
import { api, ApiError, newIdempotencyKey } from '../lib/api';
import { describeError } from '../lib/errors';
import { formatInZone } from '../lib/format';
import type { CheckInResponse, GateSession } from '../lib/types';

// QR çözücü (jsQR) yalnız tarama ekranında indirilir.
const CameraScanner = lazy(() => import('../components/CameraScanner').then((m) => ({ default: m.CameraScanner })));

/** Kapı kabuğu: tek sütun, mobil öncelikli, koyu yüksek kontrast. */
export function GateLayout() {
  const { user, logout } = useAuth();
  const navigate = useNavigate();
  useEffect(() => {
    document.body.classList.add('dark');
    return () => document.body.classList.remove('dark');
  }, []);
  return (
    <div className="gate-shell">
      <div className="gate">
        <a className="skip" href="#main">İçeriğe geç</a>
        <div className="gate-col">
          <header className="gate-head">
            <Link className="wordmark" to="/kapi">Loca Kapı</Link>
            <nav className="gate-links" aria-label="Kapı gezinmesi">
              {user?.isDemo && <span style={{ alignSelf: 'center' }}><DemoTag label="Demo hesap" /></span>}
              <Link to="/kapi">Seanslar</Link>
              <Link to="/">Ana sayfa</Link>
              <button type="button" onClick={() => void logout().then(() => navigate('/')).catch(() => undefined)}>Çıkış yap</button>
            </nav>
          </header>
          <main id="main" tabIndex={-1}>
            <Outlet />
          </main>
        </div>
      </div>
    </div>
  );
}

function useGateSessions() {
  const userKey = useUserKey();
  return useQuery({
    queryKey: userKey('gate-sessions'),
    queryFn: () => api.get<GateSession[]>('/api/gate/sessions'),
    refetchInterval: (query) => (query.state.error ? false : 15_000),
  });
}

function GateError({ error, onRetry }: { error: unknown; onRetry: () => void }) {
  return (
    <div className="g-state" role="alert">
      <p><strong>Yüklenemedi.</strong> {describeError(error)}</p>
      <button type="button" className="g-btn" onClick={onRetry}>Yeniden dene</button>
    </div>
  );
}

export function GateSessionsPage() {
  const sessions = useGateSessions();
  return (
    <>
      <PageHeading title="Kapı kontrolü" />
      <p className="g-hint">Görevli olduğun seansı seç. Yalnız atandığın seanslar listelenir.</p>
      {sessions.isPending ? <div className="g-state" role="status" aria-busy="true"><p>Seanslar yükleniyor…</p></div>
        : sessions.isError ? <GateError error={sessions.error} onRetry={() => void sessions.refetch()} />
          : sessions.data.length === 0 ? (
            <div className="g-state">
              <p><strong>Atandığın seans yok.</strong></p>
              <p className="g-hint">Organizatör seni bir seansın kapı ekibine eklediğinde burada görünür.</p>
            </div>
          ) : (
            <ul className="g-list" data-testid="gate-sessions">
              {sessions.data.map((session) => (
                <li key={session.sessionId}>
                  <Link to={`/kapi/${session.sessionId}`}>
                    <span className="g-title">{session.eventTitle}</span>
                    <SessionTime startsAt={session.startsAt} timezone={session.timezone} />
                    <span className="g-meta" style={{ display: 'block' }}>
                      {session.venueName}, {session.city} ·{' '}
                      {session.status === 'Cancelled' ? 'Seans iptal edildi' : session.windowOpen ? 'Giriş penceresi açık' : 'Giriş penceresi kapalı'} ·{' '}
                      {session.ticketsCheckedIn} / {session.ticketsTotal} giriş
                    </span>
                  </Link>
                </li>
              ))}
            </ul>
          )}
    </>
  );
}

interface PendingScan {
  token: string;
  gate: string | null;
  /** Tarama başına BİR anahtar; yeniden denemeler aynı anahtarla gider. */
  key: string;
  attempts: number;
}

const AUTO_RETRY_MS = 4000;
const AUTO_RETRY_LIMIT = 5;

export function GateScanPage() {
  const { id = '' } = useParams();
  const userKey = useUserKey();
  const queryClient = useQueryClient();
  const sessions = useGateSessions();
  const session = sessions.data?.find((s) => s.sessionId === id);

  const [result, setResult] = useState<GateResult>({ kind: 'idle' });
  const [token, setToken] = useState('');
  const [gate, setGate] = useState('');
  const [inputError, setInputError] = useState<string | null>(null);
  const [online, setOnline] = useState(() => navigator.onLine);
  const pendingRef = useRef<PendingScan | null>(null);
  const sendingRef = useRef(false);
  const tokenInput = useRef<HTMLInputElement>(null);
  const panel = useRef<HTMLDivElement>(null);

  const send = useCallback(async () => {
    const scan = pendingRef.current;
    if (!scan || sendingRef.current) return;
    // Çevrimdışıyken istek gönderilmez; offline check-in yoktur.
    if (!navigator.onLine) {
      if (scan.attempts === 0) {
        pendingRef.current = null;
        setResult({ kind: 'device-offline' });
      }
      return;
    }
    sendingRef.current = true;
    scan.attempts += 1;
    setResult({ kind: 'pending' });
    try {
      const response = await api.post<CheckInResponse>(
        `/api/gate/sessions/${id}/check-ins`, { token: scan.token, gate: scan.gate }, { idempotencyKey: scan.key });
      pendingRef.current = null;
      setResult(gateResultFromResponse(response));
      void queryClient.invalidateQueries({ queryKey: userKey('gate-sessions') });
    } catch (error) {
      if (error instanceof ApiError && error.code === 'unauthenticated') return; // Oturum bitti: giriş ekranına yönlenilir.
      const next = gateResultFromError(error, scan.attempts);
      if (next.kind !== 'offline') pendingRef.current = null; // Kesin yanıt: bu tarama kapandı.
      setResult(next);
    } finally {
      sendingRef.current = false;
    }
  }, [id, queryClient, userKey]);

  const submit = useCallback((raw: string) => {
    const value = raw.trim();
    if (!value || sendingRef.current) return;
    pendingRef.current = { token: value, gate: gate.trim() || null, key: newIdempotencyKey(), attempts: 0 };
    void send();
  }, [gate, send]);

  useEffect(() => {
    const update = () => {
      setOnline(navigator.onLine);
      if (navigator.onLine && pendingRef.current) void send(); // Bağlantı geldi: bekleyen tarama aynı anahtarla yeniden denenir.
    };
    window.addEventListener('online', update);
    window.addEventListener('offline', update);
    return () => {
      window.removeEventListener('online', update);
      window.removeEventListener('offline', update);
    };
  }, [send]);

  // Sonuç bilinmiyorsa aynı tarama kendiliğinden yeniden denenir (sınırlı sayıda); başarı asla varsayılmaz.
  const offlineAttempts = result.kind === 'offline' ? result.attempts : null;
  useEffect(() => {
    if (offlineAttempts === null || offlineAttempts >= AUTO_RETRY_LIMIT) return;
    const timer = setTimeout(() => void send(), AUTO_RETRY_MS);
    return () => clearTimeout(timer);
  }, [offlineAttempts, send]);

  // Sonuç geldiğinde odak sonuç panelindeki eyleme taşınır.
  const resultKind = result.kind;
  useEffect(() => {
    if (resultKind === 'idle' || resultKind === 'pending') return;
    panel.current?.querySelector<HTMLElement>('[data-gate-focus]')?.focus({ preventScroll: true });
    window.scrollTo(0, 0);
  }, [resultKind]);

  const next = () => {
    pendingRef.current = null;
    setResult({ kind: 'idle' });
    setToken('');
    setInputError(null);
    tokenInput.current?.focus();
  };

  const onSubmit = (event: FormEvent) => {
    event.preventDefault();
    if (!token.trim()) {
      setInputError('Kodu yaz veya bileti okut.');
      tokenInput.current?.focus();
      return;
    }
    setInputError(null);
    submit(token);
  };

  if (sessions.isPending) return <div className="g-state" role="status" aria-busy="true"><p>Seans yükleniyor…</p></div>;
  if (sessions.isError && !session) return <GateError error={sessions.error} onRetry={() => void sessions.refetch()} />;
  if (!session) {
    return (
      <div className="g-state" role="alert">
        <p><strong>Bulunamadı.</strong> Bu seans yok ya da kapı ekibinde değilsin.</p>
        <Link className="g-btn" to="/kapi">Seans listesine dön</Link>
      </div>
    );
  }

  const busy = result.kind === 'pending';
  return (
    <>
      <PageHeading title={session.eventTitle} />
      <p className="ctx">
        <SessionTime startsAt={session.startsAt} timezone={session.timezone} />
        <span className="g-meta">{session.venueName}, {session.city}</span>
      </p>
      <p className="g-counts" role="status">
        <span>Giriş yapan: <strong className="mono">{session.ticketsCheckedIn} / {session.ticketsTotal}</strong></span>
        <span>
          Giriş penceresi: {formatInZone(session.checkInOpensAt, session.timezone)} – {formatInZone(session.checkInClosesAt, session.timezone)}
        </span>
      </p>

      {!online && (
        <p className="g-banner" role="alert"><Icon name="offline" /><span>Çevrimdışı — check-in yapılamaz. Bağlantı gelene kadar tarama gönderilmez.</span></p>
      )}
      {session.status === 'Cancelled' && (
        <p className="g-banner" role="alert"><Icon name="cross" /><span>Bu seans iptal edildi. Biletler geçersiz; içeri alma.</span></p>
      )}
      {session.status !== 'Cancelled' && !session.windowOpen && (
        <p className="g-banner" role="status"><Icon name="clock" /><span>Giriş penceresi şu anda kapalı. Taramalar “GEÇERSİZ · pencere dışı” sonucu verir.</span></p>
      )}

      <div ref={panel}>
        <GateResultPanel result={result} timezone={session.timezone} onNext={next} onRetry={() => void send()} />
      </div>

      <Suspense fallback={<div className="scan"><p className="g-hint" role="status">Kamera bileşeni yükleniyor…</p></div>}>
        <CameraScanner onScan={submit} paused={result.kind !== 'idle'} />
      </Suspense>

      <form className="g-form" onSubmit={onSubmit} noValidate>
        <label htmlFor="g-token">Elle kod girişi</label>
        <input
          id="g-token" ref={tokenInput} name="token" type="text" inputMode="text" autoComplete="off" autoCapitalize="off" spellCheck={false}
          value={token} onChange={(e) => setToken(e.target.value)}
          aria-describedby="g-hint g-err" aria-invalid={inputError ? true : undefined}
        />
        <p className="g-hint" id="g-hint">Kamera kullanılamıyorsa biletteki kodu (LT1. ile başlar) yaz.</p>
        <p className="g-err" id="g-err" role="alert">{inputError && <><Icon name="warn" /><span>{inputError}</span></>}</p>
        <label htmlFor="g-gate">Kapı adı (isteğe bağlı)</label>
        <input id="g-gate" className="g-input" type="text" maxLength={40} autoComplete="off" value={gate} onChange={(e) => setGate(e.target.value)} />
        <button type="submit" className="g-btn pri" disabled={busy}>{busy ? 'Kontrol ediliyor…' : 'Kontrol et'}</button>
      </form>
    </>
  );
}
