# ADR 0007 — Simüle PSP: anında tahsilat, HTTP sınırı, ayrı şema

**Durum:** Kabul edildi (2026-10-10)

## İhtiyaç
Gerçek ödeme yok. Yine de timeout, yanıt kaybı, tekrarlı/sırasız callback ve geç başarı gibi sınır
dışı hataların gerçekten yaşanabildiği bir sağlayıcı gerekir; "mock başarı döndürür" yetmez.

## Karar
- **Model: anında tahsilat (immediate charge).** Provizyon/capture ayrımı yoktur; bu nedenle telafi her
  zaman **iade (refund)** olur. **Void uygulanmaz** — void yalnız henüz tahsil edilmemiş provizyonu
  iptal eder ve bu modelde böyle bir durum oluşmaz.
- Simülatör aynı süreçte barınır, fakat kendi şeması (`pspsim`), kendi DbContext'i ve migration
  geçmişi vardır. Uygulama onunla yalnız **HTTP** (API anahtarı) üzerinden konuşur; simülatör
  uygulamaya yalnız **HMAC imzalı webhook** ile döner. Uygulama kodu `pspsim` tablolarına erişmez.
- Sağlayıcı komutları `merchant_reference` ile idempotent'tir; aynı referansla tekrar yeni tahsilat/iade yaratmaz.
- Senaryo, ödeme yöntemi token'ı ile deterministik seçilir (`tok_success`, `tok_decline`,
  `tok_transient`, `tok_timeout_success`, `tok_async_success`, `tok_async_duplicate`,
  `tok_async_out_of_order`, `tok_late_success`, `tok_refund_fail`, `tok_refund_timeout`).
- Yanıt kaybı: simülatör işlemi commit eder, ardından yanıtı istemci zaman aşımını geçecek şekilde tutar.

## Alternatifler
- *Bellek içi sahte adaptör:* ağ/zaman aşımı/yanıt kaybı gerçekçi olmaz; ayrı durum tutmaz.
- *Ayrı konteyner:* gerçek ağ sınırı ve "sağlayıcı kapalı" senaryosu; ikinci imaj/servis maliyeti.
  Kod zaten yalnız HTTP kullandığı için sonradan ayrılabilir.
- *Provizyon + capture:* void ve capture zaman aşımı gibi ek durumlar; ürün gereksinimi yok.

## Maliyet
İkinci migration seti; simülatör ile uygulama aynı süreçte öldüğü için "yalnız sağlayıcı kapalı"
durumu Compose düzeyinde değil, hata senaryosu token'larıyla üretilir.

## Ne zaman değişir
Gerçek PSP entegrasyonu: `IPaymentProvider` uygulaması ve webhook doğrulaması değişir; durum
makineleri ve telafi mantığı aynı kalır.
