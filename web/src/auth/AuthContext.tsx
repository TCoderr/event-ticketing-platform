import { useQueryClient } from '@tanstack/react-query';
import { createContext, useCallback, useContext, useEffect, useMemo, useRef, useState, type ReactNode } from 'react';
import { api, ApiError } from '../lib/api';
import type { LoginResponse, Me } from '../lib/types';

export type AuthStatus = 'loading' | 'anonymous' | 'authenticated';

export interface AuthValue {
  status: AuthStatus;
  user: Me | null;
  /** Açılışta oturum sorgusu ağ hatasıyla sonuçlandıysa `true`; "anonim" varsayılmaz. */
  bootstrapFailed: boolean;
  login(email: string, password: string): Promise<Me>;
  register(email: string, password: string, displayName: string): Promise<Me>;
  logout(): Promise<void>;
  /** Üyelikler değiştiğinde (ör. organizasyon başvurusu) kullanıcıyı sunucudan yeniden okur. */
  reload(): Promise<void>;
}

const AuthContext = createContext<AuthValue | null>(null);

/** Kullanıcıya özel sorgu anahtarlarının kökü. Logout ve kullanıcı değişiminde bu kökün altı tamamen silinir. */
export const PRIVATE_ROOT = 'u';

export function AuthProvider({ children }: { children: ReactNode }) {
  const queryClient = useQueryClient();
  const [status, setStatus] = useState<AuthStatus>('loading');
  const [user, setUser] = useState<Me | null>(null);
  const [bootstrapFailed, setBootstrapFailed] = useState(false);
  const userRef = useRef<Me | null>(null);

  const clearPrivateCache = useCallback(() => {
    void queryClient.cancelQueries({ queryKey: [PRIVATE_ROOT] });
    queryClient.removeQueries({ queryKey: [PRIVATE_ROOT] });
  }, [queryClient]);

  const apply = useCallback((next: Me | null) => {
    if (userRef.current?.id !== next?.id) clearPrivateCache();
    userRef.current = next;
    setUser(next);
    setStatus(next ? 'authenticated' : 'anonymous');
  }, [clearPrivateCache]);

  const loadMe = useCallback(async () => {
    const result = await fetchMe();
    apply(result.user);
    setBootstrapFailed(result.failed);
  }, [apply]);

  useEffect(() => {
    // Açılış: CSRF token'ı ve oturum durumu. Token yalnız bellekte tutulur.
    let active = true;
    api.refreshCsrf().catch(() => undefined);
    void fetchMe().then((result) => {
      if (!active) return;
      apply(result.user);
      setBootstrapFailed(result.failed);
    });
    return () => {
      active = false;
    };
  }, [apply]);

  useEffect(() => {
    // Oturum sunucuda bittiyse (401): özel önbellek silinir, kullanıcı anonim olur; korumalı rotalar girişe yönlenir.
    api.onUnauthenticated(() => {
      if (userRef.current === null) return;
      apply(null);
      api.setCsrfToken(null);
      api.refreshCsrf().catch(() => undefined);
    });
    return () => api.onUnauthenticated(null);
  }, [apply]);

  const login = useCallback(async (email: string, password: string) => {
    const response = await api.post<LoginResponse>('/api/auth/login', { email, password });
    api.setCsrfToken(response.csrfToken); // Oturumla birlikte token da değişir.
    clearPrivateCache();
    apply(response.user);
    setBootstrapFailed(false);
    return response.user;
  }, [apply, clearPrivateCache]);

  const register = useCallback(async (email: string, password: string, displayName: string) => {
    await api.post('/api/auth/register', { email, password, displayName });
    return login(email, password);
  }, [login]);

  const logout = useCallback(async () => {
    try {
      await api.post('/api/auth/logout');
    } catch (error) {
      // Oturum zaten bitmişse sonuç aynıdır; başka hatada kullanıcı oturumda kalır ve hatayı görür.
      if (!(error instanceof ApiError && error.status === 401)) throw error;
    }
    apply(null);
    clearPrivateCache();
    api.setCsrfToken(null);
    await api.refreshCsrf().catch(() => undefined);
  }, [apply, clearPrivateCache]);

  const value = useMemo<AuthValue>(
    () => ({ status, user, bootstrapFailed, login, register, logout, reload: loadMe }),
    [status, user, bootstrapFailed, login, register, logout, loadMe],
  );
  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>;
}

/** Oturum sorgusu. 401 "anonim" demektir; ağ hatasında kimlik bilinmez (`failed`), korumalı sayfalar "yeniden dene" sunar. */
async function fetchMe(): Promise<{ user: Me | null; failed: boolean }> {
  try {
    return { user: await api.get<Me>('/api/auth/me'), failed: false };
  } catch (error) {
    return { user: null, failed: !(error instanceof ApiError && error.status === 401) };
  }
}

export function useAuth(): AuthValue {
  const value = useContext(AuthContext);
  if (!value) throw new Error('useAuth, AuthProvider içinde kullanılmalı.');
  return value;
}

/** Kullanıcıya kapsamlı sorgu anahtarı: `['u', userId, …]`. */
export function useUserKey(): (...parts: unknown[]) => unknown[] {
  const { user } = useAuth();
  const id = user?.id ?? 'anonymous';
  return useCallback((...parts: unknown[]) => [PRIVATE_ROOT, id, ...parts], [id]);
}

/** Herkese açık sorgu anahtarı: `['public', …]`. */
export function publicKey(...parts: unknown[]): unknown[] {
  return ['public', ...parts];
}

export function isOrganizerAdmin(user: Me | null): boolean {
  return Boolean(user?.memberships.some((m) => m.role === 'OrganizerAdmin'));
}
