# ADR 0009 — Bilet token'ı: CSPRNG taşıyıcı token, özet ile arama, şifreli saklama

**Durum:** Kabul edildi (2026-10-10)

## İhtiyaç
QR tahmin edilemez olmalı, kişisel veri taşımamalı; veritabanı sızıntısı tek başına geçerli bilet
üretmemeli; sahibi biletini sonradan yeniden görüntüleyebilmeli.

## Karar
- Token: 32 bayt CSPRNG → `LT1.<base64url>`.
- Arama: `token_hash = SHA-256(token)`, unique indeks. (256 bit entropili girdide anahtarsız özet
  kaba kuvvete karşı yeterlidir.)
- Yeniden gösterim: token ayrıca ASP.NET Data Protection ile şifrelenip `token_protected` sütununda tutulur.
  Anahtarlar veritabanında değil, ayrı volume'dadır.
- Bilet kimliği deterministiktir: `UUIDv5(sipariş, satır, sıra)`; `(reservation_line_id, ordinal)` unique.
  Tekrarlanan issuance `ON CONFLICT DO NOTHING` ile etkisizdir.
- QR istemcide yerel kütüphaneyle çizilir.

## Alternatifler
- *Yalnız özet sakla, token'ı bir kez göster:* en güvenlisi; ama "Biletlerim" ekranı yeniden gösteremez.
- *İmzalı (HMAC/JWT) içerikli QR:* offline doğrulama sağlar (kapsam dışı), iptal için yine sunucu gerekir.
- *Token'ı düz metin sakla:* veritabanı dökümü tüm biletleri açığa çıkarır.

## Maliyet
Data Protection anahtar volume'u yedeklenmeden kaybolursa mevcut biletlerin QR'ı yeniden
gösterilemez (özet durur; yeniden üretim gerekir — bu sürümde uygulanmadı).

## Ne zaman değişir
Offline check-in gereksinimi (imzalı içerik + cihaz senkronu).
