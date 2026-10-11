# 08 — Uygulama Planı

Aşamalar bağımlı sırayla yürür. Her aşamanın kapısı geçmeden sonrakine başlanmaz; önceki aşamaların
testleri korunur. Gerçek durum `implementation-progress.md` içindedir — bu belge plandır, kanıt değildir.

| # | Aşama | Çıktı | Kabul kapısı (özet) |
|---|---|---|---|
| 1 | Temel, kimlik, organizasyon sahipliği | Solution, modül iskeleti, ilk migration, Identity, oturum iptali, CSRF, üyelik, ProblemDetails, health, OpenAPI | Login/logout; logout sonrası eski çerez reddi; yetki yükseltme yok; iki organizasyon arası erişim engeli; model/migration tutarlı; birim + gerçek DB entegrasyon |
| 2 | Docker, yerel stack, demo seed, CI | Dockerfile, Compose, HTTPS, secret dosyaları, `local-stack` betikleri, CI workflow | Boş stack tek akışla açılır; seed idempotent; restart veriyi korur; readiness DB kapalı/açık doğru; root değil; secret git/log'da yok |
| 3 | Mekan, etkinlik, seans, katalog | Organizatör CRUD, yayın guard'ları, değişmez plan sürümü + seans snapshot'ı, GA kapasite/fiyat, public filtre/ayrıntı | Yayınlanmamış içerik sızmaz; A, B'yi değiştiremez; geçersiz timezone/fiyat/kapasite reddi; satış sonrası plan değişikliği reddi; deterministik sayfalama |
| 4 | Envanter, hold, idempotency | Koltuk + GA claim, hep-ya-hiç, süre dolumu, kanonik hash, replay | ≥20 eşzamanlı istekte tek hold; GA'da oversell yok; çok satırlı hatada artık yok; replay / 409; yanıt kaybı replay; süre sınırı ve devralma; deadlock yeniden deneme; rol değişimi |
| 5 | Sipariş ve simüle ödeme | Checkout tekilliği, PSP simülatörü, imzalı webhook + inbox, başarı/ret/belirsiz, iade, reconciliation | Aynı hold farklı anahtar → tek sipariş/tahsilat; tekrarlı/sırasız callback tek etki; yanıt kaybı kurtarma; geç başarı telafisi; başarı-süre dolumu iki sıra; timeout ret değil; sahte HMAC/timestamp/event-id reddi |
| 6 | Bilet ve atomik check-in | Bilet üretimi, QR, sahibin biletleri, kapı ataması, check-in | Tekrarlı issuance tek bilet; iki kapı eşzamanlı → tek kabul; replay ve already-used; yanlış seans/organizasyon/iade/pencere reddi; DB hatasında sahte başarı yok; QR gerçek çözücüyle gidiş-dönüş |
| 7 | İptal, iade, worker kurtarma | Müşteri iptali, seans iptali toplu işi, iade yeniden deneme, envanter yeniden açma, lease, saklama | İptal-check-in iki sıra; iptal-geç ödeme; iade yanıt kaybı tek iade; lease dolumu kurtarma; zehirli olay; iptal seans satışa dönmez; toplu iş yeniden başlatmada çift etki yok |
| 8 | Müşteri, organizatör, kapı arayüzü | Gerçek API'ye bağlı SPA | typecheck/lint/build/bileşen testleri; E2E; rol/önbellek/logout; klavye koltuk alternatifi; mobil taşma yok; sahte başarı yok |
| 9 | Operasyon ve sağlamlaştırma | KPI tanımları, kurtarma ekranı, log/metrik, limit/timeout/kapanış | Reconciliation sayımı doğru; metriklerde PII/yüksek kardinalite yok; yetkisiz operasyon yok; polling sonrası otoriter veri; secret taraması; bağımlılık bulguları dürüst |
| 10 | Son doğrulama ve teslim | Boş kurulum + upgrade, tüm kapılar, Compose smoke, belgeler, rapor | `09` §Final kapılar geçti veya her gerçek engel açık yazıldı |

## Aşama sonu kaydı

Her aşama bitiminde `implementation-progress.md` içine: değişen dosya grupları, kararlar, çalıştırılan
komutlar + çıkış kodu, test sayıları (bulunan / geçen / başarısız / atlanan), kaynak parmak izi
(izlenen dosyaların içerik özeti), sonraki adım.

## Sıra dışı izinler

- Frontend temeli (yapı, tasarım belirteçleri, kimlik akışı) erken aşamalarda eklenebilir.
- `design/preview.html` Aşama 1 öncesinde yazılır.

## Durma koşulları

Çelişen temel ürün gereksinimi, güvenli çözülemeyen veri riski veya gerçek ortam engeli. Rutin
seçimler gerekçesiyle kaydedilir ve devam edilir.
