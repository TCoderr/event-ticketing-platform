import { useState, type FormEvent } from 'react';
import { Link, Navigate, useLocation, useNavigate } from 'react-router-dom';
import { useAuth } from '../auth/AuthContext';
import { Screen } from '../components/Layout';
import { ErrorAlert, Field, Loading, PageHeading } from '../components/ui';
import { invalidFields } from '../lib/errors';

interface ReturnState {
  from?: string;
  carry?: unknown;
}

function useReturnTarget(): { from: string; carry: unknown; state: ReturnState } {
  const location = useLocation();
  const state = (location.state ?? {}) as ReturnState;
  // Yalnız uygulama içi yollar: açık yönlendirme (open redirect) kabul edilmez.
  const from = typeof state.from === 'string' && state.from.startsWith('/') && !state.from.startsWith('//') ? state.from : '/';
  return { from, carry: state.carry, state };
}

export function LoginPage() {
  const { status, login } = useAuth();
  const navigate = useNavigate();
  const { from, carry, state } = useReturnTarget();
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [errors, setErrors] = useState<{ email?: string; password?: string }>({});
  const [failure, setFailure] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  if (status === 'loading') return <Screen><Loading label="Oturum denetleniyor…" /></Screen>;
  if (status === 'authenticated' && !busy) return <Navigate to={from} replace state={carry} />;

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();
    const next: typeof errors = {};
    if (!email.trim()) next.email = 'E-posta adresini yaz.';
    if (!password) next.password = 'Parolanı yaz.';
    setErrors(next);
    setFailure(null);
    if (Object.keys(next).length > 0) return;
    setBusy(true);
    try {
      await login(email.trim(), password);
      navigate(from, { replace: true, state: carry });
    } catch (error) {
      setFailure(error);
      setBusy(false);
    }
  };

  return (
    <Screen>
      <div className="auth">
        <PageHeading title="Giriş yap" />
        {from !== '/' && <p className="lede">Devam etmek için giriş yap; ardından kaldığın yere dönersin.</p>}
        <form className="form" style={{ marginTop: 20 }} onSubmit={(e) => void onSubmit(e)} noValidate>
          <ErrorAlert error={failure} />
          <Field label="E-posta" error={errors.email}>
            {(control) => (
              <input {...control} type="email" name="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} required />
            )}
          </Field>
          <Field label="Parola" error={errors.password}>
            {(control) => (
              <input {...control} type="password" name="password" autoComplete="current-password" value={password} onChange={(e) => setPassword(e.target.value)} required />
            )}
          </Field>
          <div className="row-actions">
            <button type="submit" className="btn btn-primary" disabled={busy}>{busy ? 'Giriş yapılıyor…' : 'Giriş yap'}</button>
            <Link className="btn btn-link" to="/kayit" state={state}>Hesabın yok mu? Kayıt ol</Link>
          </div>
        </form>
      </div>
    </Screen>
  );
}

export function RegisterPage() {
  const { status, register } = useAuth();
  const navigate = useNavigate();
  const { from, carry, state } = useReturnTarget();
  const [displayName, setDisplayName] = useState('');
  const [email, setEmail] = useState('');
  const [password, setPassword] = useState('');
  const [errors, setErrors] = useState<{ displayName?: string; email?: string; password?: string }>({});
  const [failure, setFailure] = useState<unknown>(null);
  const [busy, setBusy] = useState(false);

  if (status === 'loading') return <Screen><Loading label="Oturum denetleniyor…" /></Screen>;
  if (status === 'authenticated' && !busy) return <Navigate to={from} replace state={carry} />;

  const onSubmit = async (event: FormEvent) => {
    event.preventDefault();
    const next: typeof errors = {};
    if (displayName.trim().length < 2) next.displayName = 'Adını en az 2 karakter olarak yaz.';
    if (!/^\S+@\S+$/.test(email.trim()) || email.trim().length < 5) next.email = 'Geçerli bir e-posta adresi yaz.';
    if (password.length < 10 || !/[a-zçğıöşü]/.test(password) || !/\d/.test(password)) {
      next.password = 'Parola en az 10 karakter olmalı, en az bir küçük harf ve bir rakam içermeli.';
    }
    setErrors(next);
    setFailure(null);
    if (Object.keys(next).length > 0) return;
    setBusy(true);
    try {
      await register(email.trim(), password, displayName.trim());
      navigate(from, { replace: true, state: carry });
    } catch (error) {
      const fields = invalidFields(error);
      setErrors({
        displayName: fields.includes('displayName') ? 'Ad geçersiz (2–120 karakter).' : undefined,
        email: fields.includes('email') ? 'Geçerli bir e-posta adresi yaz.' : undefined,
        password: fields.includes('password') ? 'Parola en az 10 karakter olmalı, en az bir küçük harf ve bir rakam içermeli.' : undefined,
      });
      setFailure(error);
      setBusy(false);
    }
  };

  return (
    <Screen>
      <div className="auth">
        <PageHeading title="Kayıt ol" />
        <p className="lede">Bilet almak için bir hesap oluştur. Ödemeler simüledir; gerçek kart bilgisi istenmez.</p>
        <form className="form" style={{ marginTop: 20 }} onSubmit={(e) => void onSubmit(e)} noValidate>
          <ErrorAlert error={failure} />
          <Field label="Ad" error={errors.displayName}>
            {(control) => <input {...control} type="text" name="displayName" autoComplete="name" value={displayName} onChange={(e) => setDisplayName(e.target.value)} required />}
          </Field>
          <Field label="E-posta" error={errors.email}>
            {(control) => <input {...control} type="email" name="email" autoComplete="username" value={email} onChange={(e) => setEmail(e.target.value)} required />}
          </Field>
          <Field label="Parola" error={errors.password} hint="En az 10 karakter; en az bir küçük harf ve bir rakam.">
            {(control) => <input {...control} type="password" name="password" autoComplete="new-password" value={password} onChange={(e) => setPassword(e.target.value)} required />}
          </Field>
          <div className="row-actions">
            <button type="submit" className="btn btn-primary" disabled={busy}>{busy ? 'Hesap oluşturuluyor…' : 'Kayıt ol'}</button>
            <Link className="btn btn-link" to="/giris" state={state}>Hesabın var mı? Giriş yap</Link>
          </div>
        </form>
      </div>
    </Screen>
  );
}
