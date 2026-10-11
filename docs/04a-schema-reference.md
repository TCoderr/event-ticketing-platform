# 04a — Şema Başvurusu (veritabanından üretildi)

Bu dosya, tüm migration'lar uygulanmış yerel stack'in `information_schema` / `pg_catalog` çıktısından üretilmiştir
(2026-10-10). Tasarım gerekçeleri `04-database-design.md` içindedir; otorite migration dosyalarıdır.


## catalog

| Tablo | Sütunlar | CHECK kısıtları | Unique indeksler |
|---|---|---|---|
| `catalog.events` | id, organization_id, title, slug, description, category, status, moderation_blocked_at, is_demo, created_at, updated_at | `ck_events_category`, `ck_events_status` | `ix_events_slug` |
| `catalog.layout_seats` | id, layout_id, section, row_label, seat_number, x, y, tier_code | — | `ix_layout_seats_layout_id_section_row_label_seat_number` |
| `catalog.seat_layouts` | id, venue_id, version, name, seat_count, created_at | — | `ix_seat_layouts_venue_id_version` |
| `catalog.sessions` | id, event_id, organization_id, venue_id, city, timezone, starts_at, sales_start_at, sales_end_at, sales_mode, status, hold_ttl_seconds, layout_id, demo_simulator_enabled, created_at, cancelled_at | `ck_sessions_hold_ttl`, `ck_sessions_sales_mode`, `ck_sessions_sales_window`, `ck_sessions_status` | — |
| `catalog.venues` | id, organization_id, name, city, address, timezone, created_at | — | — |

## identity

| Tablo | Sütunlar | CHECK kısıtları | Unique indeksler |
|---|---|---|---|
| `identity.organization_members` | organization_id, user_id, role, created_at | `ck_organization_members_role` | — |
| `identity.organizations` | id, name, slug, status, is_demo, created_at | `ck_organizations_status` | `ix_organizations_slug` |
| `identity.user_claims` | id, user_id, claim_type, claim_value | — | — |
| `identity.user_logins` | login_provider, provider_key, provider_display_name, user_id | — | — |
| `identity.user_sessions` | id, user_id, created_at, expires_at, last_seen_at, revoked_at, revoke_reason | — | — |
| `identity.user_tokens` | user_id, login_provider, name, value | — | — |
| `identity.users` | id, display_name, is_platform_admin, is_demo, created_at, disabled_at, user_name, normalized_user_name, email, normalized_email, email_confirmed, password_hash, security_stamp, concurrency_stamp, phone_number, phone_number_confirmed, two_factor_enabled, lockout_end, lockout_enabled, access_failed_count | — | `UserNameIndex` |

## inventory

| Tablo | Sütunlar | CHECK kısıtları | Unique indeksler |
|---|---|---|---|
| `inventory.ga_categories` | id, session_id, name, price_minor, capacity, held_count, sold_count, sort_order | `ck_ga_categories_capacity`, `ck_ga_categories_counts`, `ck_ga_categories_price` | `ix_ga_categories_session_id_name` |
| `inventory.reservation_lines` | id, reservation_id, line_no, kind, session_seat_id, ga_category_id, quantity, unit_price_minor, label | `ck_reservation_lines_kind`, `ck_reservation_lines_price`, `ck_reservation_lines_quantity` | `ix_reservation_lines_reservation_id_line_no` |
| `inventory.reservations` | id, session_id, user_id, status, created_at, expires_at, ticket_count, total_minor, currency, closed_at, close_reason | `ck_reservations_closed`, `ck_reservations_currency`, `ck_reservations_status`, `ck_reservations_ticket_count`, `ck_reservations_total` | `ux_reservations_one_active_per_user_session (kısmi)` |
| `inventory.session_seats` | id, session_id, section, row_label, seat_number, x, y, tier_code, price_minor, status, hold_reservation_id, held_until | `ck_session_seats_ownership`, `ck_session_seats_price`, `ck_session_seats_status` | `ix_session_seats_session_id_section_row_label_seat_number` |

## ops

| Tablo | Sütunlar | CHECK kısıtları | Unique indeksler |
|---|---|---|---|
| `ops.__ef_migrations_history` | migration_id, product_version | — | — |
| `ops.audit_log` | id, occurred_at, actor_user_id, actor_kind, action, entity_type, entity_id, organization_id, data, correlation_id | — | — |
| `ops.idempotency_records` | user_id, operation, idempotency_key, hash_version, request_hash, response_status, response_content_type, response_location, response_body, created_at, expires_at | — | — |
| `ops.inbox_messages` | source, event_id, payload_hash, type, received_at, outcome | — | — |
| `ops.outbox_messages` | id, sequence, type, payload, correlation_id, created_at, available_at, attempt_count, lease_owner, lease_token, lease_until, processed_at, dead_lettered_at, last_error_code | — | `ix_outbox_messages_sequence` |

## orders

| Tablo | Sütunlar | CHECK kısıtları | Unique indeksler |
|---|---|---|---|
| `orders.orders` | id, reservation_id, user_id, session_id, organization_id, order_number, status, total_minor, currency, created_at, paid_at, closed_at, cancel_reason | `ck_orders_cancel_reason`, `ck_orders_currency`, `ck_orders_paid_at`, `ck_orders_status`, `ck_orders_total` | `ix_orders_order_number`, `ux_orders_reservation` |
| `orders.payments` | id, order_id, merchant_reference, provider_payment_id, status, amount_minor, currency, method_token, failure_kind, failure_code, last_provider_sequence, attempt_count, next_reconcile_at, lease_owner, lease_token, lease_until, late_success_at, review_reason, created_at, updated_at, resolved_at | `ck_payments_amount`, `ck_payments_currency`, `ck_payments_failure_consistency`, `ck_payments_failure_kind`, `ck_payments_status` | `ix_payments_merchant_reference`, `ix_payments_order_id` |
| `orders.reconciliation_runs` | id, target_type, target_id, organization_id, requested_by, outcome, detail_code, created_at, updated_at | `ck_reconciliation_runs_outcome`, `ck_reconciliation_runs_target` | — |
| `orders.refunds` | id, payment_id, order_id, merchant_reference, provider_refund_id, reason, status, amount_minor, currency, attempt_count, last_error_code, created_at, updated_at, resolved_at | `ck_refunds_amount`, `ck_refunds_currency`, `ck_refunds_reason`, `ck_refunds_status` | `ix_refunds_merchant_reference`, `ux_refunds_payment` |
| `orders.session_cancellation_jobs` | session_id, organization_id, requested_by, status, cursor_reservation_id, processed_count, refund_count, lease_owner, lease_token, lease_until, created_at, completed_at | `ck_session_cancellation_jobs_status` | — |

## pspsim

| Tablo | Sütunlar | CHECK kısıtları | Unique indeksler |
|---|---|---|---|
| `pspsim.__ef_migrations_history` | migration_id, product_version | — | — |
| `pspsim.charges` | id, merchant_reference, amount_minor, currency, method_token, status, decline_code, pending_outcome, sequence, create_attempts, created_at, resolve_at | — | — |
| `pspsim.refunds` | id, merchant_reference, charge_id, amount_minor, status, failure_code, create_attempts, created_at | — | — |
| `pspsim.webhook_deliveries` | id, ordinal, event_id, body, deliver_at, attempts, delivered_at, last_status | — | — |

## tickets

| Tablo | Sütunlar | CHECK kısıtları | Unique indeksler |
|---|---|---|---|
| `tickets.gate_assignments` | session_id, user_id, assigned_by, created_at | — | — |
| `tickets.scan_log` | id, session_id, ticket_id, staff_user_id, result, gate, created_at | — | — |
| `tickets.tickets` | id, order_id, reservation_id, reservation_line_id, ordinal, session_id, organization_id, owner_user_id, session_seat_id, ga_category_id, label, status, token_hash, token_protected, issued_at, checked_in_at, checked_in_by, checked_in_gate, voided_at, void_reason | `ck_tickets_checked_in`, `ck_tickets_ordinal`, `ck_tickets_status`, `ck_tickets_valid_unused`, `ck_tickets_voided` | `ix_tickets_reservation_line_id_ordinal`, `ix_tickets_token_hash`, `ux_tickets_one_live_ticket_per_seat (kısmi)` |
