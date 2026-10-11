# ADR 0008 — Yerel HTTPS: Kestrel doğrudan TLS, reverse proxy yok

**Durum:** Kabul edildi (2026-10-10)

## İhtiyaç
`Secure` ve `__Host-` önekli çerezler ile CSRF akışı gerçek HTTPS gerektirir; yerel stack tek akışla açılmalı.

## Karar
Kestrel HTTPS'i doğrudan sonlandırır. `LocalTls:AutoGenerate=true` iken uygulama, `localhost` /
`127.0.0.1` için self-signed sertifikayı ilk açılışta üretir ve kalıcı anahtar volume'una yazar
(sonraki açılışlarda aynı sertifika). Üretimde bu ayar kapalıdır ve sertifika dosyası açıkça verilir.

Proxy olmadığı için `X-Forwarded-*` başlıkları **işlenmez**; rate limit bölümlemesi gerçek bağlantı
adresini kullanır. İletilen başlık işleme bu sürümde hiç yoktur; proxy arkasına alınırsa
güvenilir proxy listesiyle birlikte eklenmelidir.

Süreç içi çağrılar (simülatör ↔ uygulama, konteyner healthcheck) için ayrıca yalnız loopback'e bağlı
bir HTTP dinleyici vardır; dışarıya yayınlanmaz.

## Alternatifler
- *Caddy/Nginx reverse proxy:* otomatik yerel CA; ek imaj, ek servis ve güvenilir proxy yapılandırması.
- *`dotnet dev-certs`:* geliştirici makinesinin sertifika deposunu değiştirir; konteyner içinde taşınması ek adım.

## Maliyet
Tarayıcı ilk açılışta güven uyarısı gösterir (sertifika makineye güvenilir olarak eklenmez — makine
ayarı değiştirilmez). Betikler sertifikayı dışa aktarıp `curl --cacert` ile sabitler.

## Ne zaman değişir
Uygulama bir proxy/CDN arkasına alınırsa.
