import { describe, expect, it, vi } from 'vitest';
import { ApiClient, ApiError, IdempotentAction, isOutcomeUnknown, NetworkError, runIdempotent } from './api';
import { describeError, invalidFields } from './errors';

interface Call {
  url: string;
  method: string;
  headers: Record<string, string>;
  body: string | undefined;
  credentials: RequestCredentials | undefined;
}

type Reply = Response | Error | ((call: Call) => Response | Error);

function json(status: number, body: unknown, type = 'application/json'): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'content-type': type } });
}

const problem = (status: number, code: string, extra: Record<string, unknown> = {}) =>
  json(status, { type: `https://loca.invalid/problems/${code}`, title: 'x', status, detail: 'English server detail that must not be shown.', code, ...extra }, 'application/problem+json');

/** Sıralı yanıt veren sahte fetch; çağrıları kaydeder. */
function fakeFetch(replies: Reply[]) {
  const calls: Call[] = [];
  const queue = [...replies];
  const fetchImpl = async (url: string, init: RequestInit): Promise<Response> => {
    const call: Call = {
      url, method: init.method ?? 'GET', headers: { ...(init.headers as Record<string, string>) },
      body: init.body as string | undefined, credentials: init.credentials,
    };
    calls.push(call);
    const next = queue.shift();
    if (next === undefined) throw new Error(`Beklenmeyen istek: ${call.method} ${url}`);
    const reply = typeof next === 'function' ? next(call) : next;
    if (reply instanceof Error) throw reply;
    return reply;
  };
  return { calls, fetchImpl };
}

const csrf = (token: string) => json(200, { token });
const noSleep = { sleep: () => Promise.resolve() };

describe('ApiClient — CSRF', () => {
  it('GET için CSRF token\'ı istemez; çerezleri aynı kökene gönderir', async () => {
    const { calls, fetchImpl } = fakeFetch([json(200, { ok: true })]);
    const client = new ApiClient(fetchImpl);
    await expect(client.get('/api/catalog/filters')).resolves.toEqual({ ok: true });
    expect(calls).toHaveLength(1);
    expect(calls[0]!.headers['X-CSRF-TOKEN']).toBeUndefined();
    expect(calls[0]!.credentials).toBe('same-origin');
  });

  it('ilk güvensiz istekten önce token alır ve X-CSRF-TOKEN başlığıyla gönderir', async () => {
    const { calls, fetchImpl } = fakeFetch([csrf('t1'), json(200, { id: 1 }), json(200, { id: 2 })]);
    const client = new ApiClient(fetchImpl);
    await client.post('/api/x', { a: 1 });
    await client.put('/api/y', { b: 2 });
    expect(calls.map((c) => `${c.method} ${c.url}`)).toEqual(['GET /api/auth/csrf', 'POST /api/x', 'PUT /api/y']);
    expect(calls[1]!.headers['X-CSRF-TOKEN']).toBe('t1');
    expect(calls[2]!.headers['X-CSRF-TOKEN']).toBe('t1');
    expect(calls[1]!.body).toBe('{"a":1}');
  });

  it('login yanıtındaki yeni token ek istek yapmadan kullanılır', async () => {
    const { calls, fetchImpl } = fakeFetch([new Response(null, { status: 204 })]);
    const client = new ApiClient(fetchImpl);
    client.setCsrfToken('from-login');
    await client.post('/api/auth/logout');
    expect(calls).toHaveLength(1);
    expect(calls[0]!.headers['X-CSRF-TOKEN']).toBe('from-login');
  });

  it('403 csrf_invalid: token bir kez yenilenir ve istek aynı Idempotency-Key ile tekrarlanır', async () => {
    const { calls, fetchImpl } = fakeFetch([problem(403, 'csrf_invalid'), csrf('fresh'), json(201, { id: 'h1' })]);
    const client = new ApiClient(fetchImpl);
    client.setCsrfToken('stale');
    await expect(client.post('/api/sessions/s/holds', { seatIds: ['a'] }, { idempotencyKey: 'key-0000000000000001' })).resolves.toEqual({ id: 'h1' });
    expect(calls.map((c) => `${c.method} ${c.url}`)).toEqual(['POST /api/sessions/s/holds', 'GET /api/auth/csrf', 'POST /api/sessions/s/holds']);
    expect(calls[0]!.headers['X-CSRF-TOKEN']).toBe('stale');
    expect(calls[2]!.headers['X-CSRF-TOKEN']).toBe('fresh');
    expect(calls[2]!.headers['Idempotency-Key']).toBe('key-0000000000000001');
    expect(calls[2]!.body).toBe(calls[0]!.body);
  });

  it('yenilemeden sonra da csrf_invalid gelirse sonsuz döngüye girmez, hatayı verir', async () => {
    const { calls, fetchImpl } = fakeFetch([problem(403, 'csrf_invalid'), csrf('fresh'), problem(403, 'csrf_invalid')]);
    const client = new ApiClient(fetchImpl);
    client.setCsrfToken('stale');
    await expect(client.post('/api/x')).rejects.toMatchObject({ status: 403, code: 'csrf_invalid' });
    expect(calls).toHaveLength(3);
  });

  it('eşzamanlı güvensiz istekler tek token isteğini paylaşır', async () => {
    const { calls, fetchImpl } = fakeFetch([csrf('t1'), json(200, {}), json(200, {})]);
    const client = new ApiClient(fetchImpl);
    await Promise.all([client.post('/api/a'), client.post('/api/b')]);
    expect(calls.filter((c) => c.url === '/api/auth/csrf')).toHaveLength(1);
  });
});

describe('ApiClient — problem+json hata eşlemesi', () => {
  it('durum, kod ve ek alanları ApiError\'a taşır', async () => {
    const { fetchImpl } = fakeFetch([problem(409, 'seats_unavailable', { seatIds: ['s1', 's2'] })]);
    const client = new ApiClient(fetchImpl);
    client.setCsrfToken('t');
    const error = await client.post('/api/sessions/s/holds', { seatIds: ['s1', 's2'] }).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ApiError);
    expect(error).toMatchObject({ status: 409, code: 'seats_unavailable' });
    expect((error as ApiError).problem.seatIds).toEqual(['s1', 's2']);
  });

  it('kullanıcı metni koddan üretilir; İngilizce detail gösterilmez', async () => {
    const { fetchImpl } = fakeFetch([problem(409, 'cancellation_window_closed')]);
    const error = await new ApiClient(fetchImpl).get('/api/x').catch((e: unknown) => e);
    const text = describeError(error);
    expect(text).toBe('İptal süresi doldu; bu sipariş artık iptal edilemez.');
    expect(text).not.toMatch(/English/);
  });

  it('429 rate_limited: bekleme süresi mesajda yer alır', async () => {
    const { fetchImpl } = fakeFetch([problem(429, 'rate_limited', { retryAfterSeconds: 37 })]);
    const error = await new ApiClient(fetchImpl).get('/api/x').catch((e: unknown) => e);
    expect(describeError(error)).toContain('37 saniye sonra');
  });

  it('503: sonuç bilinmiyor / yeniden dene', async () => {
    const { fetchImpl } = fakeFetch([problem(503, 'service_unavailable')]);
    const error = await new ApiClient(fetchImpl).get('/api/x').catch((e: unknown) => e);
    expect(isOutcomeUnknown(error)).toBe(true);
    expect(describeError(error)).toMatch(/sonucu bilinmiyor; yeniden dene/);
  });

  it('ağ hatası NetworkError olur ve sonuç bilinmiyor sayılır', async () => {
    const { fetchImpl } = fakeFetch([new TypeError('Failed to fetch')]);
    const error = await new ApiClient(fetchImpl).get('/api/x').catch((e: unknown) => e);
    expect(error).toBeInstanceOf(NetworkError);
    expect(isOutcomeUnknown(error)).toBe(true);
    expect(describeError(error)).toMatch(/Sunucuya ulaşılamadı/);
  });

  it('JSON olmayan hata yanıtı genel bir koda çevrilir', async () => {
    const { fetchImpl } = fakeFetch([new Response('<html>bad gateway</html>', { status: 502, headers: { 'content-type': 'text/html' } })]);
    const error = await new ApiClient(fetchImpl).get('/api/x').catch((e: unknown) => e);
    expect(error).toMatchObject({ status: 502, code: 'http_502' });
    expect(isOutcomeUnknown(error)).toBe(true);
  });

  it('401 unauthenticated: oturum bitti işleyicisi çağrılır; invalid_credentials çağırmaz', async () => {
    const { fetchImpl } = fakeFetch([problem(401, 'unauthenticated'), problem(401, 'invalid_credentials')]);
    const client = new ApiClient(fetchImpl);
    client.setCsrfToken('t');
    const handler = vi.fn();
    client.onUnauthenticated(handler);
    await expect(client.get('/api/orders')).rejects.toMatchObject({ code: 'unauthenticated' });
    expect(handler).toHaveBeenCalledTimes(1);
    await expect(client.post('/api/auth/login', { email: 'a', password: 'b' })).rejects.toMatchObject({ code: 'invalid_credentials' });
    expect(handler).toHaveBeenCalledTimes(1);
  });

  it('doğrulama hatasında geçersiz alan adlarını verir', async () => {
    const { fetchImpl } = fakeFetch([problem(400, 'validation_failed', { errors: { title: ['x'], description: ['y'] } })]);
    const error = await new ApiClient(fetchImpl).get('/api/x').catch((e: unknown) => e);
    expect(invalidFields(error)).toEqual(['title', 'description']);
  });

  it('204 yanıtı gövdesiz döner', async () => {
    const { fetchImpl } = fakeFetch([new Response(null, { status: 204 })]);
    await expect(new ApiClient(fetchImpl).get('/api/sessions/s/my-hold')).resolves.toBeUndefined();
  });
});

describe('Idempotency-Key — aynı anahtarla yeniden deneme', () => {
  const setup = (replies: Reply[]) => {
    const { calls, fetchImpl } = fakeFetch(replies);
    const client = new ApiClient(fetchImpl);
    client.setCsrfToken('t');
    const send = (key: string) => client.post<{ id: string }>('/api/holds/h/checkout', { paymentMethod: 'tok_success' }, { idempotencyKey: key });
    const keys = () => calls.map((c) => c.headers['Idempotency-Key']);
    return { calls, send, keys };
  };

  it('ağ hatası ve 503 sonrasında AYNI anahtarla yeniden dener ve sonucu verir', async () => {
    const { send, keys } = setup([new TypeError('Failed to fetch'), problem(503, 'service_unavailable'), json(202, { id: 'o1' })]);
    const action = new IdempotentAction();
    await expect(runIdempotent(action, 'body', send, noSleep)).resolves.toEqual({ id: 'o1' });
    expect(keys()).toHaveLength(3);
    expect(new Set(keys()).size).toBe(1);
    expect(keys()[0]).toMatch(/^[A-Za-z0-9_.:-]{16,128}$/);
    expect(action.pending).toBe(false);
  });

  it('kesin hata (409) yeniden denenmez ve anahtar bırakılır', async () => {
    const { send, keys } = setup([problem(409, 'hold_expired')]);
    const action = new IdempotentAction();
    await expect(runIdempotent(action, 'body', send, noSleep)).rejects.toMatchObject({ code: 'hold_expired' });
    expect(keys()).toHaveLength(1);
    expect(action.pending).toBe(false);
  });

  it('denemeler tükenirse anahtar saklanır: kullanıcının "yeniden dene"si aynı anahtarla gider', async () => {
    const { send, keys } = setup([new TypeError('x'), new TypeError('x'), new TypeError('x'), json(202, { id: 'o1' })]);
    const action = new IdempotentAction();
    await expect(runIdempotent(action, 'body', send, noSleep)).rejects.toBeInstanceOf(NetworkError);
    expect(action.pending).toBe(true);
    await expect(runIdempotent(action, 'body', send, noSleep)).resolves.toEqual({ id: 'o1' });
    expect(keys()).toHaveLength(4);
    expect(new Set(keys()).size).toBe(1);
  });

  it('yeni eylem (ya da değişen istek içeriği) yeni anahtar alır', async () => {
    const { send, keys } = setup([json(202, { id: 'o1' }), json(202, { id: 'o2' }), new TypeError('x'), json(202, { id: 'o3' })]);
    const action = new IdempotentAction();
    await runIdempotent(action, 'A', send, noSleep);
    await runIdempotent(action, 'A', send, noSleep);
    expect(keys()[0]).not.toBe(keys()[1]); // Tamamlanan eylemden sonra yeni anahtar.

    await expect(runIdempotent(action, 'B', send, { ...noSleep, retries: 0 })).rejects.toBeInstanceOf(NetworkError);
    await runIdempotent(action, 'C', send, noSleep); // İçerik değişti: eski anahtar yeniden kullanılmaz.
    expect(keys()[2]).not.toBe(keys()[3]);
  });

  it('409 request_in_progress aynı anahtarla yeniden denenir', async () => {
    const { send, keys } = setup([problem(409, 'request_in_progress'), json(202, { id: 'o1' })]);
    await expect(runIdempotent(new IdempotentAction(), 'body', send, noSleep)).resolves.toEqual({ id: 'o1' });
    expect(new Set(keys()).size).toBe(1);
  });
});
