# ADR 0006 — Veritabanı outbox/inbox ve in-process worker'lar; broker yok

**Durum:** Kabul edildi (2026-10-10)

## İhtiyaç
Ödeme başlatma, bilet üretimi ve iade gönderimi gibi yan etkiler, iş değişikliğiyle atomik olarak
kaydedilmeli ve çökme sonrası kaybolmadan tamamlanmalı.

## Karar
- `ops.outbox_messages` iş değişikliğiyle aynı transaction'da yazılır.
- Dispatcher mesajı **kiralar**: `lease_owner`, `lease_token` (her kiralamada yeni), `lease_until`.
  Tamamlama/erteleme güncellemeleri `WHERE lease_token = @mine` koşulludur (fencing): süresi dolmuş
  eski sahip, yeni sahibin sonucunu ezemez.
- Sınırlı yeniden deneme: üstel geri çekilme + jitter; bütçe bitince dead-letter. Operatör yeniden oynatır.
- Handler'lar idempotent'tir (teslim en az bir kez). Sıra varsayılmaz.
- `ops.inbox_messages`: `(source, event_id)` birincil anahtarı ile dedupe; iş etkisiyle aynı transaction.
  Aynı kimlik + farklı gövde özeti çakışma olarak kaydedilir.
- Tümüyle tek transaction'da biten işler (hold süre dolumu) lease yerine `FOR UPDATE SKIP LOCKED` kullanır.

## Alternatifler
- *RabbitMQ/Kafka:* harici tüketici yok; ek işletim yüzeyi ve yine outbox ihtiyacı.
- *İstek içinde senkron sağlayıcı çağrısı:* transaction dışı etki ile iş durumu arasında çökme penceresi.
- *Hangfire/Quartz:* kendi şeması ve semantiği; ihtiyaç bu kadar dar iken fazla.

## Maliyet
Yoklama gecikmesi (commit sonrası süreç içi sinyal ile azaltılır); tek veritabanı hem iş hem kuyruk yükünü taşır.

## Ne zaman değişir
Harici tüketiciler, yüksek olay hacmi veya servis ayrışması.
