# 06 — Güvenlik ve Hata Davranışı

## 1. Kimlik doğrulama ve oturum

- ASP.NET Core Identity kullanıcı deposu (parola özeti, kilitlenme). Aynı origin SPA için çerez tabanlı
  oturum; JWT/OAuth2/OIDC eklenmedi (üçüncü taraf istemci yok).
- Çerez: `__Host-loca-session`, `HttpOnly`, `Secure`, `SameSite=Lax`, `Path=/`. Yalnız HTTPS.
- **Sunucu oturum kaydı** (`identity.user_sessions`): çerez `sid` taşır. Her istekte `sid` veritabanından
  doğrulanır (iptal edilmemiş, süresi dolmamış, kullanıcı devre dışı değil, `security_stamp` eşleşiyor).
  Logout satırı `revoked_at` ile işaretler; eski çerezin yeniden gönderilmesi `401` alır. Yalnız tarayıcı
  çerezini silmek iptal sayılmaz.
- Süreler (demo varsayımı): mutlak 12 saat, boşta 2 saat.
- Rol, organizasyon üyeliği ve kapı ataması çerezde **taşınmaz**; her istekte veritabanından okunur.
- Kimlik doğrulama hataları JSON `ProblemDetails` (`401 unauthenticated`, `403 forbidden`).

## 2. CSRF

- `GET /api/auth/csrf` anonim token üretir (çerez `__Host-loca-csrf` + gövdede istek token'ı).
- Login anonim token ister (login CSRF'e karşı); başarılı login kullanıcıya bağlı yeni token döner.
- Tüm güvensiz `/api` metotları `X-CSRF-TOKEN` doğrular. Webhook HMAC ile korunur, CSRF dışıdır.
- Token yalnız bu uçta üretilir; katalog `GET`'leri antiforgery çerezi yazmaz ve `Cache-Control`
  başlıkları bozulmaz (testle doğrulanır).

## 3. Yetki matrisi

| Kaynak / işlem | Customer | OrganizerAdmin (kendi org.) | GateStaff (atandığı seans) | PlatformAdmin |
|---|---|---|---|---|
| Public katalog | ✔ | ✔ | ✔ | ✔ |
| Kendi hold / sipariş / bilet | ✔ | ✔ (müşteri olarak) | ✔ (müşteri olarak) | ✔ (müşteri olarak) |
| Başkasının sipariş / bileti | ✘ (404) | ✘ | ✘ | ✘ |
| Mekan / plan / etkinlik / seans yönetimi | ✘ | ✔ | ✘ | ✘ |
| Üye ve kapı ataması | ✘ | ✔ | ✘ | ✘ |
| Satış özeti, operasyon kuyruğu | ✘ | ✔ (yalnız kendi) | ✘ | ✔ (tümü) |
| Check-in | ✘ | ✘ (kendini atarsa ✔) | ✔ | ✘ |
| Müşteri listesi / finans | ✘ | Özet düzeyinde | ✘ | Operasyon düzeyinde |
| Organizasyon onayı, yayından kaldırma | ✘ | ✘ | ✘ | ✔ |
| Audit, dead-letter, metrikler | ✘ | ✘ | ✘ | ✔ |

- `is_platform_admin` hiçbir API ile yazılamaz; yalnız `seed-demo` CLI kipi atar.
- Mass assignment: istek DTO'ları yalnız izin verilen alanları içerir; bilinmeyen alan reddedilir.
  Üye rolü yalnız `OrganizerAdmin | GateStaff` olabilir.
- Organizatör yanıtları müşteri e-postası/adı içermez; kapı yanıtı yalnız bilet etiketi içerir.

## 4. Rate limit

| Uç | Bölümleme | Limit (varsayılan) | Gerekçe |
|---|---|---|---|
| Login | Bağlantı adresi | 10 / dk | Parola deneme; ayrıca hesap kilidi (5 hata → 5 dk) |
| Register | Bağlantı adresi | 5 / dk | Hesap üretimi |
| Public katalog | Bağlantı adresi | 300 / dk | Kazıma / gürültü |
| Hold oluşturma | Kullanıcı | 20 / dk | Envanter kurcalama |
| Checkout / iptal | Kullanıcı | 10 / dk | Ödeme yüzeyi |
| Check-in | Personel kullanıcı | 120 / dk | Hızlı tarama + token tahmini sınırı |
| Webhook | Global | 600 / dk | Taşma koruması |

Bölümleme anahtarı kimliği doğrulanmış uçlarda kullanıcıdır; müşteriler tek kovayı paylaşmaz.
`X-Forwarded-*` başlıkları **hiç işlenmez** (Kestrel TLS'i doğrudan sonlandırır; testle doğrulanır). Uygulama bir proxy
arkasına alınacaksa, güvenilir proxy listesiyle iletilen başlık işleme eklenmelidir; bu sürümde yoktur.

## 5. Bilet token'ı

- 32 bayt CSPRNG, `LT1.` önekli base64url. QR içeriği yalnız bu token'dır; ad/telefon/e-posta yok.
- Veritabanında arama için `SHA-256(token)` (`token_hash`, unique) saklanır. Token yeterince yüksek
  entropili olduğundan anahtarsız özet yeterlidir.
- Sahibine yeniden gösterebilmek için token ayrıca ASP.NET Data Protection ile şifreli saklanır
  (`token_protected`). Data Protection anahtarları veritabanında değil, ayrı bir volume'dadır;
  yalnız veritabanı dökümü token'ları açığa çıkarmaz.
- Token loglanmaz, `scan_log` ve audit içinde yer almaz. Token içeren yanıtlar `Cache-Control: no-store`.
- QR istemcide yerel kütüphaneyle üretilir; harici QR servisine istek yapılmaz.

## 6. Webhook güvenliği

§05-8. İmza ham istek baytları üzerinden hesaplanır (parse/yeniden serileştirme yok), sabit zamanlı
karşılaştırılır. Secret rotasyonu: yapılandırmada birden çok aktif secret; yeni secret eklenir, sağlayıcı
geçince eski kaldırılır. Dedupe penceresi (30 gün) yeniden oynatma toleransından (5 dk) uzundur.

## 7. Hata davranışı

| Durum | Davranış |
|---|---|
| Sağlayıcı timeout / ağ / 5xx | Ödeme `Unknown`; aynı `merchant_reference` ile sınırlı yeniden deneme; sonra sorgu (reconciliation). Ret sayılmaz |
| Sağlayıcı işlemi kaydetti, yanıt kayboldu | Yeniden deneme yeni tahsilat yaratmaz (sağlayıcı referansla idempotent); sorgu sonucu uygulanır |
| Commit başarılı, HTTP yanıtı kayboldu | Aynı Idempotency-Key ilk yanıtı döndürür |
| Ödeme başarısı hold süresinden sonra | Geç başarı: sipariş `Paid` olmaz, tek telafi iadesi, operasyon kuyruğunda görünür |
| İade sağlayıcı yanıtı kayboldu | Aynı iade referansıyla yeniden deneme; sağlayıcıda tek iade |
| Worker çöktü | Lease süresi dolar; başka worker işi alır. Eski sahip geç yazarsa fencing token eşleşmez, yazısı reddedilir |
| Zehirli (poison) outbox mesajı | Sınırlı deneme (üstel geri çekilme + jitter) sonrası dead-letter; operatör yeniden oynatabilir |
| Veritabanı erişilemez | `503`; check-in dahil hiçbir uç başarı göstermez; `/health/ready` `503` |
| Deadlock / serialization hatası | Transaction en fazla 3 kez, jitter'lı yeniden denenir; sonra `503` |
| Aynı webhook tekrar | İş etkisi tek; "tam bir kez teslim" iddiası yoktur, etki idempotent'tir |

Outbox teslimi **en az bir kez**dir. Olay sırası varsayılmaz; sıraya duyarlı tek yer sağlayıcı
olaylarıdır ve ödeme başına `sequence` ile korunur.

## 8. Loglama, metrik, gizlilik

- Yapısal JSON log; her istekte `correlationId`. Loglanmayanlar: parola, çerez, CSRF token, bilet token'ı,
  webhook secret'ı, e-posta, ad. Kullanıcı/sipariş kimlikleri (UUID) log alanı olabilir.
- Metrikler düşük kardinaliteli etiket kümesi kullanır (`mode`, `outcome`, `result`, `type`).
  Sipariş/bilet/müşteri kimliği metrik etiketi olamaz; izin verilen etiket adları testle sabitlenir.
- OpenAPI belgesi secret veya örnek kimlik bilgisi içermez.

## 9. Çalışma zamanı ve secret'lar

- Konteyner root olmayan kullanıcıyla çalışır.
- Secret'lar dosyadan okunur (`*File` yapılandırması, Compose `secrets`). Depoda yalnız yer tutuculu
  `.env.example` bulunur. `Demo:SeedEnabled` açık değilse varsayılan yönetici veya PSP secret'ı üretilmez;
  eksik secret ile `serve` kipi başlamaz.
- Failpoint / hata enjeksiyonu yalnız test host'unda DI ile kaydedilir; üretim derlemesinde HTTP ile
  erişilebilir bir hata ayıklama ucu yoktur.
- Üretim kipinde (`ASPNETCORE_ENVIRONMENT=Production`) ödeme sağlayıcısı adresi, API anahtarı dosyası ve en az bir
  webhook secret dosyası yoksa uygulama başlamaz (kapalı başarısızlık).
- Simüle PSP'nin `/sim-psp/*` uçları yalnız `Psp:Simulator:Enabled=true` iken eşlenir ve API anahtarı ister; demo PSP
  paneli yalnız platform yöneticisine açıktır.

## 10. Bilinçli olarak yapılmayanlar

CAPTCHA, bot koruması, cihaz parmak izi, 2FA, parola sıfırlama e-postası, WAF. `known-limitations.md`.
