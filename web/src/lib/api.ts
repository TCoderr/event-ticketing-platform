// API istemcisi: çerez oturumu, bellekte CSRF token'ı, problem+json hataları, Idempotency-Key.
// Token veya secret hiçbir tarayıcı deposuna yazılmaz; CSRF token'ı yalnız bu modülün belleğindedir.

export interface Problem {
  status: number;
  code: string;
  detail?: string;
  [extension: string]: unknown;
}

/** Sunucunun bilinçli olarak döndürdüğü hata (RFC 9457). Kullanıcı metni `code`'dan üretilir, `detail`'den değil. */
export class ApiError extends Error {
  readonly status: number;
  readonly code: string;
  readonly problem: Problem;

  constructor(problem: Problem) {
    super(`${problem.status} ${problem.code}`);
    this.name = 'ApiError';
    this.status = problem.status;
    this.code = problem.code;
    this.problem = problem;
  }
}

/** İstek sunucuya ulaşmadı ya da yanıt alınamadı: sonuç BİLİNMİYOR (başarı da başarısızlık da varsayılmaz). */
export class NetworkError extends Error {
  constructor(cause?: unknown) {
    super('network_error');
    this.name = 'NetworkError';
    this.cause = cause;
  }
}

/** Sonucu bilinmeyen hata: ağ hatası veya 502/503/504. */
export function isOutcomeUnknown(error: unknown): boolean {
  if (error instanceof NetworkError) return true;
  return error instanceof ApiError && (error.status === 503 || error.status === 502 || error.status === 504);
}

/** Aynı Idempotency-Key ile yeniden denenmesi gereken hata. */
export function isRetryableWithSameKey(error: unknown): boolean {
  return isOutcomeUnknown(error) || (error instanceof ApiError && error.code === 'request_in_progress');
}

export function isApiError(error: unknown, code?: string): error is ApiError {
  return error instanceof ApiError && (code === undefined || error.code === code);
}

export interface RequestOptions {
  body?: unknown;
  idempotencyKey?: string;
  signal?: AbortSignal;
}

type FetchLike = (input: string, init: RequestInit) => Promise<Response>;

const UNSAFE = new Set(['POST', 'PUT', 'DELETE', 'PATCH']);

export class ApiClient {
  private csrfToken: string | null = null;
  private csrfRequest: Promise<string> | null = null;
  private unauthenticatedHandler: (() => void) | null = null;
  private readonly fetchImpl: FetchLike;

  constructor(fetchImpl?: FetchLike) {
    this.fetchImpl = fetchImpl ?? ((input, init) => fetch(input, init));
  }

  /** Login yanıtındaki yeni token burada verilir; `null` bir sonraki güvensiz istekte yeniden alınmasını sağlar. */
  setCsrfToken(token: string | null): void {
    this.csrfToken = token;
  }

  /** `401 unauthenticated` yanıtında çağrılır (oturum bitti / iptal edildi). */
  onUnauthenticated(handler: (() => void) | null): void {
    this.unauthenticatedHandler = handler;
  }

  /** `GET /api/auth/csrf`: token üreten tek uç. Eşzamanlı çağrılar tek isteği paylaşır. */
  refreshCsrf(): Promise<string> {
    this.csrfRequest ??= this.send<{ token: string }>('GET', '/api/auth/csrf', {})
      .then((response) => {
        this.csrfToken = response.token;
        return response.token;
      })
      .finally(() => {
        this.csrfRequest = null;
      });
    return this.csrfRequest;
  }

  get<T>(path: string, options: RequestOptions = {}): Promise<T> {
    return this.request<T>('GET', path, options);
  }

  post<T>(path: string, body?: unknown, options: RequestOptions = {}): Promise<T> {
    return this.request<T>('POST', path, { ...options, body });
  }

  put<T>(path: string, body?: unknown, options: RequestOptions = {}): Promise<T> {
    return this.request<T>('PUT', path, { ...options, body });
  }

  delete<T>(path: string, options: RequestOptions = {}): Promise<T> {
    return this.request<T>('DELETE', path, options);
  }

  async request<T>(method: string, path: string, options: RequestOptions = {}): Promise<T> {
    try {
      return await this.send<T>(method, path, options);
    } catch (error) {
      // CSRF token'ı eskimiş olabilir (oturum değişimi, başka sekmede giriş): bir kez yenile ve aynı isteği tekrarla.
      if (isApiError(error, 'csrf_invalid') && UNSAFE.has(method)) {
        this.csrfToken = null;
        await this.refreshCsrf();
        return await this.send<T>(method, path, options);
      }
      throw error;
    }
  }

  private async send<T>(method: string, path: string, options: RequestOptions): Promise<T> {
    const headers: Record<string, string> = { Accept: 'application/json' };
    if (UNSAFE.has(method)) {
      headers['X-CSRF-TOKEN'] = this.csrfToken ?? (await this.refreshCsrf());
    }
    if (options.idempotencyKey) headers['Idempotency-Key'] = options.idempotencyKey;
    let body: string | undefined;
    if (options.body !== undefined) {
      headers['Content-Type'] = 'application/json';
      body = JSON.stringify(options.body);
    }

    let response: Response;
    try {
      response = await this.fetchImpl(path, { method, headers, body, credentials: 'same-origin', signal: options.signal, cache: 'no-store' });
    } catch (error) {
      if (options.signal?.aborted) throw error;
      throw new NetworkError(error);
    }

    if (response.ok) {
      if (response.status === 204) return undefined as T;
      const type = response.headers.get('content-type') ?? '';
      return (type.includes('json') ? await response.json() : await response.text()) as T;
    }

    const error = new ApiError(await readProblem(response));
    if (error.status === 401 && error.code === 'unauthenticated') this.unauthenticatedHandler?.();
    throw error;
  }
}

async function readProblem(response: Response): Promise<Problem> {
  const fallback: Problem = { status: response.status, code: `http_${response.status}` };
  try {
    const type = response.headers.get('content-type') ?? '';
    if (!type.includes('json')) return fallback;
    const parsed = (await response.json()) as Partial<Problem> | null;
    if (parsed && typeof parsed.code === 'string') return { ...parsed, status: response.status, code: parsed.code };
    return fallback;
  } catch {
    return fallback;
  }
}

export const api = new ApiClient();

// --- Idempotency -----------------------------------------------------------------------------

export function newIdempotencyKey(): string {
  return crypto.randomUUID();
}

/**
 * Bir kullanıcı eyleminin Idempotency-Key'i. Anahtar eylem başına BİR KEZ üretilir; sonuç bilinmiyorsa
 * (ağ hatası / 503) saklanır ve yeniden denemede AYNI anahtar kullanılır. Kesin bir yanıt (2xx veya
 * yeniden denenemez hata) geldiğinde ya da istek içeriği değiştiğinde anahtar bırakılır.
 */
export class IdempotentAction {
  private key: string | null = null;
  private fingerprint: string | null = null;

  keyFor(fingerprint = ''): string {
    if (this.key === null || this.fingerprint !== fingerprint) {
      this.key = newIdempotencyKey();
      this.fingerprint = fingerprint;
    }
    return this.key;
  }

  settle(): void {
    this.key = null;
    this.fingerprint = null;
  }

  get pending(): boolean {
    return this.key !== null;
  }
}

export interface IdempotentRunOptions {
  /** Otomatik yeniden deneme sayısı (ilk denemeye ek). */
  retries?: number;
  delayMs?: (attempt: number) => number;
  sleep?: (ms: number) => Promise<void>;
}

const defaultSleep = (ms: number) => new Promise<void>((resolve) => setTimeout(resolve, ms));

/**
 * İsteği gönderir; sonuç bilinmiyorsa aynı anahtarla yeniden dener. Denemeler tükenirse hata fırlatılır
 * ama anahtar SAKLANIR: kullanıcının "yeniden dene"si de aynı anahtarla gider.
 */
export async function runIdempotent<T>(
  action: IdempotentAction,
  fingerprint: string,
  send: (idempotencyKey: string) => Promise<T>,
  options: IdempotentRunOptions = {},
): Promise<T> {
  const retries = options.retries ?? 2;
  const delayMs = options.delayMs ?? ((attempt) => 600 * 2 ** attempt);
  const sleep = options.sleep ?? defaultSleep;
  const key = action.keyFor(fingerprint);
  for (let attempt = 0; ; attempt++) {
    try {
      const result = await send(key);
      action.settle();
      return result;
    } catch (error) {
      if (!isRetryableWithSameKey(error)) {
        action.settle();
        throw error;
      }
      if (attempt >= retries) throw error;
      await sleep(delayMs(attempt));
    }
  }
}
