# 05 — API Sözleşmeleri

Otorite: çalışan uygulamanın `/openapi/v1.json` çıktısı. Bu belge sözleşmenin insan tarafından okunan
özetidir. Tam operasyon listesi `05a-api-operations.md` içindedir; o liste OpenAPI çıktısıyla bir
entegrasyon testiyle (`ApiDocumentationTests`) karşılaştırılır — sapma testi kırar.

## 1. Genel kurallar

- Taban yol `/api`. JSON, `camelCase`; enum'lar metin. Bilinmeyen alan içeren gövde `400 unknown_field` ile reddedilir.
- Hatalar RFC 9457 `application/problem+json`; her hatada makinece okunur `code` ve `correlationId` uzantısı vardır.
  Kimlik doğrulama hataları da JSON'dur; HTML login yönlendirmesi yoktur.
- Para: `…Minor` son ekli tam sayı (kuruş) + `currency: "TRY"`. Başka para birimi `400 unsupported_currency`.
  Kesirli tutar bağlanamaz (`400`).
- Zaman: ISO-8601 UTC anı (`startsAt`) + IANA `timezone` + gösterim için `startsAtLocal` (`yyyy-MM-ddTHH:mm`).
  Karar gerektiren yanıtlar `serverTime` içerir. Organizatör, seans saatini mekanın **yerel saatiyle** gönderir.
- Güvensiz metotlar (`POST/PUT/DELETE`) `X-CSRF-TOKEN` başlığı ister (webhook hariç; o HMAC ile korunur).
  Yetkilendirme CSRF'ten önce değerlendirilir: oturumsuz istek `401`, oturumlu ama token'sız istek `403 csrf_invalid` alır.
- Başka organizasyona/kullanıcıya ait kaynak `404 not_found` döner (varlık sızdırılmaz); üyeliği olup rolü
  yetmeyen çağıran `403 forbidden` alır.

### Idempotency-Key

Şu uçlarda **zorunlu**: hold oluşturma, checkout, sipariş iptali, seans iptali, check-in.

| Özellik | Değer |
|---|---|
| Biçim | 16–128 karakter, `[A-Za-z0-9_.:-]` |
| Kapsam | `(kullanıcı, operasyon, anahtar)` |
| İstek özeti | `SHA-256("v1\n" + METHOD + "\n" + yol + "\n" + kanonik JSON gövde)` |
| Kanonik JSON | Anahtarlar ordinal sıralı, boşluksuz, dizi sırası korunur, metinler NFC, tam sayı değerli sayılar tam sayı |
| TTL | 24 saat; süresi dolan kayıt yok sayılır ve istek yeniden yürütülür |
| Eksik / geçersiz anahtar | `400 idempotency_key_required` / `400 idempotency_key_invalid` |
| Aynı anahtar + farklı istek | `409 idempotency_key_reused` |
| Aynı anahtar, ilk istek hâlâ sürüyor | İkinci istek bekler; `lock_timeout` aşılırsa `409 request_in_progress` (`retryAfterSeconds`) |
| Başarılı yanıtın tekrarı | Aynı durum kodu, aynı gövde baytları, `Content-Type`, `Location` + `Idempotent-Replayed: true` |
| Başarısız (2xx olmayan) sonuç | Saklanmaz; aynı anahtarla yeniden denenebilir |

Tekrar oynatmadan **önce** güncel kimlik doğrulama, sahiplik ve yetki kontrolü yapılır.

## 2. Kimlik

| Metot | Yol | Açıklama |
|---|---|---|
| GET | `/api/auth/csrf` | CSRF çerezi + `{ token }`; `Cache-Control: no-store`. Token üreten **tek** GET ucu |
| POST | `/api/auth/register` | `{ email, password, displayName }` → `201`. Parola ≥ 10 karakter, harf + rakam |
| POST | `/api/auth/login` | `{ email, password }` → oturum çerezi + `{ csrfToken, user }`. Hata her durumda `401 invalid_credentials` |
| POST | `/api/auth/logout` · `/logout-all` | Oturum(lar)ı sunucuda iptal eder → `204` |
| GET | `/api/auth/me` | `{ id, email, displayName, isPlatformAdmin, isDemo, memberships[] }`; `no-store` |

## 3. Public katalog

| Metot | Yol | Not |
|---|---|---|
| GET | `/api/catalog/sessions?city&category&from&to&cursor&limit` | Keyset `(startsAt, id)`; `limit` 1–50; `public, max-age=30` |
| GET | `/api/catalog/filters` | Mevcut şehir ve kategoriler |
| GET | `/api/catalog/events/{slug}` | Etkinlik + public seansları |
| GET | `/api/catalog/sessions/{sessionId}` | Ayrıntı, satış penceresi, fiyatlar, GA müsaitliği, `demoSimulatorEnabled`, `serverTime`; `no-store` |
| GET | `/api/catalog/sessions/{sessionId}/seats` | Koltuk planı + müsaitlik **ipucu** (`available: bool`); `no-store` |

Public yanıtlar bilet token'ı, müşteri verisi veya taslak içerik içermez. Görünürlük: yayınlanmış seans +
yayınlanmış ve yayından kaldırılmamış etkinlik + aktif organizasyon + başlamamış.

## 4. Müşteri

| Metot | Yol | Idem. | Başarı | Önemli hatalar |
|---|---|---|---|---|
| POST | `/api/sessions/{sessionId}/holds` | ✔ | `201` hold | `409 seats_unavailable` (`seatIds`), `409 insufficient_capacity` (`categories`), `409 active_hold_exists` (`holdId`), `409 sales_not_started`, `409 sales_closed`, `409 session_cancelled`, `422 invalid_selection` |
| GET | `/api/sessions/{sessionId}/my-hold` | | `200` veya `204` | |
| GET | `/api/holds/{holdId}` | | `200`; `no-store` | `404` |
| DELETE | `/api/holds/{holdId}` | | `204` | `409 hold_not_active`, `409 order_already_exists` |
| POST | `/api/holds/{holdId}/checkout` | ✔ | `202` sipariş (`PendingPayment`) | `409 hold_expired`, `409 order_already_exists` (`orderId`), `409 session_cancelled`, `422 payment_method_not_allowed` |
| GET | `/api/orders` · `/api/orders/{orderId}` | | Sipariş + ödeme + iade durumu; `no-store` | `404` |
| POST | `/api/orders/{orderId}/cancel` | ✔ | `200` | `409 order_not_paid`, `409 cancellation_window_closed`, `409 order_has_checked_in_ticket`, `409 session_cancelled` |
| GET | `/api/tickets` | | Kendi biletleri (token **yok**) | |
| GET | `/api/tickets/{ticketId}` | | Token yalnız `Valid` bilette; `no-store` | `404` |

Hold isteği: `{ "seatIds": ["…"] }` (koltuklu) **veya** `{ "items": [{ "categoryId": "…", "quantity": 2 }] }` (genel giriş).

Hold yanıtı:

```json
{
  "id": "…", "sessionId": "…", "status": "Active",
  "expiresAt": "2026-11-20T17:40:00Z", "serverTime": "2026-11-20T17:30:00Z",
  "ticketCount": 2, "totalMinor": 90000, "currency": "TRY",
  "lines": [{ "lineNo": 1, "kind": "Seat", "label": "Salon / C / 12", "quantity": 1, "unitPriceMinor": 45000, "seatId": "…", "categoryId": null }],
  "orderId": null,
  "guarantee": "inventory_reserved_until_expiry_not_a_purchase"
}
```

Checkout isteği `{ "paymentMethod": "tok_success" }`. `202` yanıtı (`note: "order_recorded_payment_not_yet_confirmed"`)
ödemenin alındığı anlamına **gelmez**; istemci `GET /api/orders/{orderId}` ile sonucu izler.

Ödeme yöntemi token'ları (simüle PSP senaryoları; `tok_success` dışındakiler yalnız seans demo simülatörüne
opt-in ise kabul edilir): `tok_success`, `tok_decline`, `tok_transient`, `tok_timeout_success`,
`tok_timeout_decline`, `tok_async_success`, `tok_async_decline`, `tok_async_duplicate`,
`tok_async_out_of_order`, `tok_late_success`, `tok_refund_fail`, `tok_refund_timeout`, `tok_refund_transient`.

## 5. Organizatör

Tüm uçlar sunucuda üyelik doğrular (`OrganizerAdmin`).

| Metot | Yol | Not |
|---|---|---|
| POST | `/api/organizations` | Başvuru → `PendingApproval`; başvuran `OrganizerAdmin` olur |
| GET | `/api/organizer/organizations` · `/{organizationId}` | Üyesi olduğum organizasyonlar |
| PUT | `/api/organizer/organizations/{organizationId}` | Ad |
| GET/POST/DELETE | `/api/organizer/organizations/{organizationId}/members[/{userId}]` | `{ email, role }`; `role ∈ {OrganizerAdmin, GateStaff}`; son yönetici çıkarılamaz |
| GET/POST | `/api/organizer/organizations/{organizationId}/venues` | `timezone` IANA doğrulanır |
| GET/POST | `/api/organizer/venues/{venueId}/layouts` | `POST` yeni **değişmez** plan sürümü üretir |
| GET | `/api/organizer/layouts/{layoutId}/seats` | Plan önizlemesi |
| GET/POST | `/api/organizer/organizations/{organizationId}/events` | |
| GET/PUT | `/api/organizer/events/{eventId}` | |
| POST | `/api/organizer/events/{eventId}/publish` · `/unpublish` | Yayınlanmış seansı olan etkinlik taslağa dönemez |
| POST | `/api/organizer/events/{eventId}/sessions` | Taslak seans; `startsAtLocal` + mekan timezone |
| GET/PUT | `/api/organizer/sessions/{sessionId}` | `PUT` yalnız `Draft` (`409 session_not_editable`) |
| PUT | `/api/organizer/sessions/{sessionId}/seating` | `{ layoutId, tierPrices }`; rezervasyon varsa `409 layout_locked` |
| PUT | `/api/organizer/sessions/{sessionId}/tier-prices` | Tutulmuş/satılmış koltuk içeren kademe `409 price_locked` |
| POST/PUT | `/api/organizer/sessions/{sessionId}/categories[/{categoryId}]` | Kapasite `sold+held` altına inemez → `409 capacity_below_committed` |
| POST | `/api/organizer/sessions/{sessionId}/publish` | `409 publish_guard_failed` (`reasons[]`) |
| POST | `/api/organizer/sessions/{sessionId}/cancel` | Idempotency-Key zorunlu; `202`; kalıcı toplu işi başlatır |
| GET | `/api/organizer/sessions/{sessionId}/cancellation` | İşin durumu ve sayaçları |
| GET/PUT/DELETE | `/api/organizer/sessions/{sessionId}/gate-staff[/{userId}]` | Yalnız kendi organizasyonunun üyesi atanabilir |
| PUT | `/api/organizer/sessions/{sessionId}/demo-simulator` | `{ enabled }` |
| GET | `/api/organizer/sessions/{sessionId}/summary` | §01-5 metrikleri; müşteri verisi içermez |
| GET | `/api/organizer/organizations/{organizationId}/operations` | Operasyon kuyruğu + son denemeler + sayaçlar |
| POST | `/api/organizer/payments/{paymentId}/reconcile` | `{ planned, attempted, outcome: Planned\|Attempted\|Succeeded\|Failed, detailCode }` |
| POST | `/api/organizer/refunds/{refundId}/retry` | Aynı sonuç biçimi; yalnız `Failed` iade (`409 refund_not_retryable`) |

## 6. Kapı (gate)

| Metot | Yol | Not |
|---|---|---|
| GET | `/api/gate/sessions` | Atandığım seanslar; sayım ve pencere bilgisi, müşteri verisi yok |
| POST | `/api/gate/sessions/{sessionId}/check-ins` | Idempotency-Key zorunlu; `{ token, gate }` |

| Sonuç | HTTP | `code` | İçerik |
|---|---|---|---|
| Kabul | `200` | — | `{ result: "Accepted", ticketId, label, checkedInAt, gate }` |
| Zaten kullanılmış | `409` | `already_checked_in` | `result: "AlreadyUsed"`, ilk giriş zamanı ve kapısı (yalnız yetkili personele) |
| Token bulunamadı / biçimsiz | `404` | `ticket_not_found` | `result: "Invalid"` |
| Başka seansın bileti | `409` | `wrong_session` | Diğer seans hakkında bilgi verilmez |
| İptal/iade edilmiş | `409` | `ticket_void` | |
| Pencere dışı | `409` | `outside_checkin_window` | `opensAt`, `closesAt`, `serverTime` |
| Seans iptal | `409` | `session_cancelled` | |
| Atanmamış / üyeliği kalkmış personel | `404` | `not_found` | Tekrar oynatma dahil |
| Veritabanı erişilemez | `503` | `service_unavailable` | Başarı gösterilmez; aynı anahtarla yeniden denenir |

## 7. Platform yöneticisi

| Metot | Yol |
|---|---|
| GET | `/api/admin/organizations?status` |
| POST | `/api/admin/organizations/{organizationId}/approve` · `/suspend` · `/reinstate` |
| GET | `/api/admin/events` |
| POST | `/api/admin/events/{eventId}/takedown` · `/clear-takedown` |
| GET | `/api/admin/operations` · `/api/admin/audit?limit` · `/api/admin/outbox/dead-letters` · `/api/admin/metrics` |
| POST | `/api/admin/outbox/{messageId}/replay` |
| GET/POST | `/api/admin/demo-psp/charges` · `/api/admin/demo-psp/charges/{reference}/resolve` — yalnız simülatör açıkken |

Platform yöneticiliği hiçbir API ile atanamaz. Platform yöneticisi organizatörün kataloğunu yönetemez;
yalnız moderasyon ve operasyon düzeyinde görür.

## 8. Webhook (simüle PSP → uygulama)

`POST /api/webhooks/psp` — çerez/CSRF yok.

- Başlık: `X-Psp-Signature: t=<unix saniye>,v1=<hex>`; `v1 = HMAC-SHA256(secret, "<t>." + ham gövde baytları)`.
- Gövde en fazla 16 KiB (`413 payload_too_large`). Eksik/bozuk başlık veya gövde `400 webhook_malformed`.
- Geçersiz imza `401 webhook_signature_invalid`; geçerli imza ama zaman toleransı (±300 sn) dışı `401 webhook_timestamp_out_of_range`.
  Yapılandırılmış **tüm** aktif secret'lar denenir (rotasyon).
- `id` ile dedupe: aynı `id` + aynı gövde → `200 { outcome: "duplicate" }`; aynı `id` + farklı gövde → `409 webhook_event_conflict`.
- `200` yanıtındaki `outcome`: `applied`, `ignored_stale` (sırasız/eski olay), `ignored_already_final`,
  `ignored_unknown_reference`, `ignored_unknown_type`, `mismatch` (tutar/para birimi uyuşmuyor; uygulanmaz, operatör incelemesine düşer).

## 9. Sağlık ve meta

`/health/live` (süreç), `/health/ready` (veritabanı + bekleyen migration yok), `/openapi/v1.json`.

## 10. Hata kodu kataloğu

`validation_failed, unknown_field, unauthenticated, forbidden, not_found, conflict, csrf_invalid, rate_limited,
payload_too_large, internal_error, service_unavailable, idempotency_key_required, idempotency_key_invalid,
idempotency_key_reused, request_in_progress, invalid_credentials, email_already_registered, weak_password,
user_not_found, last_admin_cannot_leave, unsupported_currency, invalid_timezone, invalid_local_time,
organization_not_active, publish_guard_failed, invalid_state_transition, layout_locked, price_locked,
capacity_below_committed, session_not_editable, moderation_blocked, sales_not_started, sales_closed,
invalid_selection, seats_unavailable, insufficient_capacity, active_hold_exists, hold_expired, hold_not_active,
order_already_exists, payment_method_not_allowed, order_not_paid, cancellation_window_closed,
order_has_checked_in_ticket, refund_not_retryable, ticket_not_found, wrong_session, ticket_void,
already_checked_in, outside_checkin_window, session_cancelled, webhook_signature_invalid,
webhook_timestamp_out_of_range, webhook_event_conflict, webhook_malformed`
