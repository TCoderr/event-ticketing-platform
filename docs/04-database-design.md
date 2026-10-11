# 04 — Veritabanı Tasarımı

PostgreSQL 18. Adlandırma `snake_case`. Tüm birincil anahtarlar `uuid` (UUIDv7) — aksi belirtilmedikçe.
Zamanlar `timestamptz` (UTC, mikrosaniye). Para `bigint` minor unit + `char(3)` para birimi.
Enum'lar `text` olarak saklanır ve `CHECK` ile sınırlanır.

> Bu belge migration'larla birlikte güncellenir. Otorite: `src/Loca.Api/Infrastructure/Persistence/Migrations`
> (simülatör: `src/Loca.Api/PspSimulator/Migrations`). Tablo/sütun/kısıt dökümü: `04a-schema-reference.md`.
>
> Migration sırası: `InitialIdentity` → `Catalog` → `InventoryHolds` → `OrdersPayments` → `TicketsCheckIn` → `Cancellation`;
> simülatör: `InitialSimulator`.

## 1. Roller

| Rol | Kullanım | Yetki |
|---|---|---|
| `loca_owner` | Yalnız `migrate` kipi | Şema sahibi, DDL |
| `loca_app` | Çalışma zamanı | Yalnız DML; DDL yok; `TRUNCATE` yok |

`loca_app` kısıtları (migration içinde `GRANT/REVOKE`, gerçek veritabanında testle doğrulanır):

- `ops.audit_log`, `tickets.scan_log`: yalnız `INSERT, SELECT`.
- Migration geçmişi tabloları (`ops.__ef_migrations_history`, `pspsim.__ef_migrations_history`): yalnız `SELECT`.
- `inventory.reservation_lines`, `catalog.layout_seats`, `catalog.seat_layouts`: yalnız `INSERT, SELECT`
  (değişmez snapshot).
- Diğer tablolar: `SELECT, INSERT, UPDATE, DELETE`.

## 2. Şemalar ve tablolar

### identity

| Tablo | Önemli sütunlar | Kısıt / indeks |
|---|---|---|
| `users` | Identity alanları, `display_name`, `is_platform_admin`, `is_demo`, `disabled_at` | `normalized_user_name` unique (kullanıcı adı = e-posta) |
| `user_sessions` | `id`, `user_id`, `created_at`, `expires_at`, `last_seen_at`, `revoked_at`, `revoke_reason` | `(user_id) WHERE revoked_at IS NULL` |
| `organizations` | `name`, `slug`, `status`, `is_demo` | `slug` unique; `status IN (PendingApproval, Active, Suspended)` |
| `organization_members` | `organization_id`, `user_id`, `role` | PK `(organization_id, user_id)`; `role IN (OrganizerAdmin, GateStaff)` |

### catalog

| Tablo | Önemli sütunlar | Kısıt / indeks |
|---|---|---|
| `venues` | `organization_id`, `name`, `city`, `address`, `timezone` | `(organization_id)` |
| `seat_layouts` | `venue_id`, `version`, `name`, `seat_count` | unique `(venue_id, version)`; değişmez |
| `layout_seats` | `layout_id`, `section`, `row_label`, `seat_number`, `x`, `y`, `tier_code` | unique `(layout_id, section, row_label, seat_number)`; değişmez |
| `events` | `organization_id`, `title`, `slug`, `description`, `category`, `status`, `moderation_blocked_at`, `is_demo` | `slug` unique; durum ve kategori CHECK |
| `sessions` | `event_id`, `organization_id`, `venue_id`, `starts_at`, `timezone`, `sales_mode`, `status`, `sales_start_at`, `sales_end_at`, `hold_ttl_seconds`, `layout_id`, `demo_simulator_enabled`, `city`, `cancelled_at` | CHECK `sales_start_at < sales_end_at AND sales_end_at <= starts_at`; CHECK `hold_ttl_seconds BETWEEN 5 AND 1800`; keşif indeksi `(starts_at, id) WHERE status='Published'` |

### inventory

| Tablo | Önemli sütunlar | Kısıt / indeks |
|---|---|---|
| `session_seats` | `session_id`, `section`, `row_label`, `seat_number`, `x`, `y`, `tier_code`, `price_minor`, `status`, `hold_reservation_id`, `held_until` | unique `(session_id, section, row_label, seat_number)`; **durum tutarlılığı CHECK** (aşağıda); `price_minor >= 0` |
| `ga_categories` | `session_id`, `name`, `price_minor`, `capacity`, `held_count`, `sold_count`, `sort_order` | CHECK `held_count >= 0 AND sold_count >= 0 AND held_count + sold_count <= capacity`; unique `(session_id, name)` |
| `reservations` | `session_id`, `user_id`, `status`, `created_at`, `expires_at`, `ticket_count`, `total_minor`, `currency`, `closed_at`, `close_reason` | CHECK `ticket_count BETWEEN 1 AND 6`; CHECK `currency='TRY'`; **unique `(user_id, session_id) WHERE status='Active'`**; `(expires_at) WHERE status='Active'` |
| `reservation_lines` | `reservation_id`, `line_no`, `kind`, `session_seat_id`, `ga_category_id`, `quantity`, `unit_price_minor`, `label` | unique `(reservation_id, line_no)`; CHECK tür tutarlılığı; CHECK `quantity BETWEEN 1 AND 6`; değişmez |

`session_seats` durum CHECK'i:

```sql
(status = 'Available' AND hold_reservation_id IS NULL     AND held_until IS NULL)
OR (status = 'Held'   AND hold_reservation_id IS NOT NULL AND held_until IS NOT NULL)
OR (status = 'Sold'   AND hold_reservation_id IS NOT NULL AND held_until IS NULL)
```

Bir koltuğun sahibi tek sütundur (`hold_reservation_id`); aynı anda iki sahip yapısal olarak imkânsızdır.
Ek savunma: `tickets` üzerinde `(session_seat_id) WHERE status IN ('Valid','CheckedIn')` unique.

### orders

| Tablo | Önemli sütunlar | Kısıt / indeks |
|---|---|---|
| `orders` | `reservation_id`, `user_id`, `session_id`, `organization_id`, `order_number`, `status`, `total_minor`, `currency`, `created_at`, `paid_at`, `closed_at`, `cancel_reason` | **unique `reservation_id`**; unique `order_number`; `(user_id, created_at)`; `(session_id, status)` |
| `payments` | `order_id`, `merchant_reference`, `provider_payment_id`, `status`, `amount_minor`, `currency`, `method_token`, `failure_kind`, `failure_code`, `last_provider_sequence`, `attempt_count`, `next_reconcile_at`, `lease_owner`, `lease_token`, `lease_until`, `late_success_at`, `review_reason` | **unique `order_id`**; unique `merchant_reference`; `(next_reconcile_at) WHERE status IN ('Pending','Unknown')` |
| `refunds` | `payment_id`, `order_id`, `merchant_reference`, `reason`, `status`, `amount_minor`, `attempt_count`, `last_error_code`, `created_at`, `resolved_at` | **unique `payment_id`**; unique `merchant_reference`; `(status)` |
| `reconciliation_runs` | `target_type`, `target_id`, `organization_id`, `requested_by`, `outcome`, `detail_code`, `created_at`, `updated_at` | `outcome IN (Planned, Attempted, Succeeded, Failed)`; `target_type IN (payment, refund)` |
| `session_cancellation_jobs` | `session_id` (PK), `organization_id`, `requested_by`, `status`, `cursor_reservation_id`, `processed_count`, `refund_count`, `lease_owner`, `lease_token`, `lease_until`, `completed_at` | `status IN (Pending, Completed)` |

### tickets

| Tablo | Önemli sütunlar | Kısıt / indeks |
|---|---|---|
| `tickets` | `id` (UUIDv5: sipariş+satır+sıra), `order_id`, `reservation_id`, `reservation_line_id`, `ordinal`, `session_id`, `organization_id`, `owner_user_id`, `session_seat_id`, `ga_category_id`, `label`, `status`, `token_hash`, `token_protected`, `issued_at`, `checked_in_at`, `checked_in_by`, `checked_in_gate`, `voided_at` | **unique `(reservation_line_id, ordinal)`**; **unique `token_hash`**; partial unique `(session_seat_id) WHERE status IN ('Valid','CheckedIn')` |
| `gate_assignments` | `session_id`, `user_id`, `assigned_by`, `created_at` | PK `(session_id, user_id)` |
| `scan_log` | `id` (identity), `session_id`, `ticket_id`, `staff_user_id`, `result`, `gate`, `created_at` | yalnız ekleme; token içermez |

### ops

| Tablo | Önemli sütunlar | Kısıt / indeks |
|---|---|---|
| `idempotency_records` | `user_id`, `operation`, `idempotency_key`, `hash_version`, `request_hash`, `response_status`, `response_content_type`, `response_location`, `response_body bytea`, `created_at`, `expires_at` | PK `(user_id, operation, idempotency_key)`; `(expires_at)` |
| `outbox_messages` | `id`, `sequence bigserial`, `type`, `payload jsonb`, `correlation_id`, `created_at`, `available_at`, `attempt_count`, `lease_owner`, `lease_token`, `lease_until`, `processed_at`, `dead_lettered_at`, `last_error_code` | `(available_at) WHERE processed_at IS NULL AND dead_lettered_at IS NULL` |
| `inbox_messages` | `source`, `event_id`, `payload_hash`, `type`, `received_at`, `outcome` (`applied`, `ignored_stale`, `ignored_already_final`, `ignored_unknown_reference`, `ignored_unknown_type`, `mismatch`) | PK `(source, event_id)` |
| `audit_log` | `id bigserial`, `occurred_at`, `actor_user_id`, `actor_kind`, `action`, `entity_type`, `entity_id`, `organization_id`, `data jsonb`, `correlation_id` | yalnız ekleme |

### pspsim (ayrı DbContext, ayrı migration geçmişi)

`charges` (`merchant_reference` unique), `refunds` (`merchant_reference` unique), `webhook_deliveries`.
Uygulama modülleri bu şemaya dokunmaz; yalnız simülatör kodu erişir.

## 3. Yabancı anahtarlar ve silme davranışı

- Finans ve bilet zinciri (`reservations → orders → payments → refunds`, `tickets`) içinde tüm FK'ler
  `ON DELETE RESTRICT`. Hiçbir saklama (retention) işi bu tabloları silmez.
- `idempotency_records`, `inbox_messages`, `outbox_messages` **hiçbir iş tablosuna FK vermez ve
  hiçbir iş tablosu bunlara FK vermez.** TTL ile silinmeleri finans kaydını etkileyemez.
- `user_sessions.user_id → users` `ON DELETE CASCADE` (kullanıcı silme işlevi yoktur; yalnız `disabled_at`).

## 4. Saklama süreleri (demo varsayımı; "KVKK uyumlu" iddiası değildir)

| Veri | Süre | İş |
|---|---|---|
| `idempotency_records` | 24 saat (`expires_at`) | RetentionWorker siler |
| `inbox_messages` | 30 gün (webhook yeniden oynatma penceresi 5 dk'nın çok üstünde) | RetentionWorker siler |
| `outbox_messages` (işlenmiş) | 7 gün | RetentionWorker siler; dead-letter silinmez |
| `user_sessions` (bitmiş/iptal) | 30 gün | RetentionWorker siler |
| Sipariş / ödeme / iade / bilet / audit | Silinmez | — |

Idempotency kaydı silindikten sonra aynı anahtarla gelen istek yeniden yürütülür; çift etkiyi iş
düzeyi unique kısıtlar engeller (`orders.reservation_id`, `refunds.payment_id`, bilet durumu).

## 5. Idempotency kaydı ve transaction

Kayıt, iş etkisiyle **aynı transaction'da** eklenir ve tamamlanır:

```sql
INSERT INTO ops.idempotency_records (user_id, operation, idempotency_key, hash_version, request_hash, ...)
VALUES (...) ON CONFLICT (user_id, operation, idempotency_key) DO NOTHING;
-- 1 satır  → talep bizde; iş mantığı çalışır; yanıt baytları aynı satıra yazılır; COMMIT
-- 0 satır  → SELECT; hash farklıysa 409; aynıysa saklanan yanıt oynatılır
```

Eşzamanlı aynı anahtarlı ikinci istek, ilkinin commit/rollback'ine kadar unique indeks üzerinde bekler
(`lock_timeout` ile sınırlı; aşımda `409 request_in_progress`). Bu nedenle kalıcı bir `InProgress`
durumu yoktur: süreç çökerse transaction geri alınır ve takılı kayıt kalmaz. Unique ihlali
istisnası üretilmediği için "hata sonrası aynı transaction'da sorgu" sorunu oluşmaz.

## 6. Migration politikası

- `dotnet ef migrations has-pending-model-changes --context AppDbContext` ve `--context PspSimDbContext` CI kapısıdır.
- Migration'lar boş veritabanına ve bir önceki aşamanın sentetik verili snapshot'ına uygulanarak test edilir.
- Çalışma zamanı migration çalıştırmaz; `serve` kipi bekleyen migration varsa hazır (ready) olmaz.
- `migrate` kipi her çalıştığında çalışma zamanı rolünün yetkilerini baştan kurar (`Migrator.SyncPermissionsAsync`); yetkiler migration dosyalarında değildir.
