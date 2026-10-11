// E2E test verisi kurulumu: her test kendi verisini gerçek API üzerinden (organizatör girişi + CSRF) oluşturur.
// Geliştirme verisine güvenilmez. Parolalar yalnız E2E_STATE_DIR'den okunur; hiçbir yere yazılmaz/loglanmaz.
import { randomUUID } from 'node:crypto';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import { expect, request as playwrightRequest, type APIRequestContext, type APIResponse } from '@playwright/test';

export const USERS = {
  admin: 'platform-admin@loca.demo',
  kumru: 'organizer-kumru@loca.demo',
  marti: 'organizer-marti@loca.demo',
  gate: 'gate-kumru@loca.demo',
  ayse: 'customer-ayse@loca.demo',
  mehmet: 'customer-mehmet@loca.demo',
} as const;

export function baseUrl(): string {
  const value = process.env.E2E_BASE_URL;
  if (!value) throw new Error('E2E_BASE_URL tanımlı değil.');
  return value;
}

export function passwordOf(email: string): string {
  const stateDir = process.env.E2E_STATE_DIR;
  if (!stateDir) throw new Error('E2E_STATE_DIR tanımlı değil.');
  return readFileSync(path.join(stateDir, 'users', `${email}.password.txt`), 'utf8').trim();
}

export const uniqueSuffix = () => `${Date.now().toString(36)}${Math.random().toString(36).slice(2, 6)}`;

/** Seansın saat diliminde `yyyy-MM-ddTHH:mm`. */
export function localTime(date: Date, timeZone = 'Europe/Istanbul'): string {
  const parts = Object.fromEntries(new Intl.DateTimeFormat('en-CA', {
    timeZone, year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23',
  }).formatToParts(date).map((p) => [p.type, p.value]));
  return `${parts.year}-${parts.month}-${parts.day}T${parts.hour}:${parts.minute}`;
}

export const hoursFromNow = (hours: number) => localTime(new Date(Date.now() + hours * 3600 * 1000));

interface CallOptions {
  /** Beklenen durum kodu (varsayılan: herhangi bir 2xx). */
  expectStatus?: number;
  idempotencyKey?: string;
}

/** Çerez oturumlu, CSRF token'ı bilen API istemcisi (bir kullanıcı = bir bağlam). */
export class ApiSession {
  private constructor(readonly context: APIRequestContext, private csrf: string, readonly email: string) {}

  static async login(email: string): Promise<ApiSession> {
    const context = await playwrightRequest.newContext({ baseURL: baseUrl(), ignoreHTTPSErrors: true });
    const csrf = await context.get('/api/auth/csrf');
    expect(csrf.status(), 'CSRF token alınabilmeli').toBe(200);
    const anonymousToken = ((await csrf.json()) as { token: string }).token;
    const login = await context.post('/api/auth/login', {
      data: { email, password: passwordOf(email) },
      headers: { 'X-CSRF-TOKEN': anonymousToken },
    });
    expect(login.status(), `${email} giriş yapabilmeli`).toBe(200);
    const body = (await login.json()) as { csrfToken: string };
    return new ApiSession(context, body.csrfToken, email);
  }

  async dispose(): Promise<void> {
    await this.context.dispose();
  }

  async get<T>(url: string, options: CallOptions = {}): Promise<T> {
    return this.read<T>(await this.context.get(url), `GET ${url}`, options);
  }

  async post<T>(url: string, data?: unknown, options: CallOptions = {}): Promise<T> {
    return this.read<T>(await this.context.post(url, { data, headers: this.headers(options) }), `POST ${url}`, options);
  }

  async put<T>(url: string, data?: unknown, options: CallOptions = {}): Promise<T> {
    return this.read<T>(await this.context.put(url, { data, headers: this.headers(options) }), `PUT ${url}`, options);
  }

  async delete<T>(url: string, options: CallOptions = {}): Promise<T> {
    return this.read<T>(await this.context.delete(url, { headers: this.headers(options) }), `DELETE ${url}`, options);
  }

  /** Durum kodunu denetlemeden ham yanıt (beklenen hata senaryoları için). */
  async rawPost(url: string, data?: unknown, options: CallOptions = {}): Promise<APIResponse> {
    return this.context.post(url, { data, headers: this.headers(options) });
  }

  private headers(options: CallOptions): Record<string, string> {
    return { 'X-CSRF-TOKEN': this.csrf, ...(options.idempotencyKey ? { 'Idempotency-Key': options.idempotencyKey } : {}) };
  }

  private async read<T>(response: APIResponse, what: string, options: CallOptions): Promise<T> {
    if (options.expectStatus !== undefined) expect(response.status(), what).toBe(options.expectStatus);
    else expect(response.ok(), `${what} → ${response.status()}`).toBe(true);
    if (response.status() === 204) return undefined as T;
    const type = response.headers()['content-type'] ?? '';
    return (type.includes('json') ? await response.json() : await response.text()) as T;
  }
}

// --- Veri kurucuları ---------------------------------------------------------------------------

export interface CreatedSession {
  organizationId: string;
  venueId: string;
  eventId: string;
  eventTitle: string;
  eventSlug: string;
  venueName: string;
  sessionId: string;
}

interface SessionOptions {
  /** Benzersiz başlığın okunur ön eki. */
  title?: string;
  startsInHours?: number;
  holdTtlSeconds?: number;
  demoSimulator?: boolean;
  publish?: boolean;
  city?: string;
  /** Satış penceresi (şimdiye göre saat); verilmezse satış hemen açılır ve seans başlangıcında kapanır. */
  salesStartsInHours?: number;
  salesEndsInHours?: number;
}

const salesWindow = (options: SessionOptions) => ({
  ...(options.salesStartsInHours !== undefined ? { salesStartAtLocal: hoursFromNow(options.salesStartsInHours) } : {}),
  ...(options.salesEndsInHours !== undefined ? { salesEndAtLocal: hoursFromNow(options.salesEndsInHours) } : {}),
});

interface SeatedOptions extends SessionOptions {
  rows?: number;
  seatsPerRow?: number;
  priceMinor?: number;
}

interface GaOptions extends SessionOptions {
  categories?: { name: string; priceMinor: number; capacity: number }[];
}

async function createEventAndVenue(organizer: ApiSession, options: SessionOptions) {
  const organizations = await organizer.get<{ id: string }[]>('/api/organizer/organizations');
  const organizationId = organizations[0]!.id;
  const suffix = uniqueSuffix();
  const venueName = `E2E Salonu ${suffix}`;
  const eventTitle = `${options.title ?? 'E2E Etkinliği'} ${suffix}`;
  const venue = await organizer.post<{ id: string }>(`/api/organizer/organizations/${organizationId}/venues`,
    { name: venueName, city: options.city ?? 'İstanbul', address: 'Test Caddesi No: 1', timezone: 'Europe/Istanbul' }, { expectStatus: 201 });
  const event = await organizer.post<{ id: string; slug: string }>(`/api/organizer/organizations/${organizationId}/events`,
    { title: eventTitle, description: 'Uçtan uca test için oluşturulan sentetik etkinlik.', category: 'Theatre' }, { expectStatus: 201 });
  await organizer.post(`/api/organizer/events/${event.id}/publish`);
  return { organizationId, venueId: venue.id, venueName, eventId: event.id, eventSlug: event.slug, eventTitle };
}

async function finishSession(organizer: ApiSession, sessionId: string, options: SessionOptions) {
  if (options.publish !== false) await organizer.post(`/api/organizer/sessions/${sessionId}/publish`);
  if (options.demoSimulator) await organizer.put(`/api/organizer/sessions/${sessionId}/demo-simulator`, { enabled: true });
}

/** Mekan → plan → etkinlik → koltuklu seans → (yayınla). Varsayılan: 2 sıra × 5 koltuk, 450,00 ₺, 3 gün sonra. */
export async function createSeatedSession(organizer: ApiSession, options: SeatedOptions = {}): Promise<CreatedSession> {
  const base = await createEventAndVenue(organizer, options);
  const layout = await organizer.post<{ id: string }>(`/api/organizer/venues/${base.venueId}/layouts`,
    { name: 'E2E Planı', sections: [{ name: 'Salon', tierCode: 'A', rows: options.rows ?? 2, seatsPerRow: options.seatsPerRow ?? 5 }] }, { expectStatus: 201 });
  const session = await organizer.post<{ id: string }>(`/api/organizer/events/${base.eventId}/sessions`, {
    venueId: base.venueId, startsAtLocal: hoursFromNow(options.startsInHours ?? 72), salesMode: 'ReservedSeating', ...salesWindow(options),
    ...(options.holdTtlSeconds ? { holdTtlSeconds: options.holdTtlSeconds } : {}),
  }, { expectStatus: 201 });
  await organizer.put(`/api/organizer/sessions/${session.id}/seating`, { layoutId: layout.id, tierPrices: { A: options.priceMinor ?? 45000 } });
  await finishSession(organizer, session.id, options);
  return { ...base, sessionId: session.id };
}

/** Mekan → etkinlik → genel giriş seansı + kategoriler → (yayınla). */
export async function createGaSession(organizer: ApiSession, options: GaOptions = {}): Promise<CreatedSession> {
  const base = await createEventAndVenue(organizer, options);
  const session = await organizer.post<{ id: string }>(`/api/organizer/events/${base.eventId}/sessions`, {
    venueId: base.venueId, startsAtLocal: hoursFromNow(options.startsInHours ?? 72), salesMode: 'GeneralAdmission', ...salesWindow(options),
    ...(options.holdTtlSeconds ? { holdTtlSeconds: options.holdTtlSeconds } : {}),
  }, { expectStatus: 201 });
  for (const category of options.categories ?? [{ name: 'Ayakta', priceMinor: 32000, capacity: 50 }, { name: 'Tribün', priceMinor: 48000, capacity: 20 }]) {
    await organizer.post(`/api/organizer/sessions/${session.id}/categories`, category);
  }
  await finishSession(organizer, session.id, options);
  return { ...base, sessionId: session.id };
}

/** Kapı görevlisini organizasyona (GateStaff) ekler ve seansa atar. */
export async function assignGateStaff(organizer: ApiSession, created: CreatedSession, email: string = USERS.gate): Promise<void> {
  const member = await organizer.post<{ userId: string }>(`/api/organizer/organizations/${created.organizationId}/members`, { email, role: 'GateStaff' });
  await organizer.put(`/api/organizer/sessions/${created.sessionId}/gate-staff/${member.userId}`, undefined, { expectStatus: 204 });
}

export interface SeatInfo {
  id: string;
  section: string;
  row: string;
  number: number;
  available: boolean;
}

export async function seatsOf(session: ApiSession, sessionId: string): Promise<SeatInfo[]> {
  return (await session.get<{ seats: SeatInfo[] }>(`/api/catalog/sessions/${sessionId}/seats`)).seats;
}

export interface OrderInfo {
  id: string;
  status: string;
  orderNumber: string;
  payment: { status: string; failureKind: string | null; lateSuccess: boolean };
  refund: { status: string } | null;
}

export async function pollOrder(customer: ApiSession, orderId: string, done: (order: OrderInfo) => boolean, timeoutMs = 60_000): Promise<OrderInfo> {
  const started = Date.now();
  let last: OrderInfo | undefined;
  while (Date.now() - started < timeoutMs) {
    last = await customer.get<OrderInfo>(`/api/orders/${orderId}`);
    if (done(last)) return last;
    await new Promise((resolve) => setTimeout(resolve, 500));
  }
  throw new Error(`Sipariş beklenen duruma ulaşmadı; son durum: ${last?.status} / ödeme ${last?.payment.status}`);
}

export interface Purchase {
  orderId: string;
  orderNumber: string;
  ticketIds: string[];
}

/** API üzerinden satın alma (test ön koşulu olarak): hold → checkout → Paid → biletler. */
export async function purchase(
  customer: ApiSession, sessionId: string, body: { seatIds: string[] } | { items: { categoryId: string; quantity: number }[] }, paymentMethod = 'tok_success',
): Promise<Purchase> {
  const hold = await customer.post<{ id: string; ticketCount: number }>(`/api/sessions/${sessionId}/holds`, body, { expectStatus: 201, idempotencyKey: randomUUID() });
  const order = await customer.post<OrderInfo>(`/api/holds/${hold.id}/checkout`, { paymentMethod }, { expectStatus: 202, idempotencyKey: randomUUID() });
  await pollOrder(customer, order.id, (o) => o.status === 'Paid');
  let tickets: { id: string; orderId: string }[] = [];
  for (let i = 0; i < 40 && tickets.length < hold.ticketCount; i++) {
    tickets = (await customer.get<{ id: string; orderId: string }[]>('/api/tickets')).filter((t) => t.orderId === order.id);
    if (tickets.length < hold.ticketCount) await new Promise((resolve) => setTimeout(resolve, 500));
  }
  expect(tickets.length, 'Ödenmiş sipariş için biletler üretilmeli').toBe(hold.ticketCount);
  return { orderId: order.id, orderNumber: order.orderNumber, ticketIds: tickets.map((t) => t.id) };
}

export const idempotencyKey = () => randomUUID();
