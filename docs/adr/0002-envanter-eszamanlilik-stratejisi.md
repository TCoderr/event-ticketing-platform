# ADR 0002 — Envanter eşzamanlılığı: sıralı satır kilidi + koşullu güncelleme + kısıtlar

**Durum:** Kabul edildi (2026-10-10)

## İhtiyaç
Bir koltuk için aynı anda tek sahip; genel girişte `held + sold <= capacity`; çok satırlı hold'da
hep-ya-hiç; gecikmiş süre dolumu worker'ına rağmen doğruluk.

## Karar
- İzolasyon `READ COMMITTED`. Kilitler `SELECT … ORDER BY id FOR UPDATE` ile **deterministik sırada** alınır
  (global sıra: reservation → session → seats/categories → tickets).
- **Koltuk:** seans başına satır (`session_seats`); sahiplik tek sütun. Claim koşulu:
  `status='Available' OR (status='Held' AND held_until <= now)`. Süresi dolmuş hold'un koltuğu yeni
  claim tarafından devralınır; eski rezervasyon satırına dokunulmaz (o, kendi tüketiminde koşullu
  güncellemenin eksik satır saymasıyla süre dolmuş olarak sonuçlanır).
- **Genel giriş:** `held_count / sold_count` sayaçları + `CHECK (held_count + sold_count <= capacity)`.
  Kapasite yetmezse süresi dolmuş hold'lar `FOR UPDATE SKIP LOCKED` ile toplanıp (durumları `Expired`
  yapılarak) sayaç düşürülür, sonra yeniden denenir.
- Deadlock (`40P01`) ve serialization (`40001`) hatalarında transaction en fazla 3 kez, jitter'lı yeniden denenir.
- Tüketim (ödeme başarısı) her zaman rezervasyon satırı kilidi altında ve koşullu güncellemeyle yapılır.

## Alternatifler
- *Yalnız optimistic concurrency (xmin):* çok satırlı claim'de yüksek çakışma ve yeniden deneme fırtınası.
- *`SERIALIZABLE`:* doğru, ama her yolda yeniden deneme gerektirir ve kilit sırası kadar öngörülebilir değil.
- *Redis kilidi / UI devre dışı bırakma:* veritabanı doğruluğunun yerine geçmez; ek bağımlılık.
- *Advisory lock (seans başına):* basit, fakat tüm seansı serileştirir.

## Maliyet
Dar bir alanda elle yazılmış parametreli SQL; GA'da başka işlem tarafından kilitlenmiş süresi dolmuş
hold o an toplanamaz (geçici, temkinli "yetersiz kapasite" — asla oversell değil).

## Ne zaman değişir
Ölçüm, tek seansta kilit beklemesinin darboğaz olduğunu gösterirse (kuyruk/bekleme odası — kapsam dışı).
