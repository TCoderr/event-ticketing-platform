# ADR 0003 — Idempotency kaydı iş etkisiyle aynı transaction'da

**Durum:** Kabul edildi (2026-10-10)

## İhtiyaç
Aynı anahtarla tekrar eden istek ilk sonucu bayt bayt döndürmeli; commit olup yanıt kaybolduysa bile.
Çökme sonrası "takılı kalan işlem sürüyor" kaydı oluşmamalı. PostgreSQL'de unique ihlali transaction'ı
iptal ettiğinden, ihlal sonrası aynı transaction'da sorgu yapılamaz.

## Karar
Idempotent uçların HTTP yolu yalnız veritabanı işi yapar (sağlayıcı çağrıları outbox handler'larındadır).
Bu sayede talep, iş etkisi ve saklanan yanıt **tek transaction**dır:

1. `INSERT … ON CONFLICT DO NOTHING` — satır eklendiyse talep bizdedir.
2. Eklenmediyse satır okunur: istek özeti farklı → `409`; aynı → saklanan yanıt oynatılır.
3. İş mantığı çalışır; 2xx yanıtın baytları aynı satıra yazılır; commit.
4. 2xx olmayan sonuçta transaction geri alınır (talep dahil); istemci aynı anahtarla yeniden deneyebilir.

Eşzamanlı aynı anahtarlı istek, ilk transaction bitene kadar unique indeks üzerinde bekler
(`lock_timeout` → `409 request_in_progress`).

Yanıt gövdesi `bytea` olarak saklanır (jsonb bayt korumaz). Kapsam `(user_id, operation, key)`.
Tekrar oynatmadan önce kimlik/sahiplik/yetki kontrolleri yeniden çalışır.

## Alternatifler
- *Kalıcı `InProgress` + ayrı tamamlama transaction'ı:* sağlayıcı çağrısını istek içinde yapmak
  gerekseydi şarttı; çökme sonrası kurtarma mantığı ve süre aşımı gerektirir.
- *Unique ihlalini yakala + savepoint:* çalışır, daha kırılgan.
- *Yalnız iş düzeyi tekillik:* yanıt kaybında aynı baytları döndüremez.

## Maliyet
Checkout `202` döner ve sonuç ayrı okunur (istemci yoklar). Reddedilen istekler saklanmadığı için
aynı anahtarla tekrar denendiğinde sonuç değişebilir (ör. koltuk boşalmışsa başarı).

## Ne zaman değişir
Bir idempotent uç istek içinde senkron dış çağrı yapmak zorunda kalırsa.
