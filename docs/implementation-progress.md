# Uygulama İlerlemesi

Bu dosya gerçek durumun kaydıdır. "Geçti" yazan bir satır, yalnız yanında belirtilen kaynak parmak izi
için geçerlidir; sonraki oturumda kaynak değiştiyse testler yeniden çalıştırılmalıdır.

## Aşama 0 — Çalışma alanı ve tasarım (2026-10-10)

### Ortam
| Öğe | Değer |
|---|---|
| İşletim sistemi | Windows 11 Home 10.0.26300; kabuk Windows PowerShell 5.1 (pwsh yok), Git Bash mevcut |
| Başlangıç dizini | `D:\Etkinlik ve Bilet Satış Platformu` — boş, git deposu değil, AGENTS.md/CLAUDE.md yok |
| Proje dizini | `D:\Etkinlik ve Bilet Satış Platformu\event-ticketing-platform` (yeni oluşturuldu) |
| Git | 2.55.0; yerel `git init -b main` yapıldı; **commit yok, remote yok** |
| .NET | SDK 10.0.401, runtime 10.0.12 |
| Node / npm | 24.18.0 / 12.1.0 |
| Docker | Engine 29.8.2 (Docker Desktop, linux/x86_64), Compose v5.5.1; çalışıyor |
| WSL | Ubuntu-26.04 (durdurulmuş), docker-desktop (çalışıyor) |
| Tarayıcı | Chrome, Edge; Playwright önbelleğinde Chromium 1234 ve 1243 |
| Yerelde bulunan imajlar | `postgres:18.6`, `mcr.microsoft.com/dotnet/sdk:10.0.401`, `mcr.microsoft.com/dotnet/aspnet:10.0.12`, `testcontainers/ryuk:0.14.0` |

### Başka projelere ilişkin gözlemler (dokunulmadı)
- Compose project `lst` (`D:\Logistics & Shipment Tracking System\...`) çalışıyor ve `127.0.0.1:8443`
  portunu kullanıyor. Loca farklı port (8643) ve farklı project adı (`loca`) kullanır.

### Aynı ürün kontrolü
- Yerel: `D:\` altında (2 seviye) `ticket|bilet|etkinlik|event` adlı başka dizin yok.
- Uzak: `gh repo list TCoderr --visibility public` → 19 depo; etkinlik/bilet ürünü yok
  (lojistik, e-ticaret ve finans ayrı ürünlerdir, yeniden incelenmedi).
- Sonuç: bağımsız bilet ürünü olarak ilerlendi.

### Kararlar
- Ürün adı **Loca**; yazar/sahip **TCoderr**.
- Sürümler: `03-target-architecture.md` §4. TypeScript 6.0.3 (7.x, `typescript-eslint` peer aralığı
  dışında), ESLint 9.39.5 (`jsx-a11y` peer aralığı), Playwright 1.63.0 (önbellekteki Chromium ile eşleşir,
  indirme gerekmez).
- Skill keşfi: ürün arayüzü için uygun skill yok; `dataviz` yalnız özet kutucukları/kapasite çubuğu için kullanıldı.

### Üretilen belgeler
`docs/01..09`, `docs/adr/0001..0010`, bu dosya, `docs/next-action.md`.

## Aşama kayıtları

Kaynak parmak izi komutu (izlenen + izlenmeyen, yok sayılanlar hariç tüm dosyalar):
`git ls-files -co --exclude-standard -z | sort -z | xargs -0 sha256sum | sha256sum`

### Aşama 1 — Temel, kimlik ve organizasyon sahipliği — TAMAMLANDI (2026-10-10)

**Dosyalar:** `Loca.slnx`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `dotnet-tools.json`,
`src/Loca.Domain/*` (enum'lar, `Money`, durum makineleri, zaman politikaları, kanonik istek özeti, bilet token'ı,
webhook imzası), `src/Loca.Api/` (`Program.cs`, `LocaApp.cs`, `Cli.cs`, `Infrastructure/{Persistence,Http,Auth,Time,Tx,Config,RateLimiting}`,
`Modules/Identity`, `Modules/Operations/Audit.cs`), migration `InitialIdentity`, `tests/Loca.UnitTests`, `tests/Loca.IntegrationTests`.

**Kararlar**
- Test çalıştırıcı: .NET 10 SDK'da xUnit v3, VSTest hedefiyle çalışmıyor; `global.json` içinde
  `"test": { "runner": "Microsoft.Testing.Platform" }` ve `dotnet test --project <yol>` biçimi kullanılır.
- Yetkilendirme, CSRF doğrulamasından önce çalışır: iptal edilmiş çerezle gelen yazma isteği `401` alır (`403 csrf_invalid` değil).
- Çalışma zamanı rolünün yetkileri migration içinde değil, `migrate` kipinde her seferinde yeniden kurulur (`Migrator.SyncPermissionsAsync`).
- Bağlama hataları için `RouteHandlerOptions.ThrowOnBadRequest = true`: bilinmeyen alan çıplak 400 yerine `unknown_field` ProblemDetails döner.

**İlk koşuda bulunan ve düzeltilen kusurlar**
1. Kanonik JSON `2.0` ile `2`'yi farklı özetliyordu (birim testi yakaladı) → tam sayı değerli ondalıklar normalize edildi.
2. Bilinmeyen alanlı gövde `application/problem+json` yerine gövdesiz 400 dönüyordu (entegrasyon testi yakaladı) → yukarıdaki ayar.

**Komutlar ve sonuçlar**
| Komut | Çıkış | Sonuç |
|---|---|---|
| `dotnet restore` (lock dosyaları üretildi) | 0 | — |
| `dotnet build -c Release` | 0 | 0 uyarı, 0 hata |
| `dotnet test --project tests/Loca.UnitTests -c Release --no-build` | 0 | bulunan 81 / geçen 81 / başarısız 0 / atlanan 0 |
| `dotnet test --project tests/Loca.IntegrationTests -c Release --no-build` | 0 | bulunan 38 / geçen 38 / başarısız 0 / atlanan 0 (PostgreSQL 18.6, Testcontainers) |
| `dotnet ef migrations has-pending-model-changes --project src/Loca.Api --no-build --configuration Release` | 0 | değişiklik yok |

Kabul: login/logout ✔, logout sonrası eski çerez reddi ✔, yetki yükseltme yok ✔, iki organizasyon arası erişim engeli ✔,
model/migration tutarlı ✔, çalışma zamanı rolü DDL/audit değiştirme/TRUNCATE yapamıyor ✔, test hedefi koruması ✔.

### Aşama 2 — Docker, yerel stack, demo seed, CI temeli — TAMAMLANDI (2026-10-10)

**Dosyalar:** `Dockerfile`, `.dockerignore`, `.gitattributes`, `compose.yaml`, `deploy/postgres/init/01-roles.sh`,
`scripts/local-stack.ps1`, `scripts/local-stack.sh`, `scripts/smoke.mjs`, `.github/workflows/ci.yml`,
`src/Loca.Api/Modules/Operations/DemoSeed.cs`, `web/` (araç yapılandırması + geçici kabuk; gerçek ekranlar Aşama 8).

**Kararlar**
- Stack: `postgres:18.6` + tek uygulama imajı (`migrate` tek seferlik servis, `api` servis). Veritabanı portu yayınlanmaz.
- HTTPS: Kestrel doğrudan; sertifika ilk açılışta üretilip `keys` volume'unda saklanır; betik public sertifikayı
  `.local/<proje>/certs/loca-local.crt` olarak dışa aktarır ve smoke testi onu sabitler (ADR 0008).
- Durum dizini `.local/<proje>/` (gitignore): `secrets/*.txt`, `users/<e-posta>.password.txt`, `stack.manifest.json`.
- `reset` yıkıcıdır; açık onay + manifest `stackId` ile volume etiketinin eşleşmesini ister.
- Konteyner healthcheck'i imajda curl olmadığı için uygulamanın `healthcheck` kipini kullanır.
- CI eylemleri commit SHA ile sabitlendi (`gh api` ile 2026-10-10'da çözüldü). **Uzak CI çalıştırılmadı** (push yok).

**İlk koşuda bulunan ve düzeltilen kusurlar**
1. Windows PowerShell 5.1: çağıran stderr'i yönlendirdiğinde `docker compose` ilerleme çıktısı sonlandırıcı hataya dönüşüyordu → başarı yalnız çıkış koduyla değerlendiriliyor.
2. Npgsql, imajda olmayan `libgssapi_krb5` için her açılışta uyarı yazıyordu → `GssEncryptionMode=Disable`.

**Komutlar ve sonuçlar**
| Komut | Çıkış | Sonuç |
|---|---|---|
| `.\scripts\local-stack.ps1 init` (boş durumdan) | 0 | imaj derlendi, migration uygulandı, seed `created=11` |
| `.\scripts\local-stack.ps1 init` (ikinci kez) | 0 | seed `created=0, existing=11` |
| `node scripts/smoke.mjs --project loca` | 0 | 23/23: HTTPS, rol eşlemesi, logout replay, CSRF, seed idempotent, root değil (uid 1654), restart kalıcılığı, DB kesinti (ready 503, istek 503) ve kurtarma, loglarda secret/e-posta yok |
| `bash scripts/local-stack.sh init --project loca-shtest --https-port 8743 --no-build` → `status` → `reset --confirm-project loca-shtest` | 0 | Git Bash altında çalıştı; onaysız `reset` reddedildi; yalnız kendi kaynakları silindi. **Gerçek Linux'ta denenmedi.** |
| `npm run typecheck` / `lint` / `build` (`web/`) | 0 | geçti |

Kaynak parmak izi (Aşama 2 sonu, 92 dosya): `fd2faa8797bcf390…`

**Açık kalan Docker kaynakları:** `loca` geliştirme stack'i çalışır durumda (kasıtlı). İmajlar: `loca-api:local`, çekilen `node:24-alpine`.

### Aşama 3 — Etkinlik / mekan / seans / katalog — TAMAMLANDI (2026-10-10)

**Dosyalar:** `src/Loca.Api/Modules/Catalog/*` (varlıklar, organizatör uçları, public katalog, demo kataloğu),
`src/Loca.Api/Modules/Inventory/{Entities,InventoryGuards}.cs`, `Infrastructure/Config/PolicyOptions.cs`, migration `Catalog`,
`tests/Loca.IntegrationTests/Stage3/CatalogTests.cs`, `Migrations/MigrationUpgradeTests.cs`, `Infrastructure/CatalogSetup.cs`.

**Kararlar**
- Koltuk planı sürümü oluşturulduğu anda değişmezdir (düzenleme ucu yok; çalışma zamanı rolünün `seat_layouts`/`layout_seats`
  üzerinde UPDATE/DELETE yetkisi yok). Seans, plandan **kendi koltuk satırlarını kopyalar** (snapshot); yeni plan sürümü mevcut seansı etkilemez.
- Sürüm numarası mekan satırı kilidi altında atanır (8 eşzamanlı oluşturma → 1..8).
- Rezervasyonu olan seansın planı değiştirilemez (`layout_locked`); tutulmuş/satılmış koltuk içeren kademe yeniden fiyatlanamaz (`price_locked`).
- GA kapasitesi koşullu `UPDATE` ile değişir; `CHECK (held + sold <= capacity)` son savunmadır.
- Keşif: keyset `(starts_at, id)`; imleç opak base64url. Liste `public, max-age=30`, ayrıntı/koltuk planı `no-store`.
- Yayından kaldırma (takedown) durum makinesinden bağımsız bir bayraktır; public görünürlüğü keser, temizlenebilir.
- Seans saatleri mekanın yerel saatiyle (`yyyy-MM-ddTHH:mm`) alınır; DST boşluğu/çakışması reddedilir.

**İlk koşuda bulunan ve düzeltilen kusurlar:** Ürün kodunda kusur çıkmadı. Test verisinde iki hata düzeltildi
(anonim nesnenin camelCase'e çevrilmesi kademe kodunu `a` yapıyordu; 1 karakterlik başlık doğrulamaya takılıyordu).

**Sonuç:** entegrasyon 73/73 (Aşama 1 testleri dahil), atlanan 0. Boş kurulum, çift migrate ve
`InitialIdentity` → son sürüm sentetik verili yükseltme testleri dahil.

### Aşama 4 — Envanter ve kalıcı hold / idempotency — TAMAMLANDI (2026-10-10)

**Dosyalar:** `Infrastructure/Idempotency/Idempotency.cs`, `Infrastructure/Workers/Workers.cs`,
`Modules/Inventory/{Reservations,InventoryService,HoldEndpoints,HoldExpiry}.cs`, migration `InventoryHolds`,
`tests/.../Stage4/*`, `Infrastructure/{TestFailpoints,InventoryTestData}.cs`.

**Kararlar**
- Kilit sırası: kullanıcının kendi aktif rezervasyonu → seans (`FOR SHARE`) → koltuklar/kategoriler (kimlik sırasıyla).
  Kullanıcının süresi dolmuş kendi hold'u, istenen koltuklarla **tek sıralı geçişte** kilitlenip kapatılır.
- GA claim'i seansın tüm kategorilerini sıralı kilitler; kapasite yetmezse süresi dolmuş hold'ları `FOR UPDATE SKIP LOCKED` ile toplar.
- Idempotency kaydı iş etkisiyle aynı transaction'dadır (ADR 0003); eşzamanlı aynı anahtar unique indeks üzerinde bekler.
- Süre dolumu worker'ı `SKIP LOCKED` ile çalışır; doğruluk worker'a bağlı değildir (claim ve durum okuma süreyi kendisi değerlendirir).
- Test host'unda parola özeti yineleme sayısı düşürülür (yalnız test; çok kullanıcılı yarışlar için).

**İlk koşuda bulunan ve düzeltilen kusurlar**
1. Test altyapısı: her testin kendi havuzu boşta bağlantı biriktirip PostgreSQL `max_connections` sınırını doldurdu
   (`53300`; uygulama bunu doğru biçimde `503` olarak raporladı) → test konteynerinde `max_connections=400`, test bitiminde havuz temizliği.
2. Deadlock testi: yeniden deneme sayacı istek tamamlanmadan okunuyordu → doğrulama isteğin bitişine alındı. (Uygulama davranışı doğruydu:
   log'da `40P01` → "attempt 1 failed … retrying" → `201`.)
3. Test beklentileri: saat 24 saat ilerletilince kullanıcı oturumu (12 saat) doğal olarak doluyor → testler yeniden giriş yapıyor.

**Komutlar ve sonuçlar**
| Komut | Çıkış | Sonuç |
|---|---|---|
| `dotnet build -c Release` | 0 | 0 uyarı, 0 hata |
| `dotnet test --project tests/Loca.UnitTests -c Release --no-build` | 0 | 81 / 81 / 0 / 0 |
| `dotnet test --project tests/Loca.IntegrationTests -c Release --no-build` | 0 | bulunan 110 / geçen 110 / başarısız 0 / atlanan 0 |
| `… --filter-namespace Loca.IntegrationTests.Stage4` (yarış testleri, 2 ek koşu) | 0, 0 | 37/37, 37/37 |
| `dotnet ef migrations has-pending-model-changes …` | 0 | değişiklik yok |

Kabul: aynı koltuğa 25 eşzamanlı istek → tek hold ✔; GA kapasite altı/üstü yarış, oversell yok ✔; çok satırlı hata artık bırakmaz ✔;
aynı anahtar replay (bayt eşit) / farklı gövde 409 ✔; yanıt kaybı replay ✔; süre sınırı (1 µs öncesi aktif, tam anında dolmuş) ve devralma ✔;
deadlock kurbanı yeniden deneme + bütçe ✔; devre dışı/çıkış yapmış kullanıcı replay alamaz ✔.

Kaynak parmak izi (Aşama 4 sonu): `9ea5120cd00c3aa7…`

### Aşama 5 — Sipariş ve simüle ödeme — TAMAMLANDI (2026-10-10)

**Dosyalar:** `Infrastructure/Outbox/Outbox.cs` (outbox + inbox), `Modules/Orders/{Entities,PaymentProvider,PaymentService,OrderEndpoints,WebhookEndpoint}.cs`,
`PspSimulator/PspSimulator.cs` (+ kendi migration seti), migration `OrdersPayments`, `tests/.../Stage5/*`, `Infrastructure/PaymentTestSupport.cs`.

**Kararlar**
- Checkout yalnız veritabanı işi yapar ve `202` döner; sağlayıcı çağrısı `payment.initiate` outbox handler'ındadır (ADR 0003, 0006).
- Senkron yanıt, webhook ve reconciliation **tek** durum uygulama yolundan geçer (`ApplyChargeStateInTransactionAsync`): kök kilit →
  tutar/para birimi kontrolü → sıra numarası kontrolü → durum makinesi. Tekrarlı/sırasız olayın etkisi bu yüzden tektir.
- Sağlayıcı modeli anında tahsilat; telafi her zaman iade, void yok (ADR 0007).
- Geç başarı: sipariş `Paid` olmaz, envanter yeniden kapılmaz, bilet üretilmez; `refunds.payment_id` unique olduğundan tek telafi kaydı oluşur.
- Operatör eylemleri `planned / attempted / succeeded / failed` olarak ayrı kaydedilir (`orders.reconciliation_runs`).
- Simülatör aynı süreçte, ayrı şema (`pspsim`) ve ayrı DbContext/migration geçmişiyle; uygulama onu yalnız HTTP ile çağırır.
  Testlerde araya giren `FaultInjectingHandler` "sağlayıcı işledi, yanıt kayboldu" durumunu gerçek HTTP hattında üretir.

**İlk koşuda bulunan ve düzeltilen kusurlar**
1. `ops.inbox_messages.outcome` 24 karakterdi; `ignored_unknown_reference` (25) bilinmeyen referanslı webhook'ta `500` üretiyordu
   (test yakaladı) → sütun 40 karaktere çıkarıldı.
2. Geç başarıda seans iptaliyse sipariş `Cancelled` yerine `Expired` oluyordu (kod incelemesinde bulundu) → durum, rezervasyon kapatılmadan önce atanıyor.

### Aşama 6 — Bilet ve atomik check-in — TAMAMLANDI (2026-10-10)

**Dosyalar:** `Modules/Tickets/{Tickets,TicketEndpoints}.cs`, migration `TicketsCheckIn`, `tests/.../Stage6/TicketAndCheckInTests.cs`,
`web/src/lib/qr.ts` + `qr.test.ts`.

**Kararlar**
- Bilet kimliği `UUIDv5(sipariş, satır, sıra)`; üretim `ON CONFLICT (id) DO NOTHING`. Token 32 bayt CSPRNG; veritabanında `SHA-256` özeti
  (arama) ve Data Protection ile şifreli hali (sahibine yeniden gösterim) bulunur (ADR 0009).
- Check-in, iptal ile aynı kilit kökünü (rezervasyon) kullanır ve koşullu `UPDATE … WHERE status = 'Valid'` ile geçiş yapar.
- Kapı yetkisi = seans ataması **ve** güncel organizasyon üyeliği; idempotent tekrar oynatmadan önce yeniden doğrulanır.
- Reddedilen taramalar geri alınan transaction'ın dışında `tickets.scan_log`'a yazılır (token içermez; tablo yalnız-ekleme).
- QR istemcide `qrcode-generator` ile üretilir; gidiş-dönüş testi gerçek çözücü `jsQR` ile yapılır.

**İlk koşuda bulunan ve düzeltilen kusurlar:** Ürün kodunda kusur çıkmadı (bilet CHECK kısıtı yazım aşamasında sadeleştirildi).

**Komutlar ve sonuçlar (Aşama 5 + 6)**
| Komut | Çıkış | Sonuç |
|---|---|---|
| `dotnet build -c Release` | 0 | 0 uyarı, 0 hata |
| `dotnet test --project tests/Loca.IntegrationTests -c Release --no-build` | 0 | bulunan 177 / geçen 177 / başarısız 0 / atlanan 0 |
| `dotnet ef migrations has-pending-model-changes --context AppDbContext …` | 0 | değişiklik yok |
| `npm run test` (`web/`) | 0 | 7/7 (QR gidiş-dönüş, jsQR) |

Kabul (5): aynı hold farklı anahtar → tek sipariş/tahsilat (12 eşzamanlı dahil) ✔; tekrarlı/sırasız callback tek etki ✔; yanıt kaybı kurtarma (tek tahsilat) ✔;
geç başarı telafisi (tek iade) ✔; başarı↔süre dolumu iki sıra, gerçek kilitlerle ✔; timeout ret değil ✔; sahte HMAC / zaman damgası / event-id çakışması reddi ✔.
Kabul (6): tekrarlı issuance tek bilet ✔; iki kapı eşzamanlı (24 istek) tek kabul ✔; aynı anahtar replay / farklı anahtar already-used ✔;
yanlış seans / yanlış organizatör / iptal bilet / pencere dışı reddi ✔; commit öncesi hata sahte başarı üretmez ✔; QR gerçek çözücüyle ✔.

Kaynak parmak izi (Aşama 6 sonu): `fbfcb11bbfbd6241…`

### Aşama 7 — İptal, iade ve worker kurtarma — TAMAMLANDI (2026-10-10)

**Dosyalar:** `Modules/Orders/Cancellation.cs` (müşteri iptali, seans iptali, toplu iş), `Modules/Operations/Retention.cs`,
migration `Cancellation`, `tests/.../Stage7/CancellationTests.cs`.

**Kararlar**
- Müşteri iptali tek transaction'dır: sipariş `Cancelled`, biletler `Cancelled`, envanter yeniden açılır, iade `Pending` + outbox.
  Bilet, iadenin sonucunu beklemeden geçersizdir; iade başarısız olsa da yeniden geçerli olmaz.
- Seans iptali iki adımdır: (1) seans satırı `FOR UPDATE` ile `Cancelled` + iş kaydı (süren hold istekleri bitene kadar bekler);
  (2) iş, rezervasyonları kimlik sırasıyla, her birini kendi transaction'ında işler. İmleç ve sayaçlar o transaction'da ve yalnız
  lease token hâlâ geçerliyse yazılır; lease'i kaybeden sahibin transaction'ı geri alınır. Seans iptalinde envanter **açılmaz**.
- Saklama işi yalnız teknik tabloları siler (idempotency, inbox, işlenmiş outbox, kapanmış oturumlar); dead-letter mesaj silinmez.

**İlk koşuda bulunan ve düzeltilen kusurlar**
1. İptal edilmiş seansta aktif hold'u olan kullanıcı yeni hold isteğinde `session_cancelled` yerine `active_hold_exists` alıyordu
   (test yakaladı) → seans iptali kontrolü öne alındı.
2. Saklama işlemcisinin "son çalışma" alanı her turda sıfırlanıyordu (işlemci kapsam başına yeniden oluşturulur; kod incelemesinde bulundu)
   → süreç ömürlü zamanlayıcıya taşındı.

Kabul: iptal↔check-in iki sıra (gerçek kilitlerle) + eşzamanlı invariant ✔; iptal-geç ödeme (telafi iadesi, bilet yok) ✔;
iade yanıt kaybı → tek iade ✔; lease dolumu sonrası devralma ve fencing (outbox, seans iptali, reconciliation) ✔; zehirli mesaj → dead-letter ✔;
iptal seans satışa/yayına dönmez ✔; toplu iş çöküp yeniden başladığında çift etki yok ✔; saklama iş verisine dokunmaz ✔.

### Aşama 9 — Operasyon ve sağlamlaştırma (arka uç kısmı) — TAMAMLANDI (2026-10-10)

Arayüz (Aşama 8) bu uçlara ihtiyaç duyduğu için arka uç kısmı önce tamamlandı; operasyon **ekranları** Aşama 8 kapsamındadır.

**Dosyalar:** `Modules/Operations/{OperationsEndpoints,DemoCrowd}.cs`, `Infrastructure/Metrics/AppMetrics.cs`, erişim logu
(`Infrastructure/Http/Middleware.cs`), `Modules/Tickets/GateDemoSeed.cs`, `tests/.../Stage9/*`, `scripts/{smoke-flow,scan-repo}.mjs`,
`docs/05a-api-operations.md` (OpenAPI'den üretildi), `docs/04a-schema-reference.md` (veritabanından üretildi).

**Kararlar**
- KPI tanımları tek sorgu sınıfında (`OperationsQueries`): brüt = başarılı tahsilatlar (geç başarı dahil); iade edilen = yalnız başarılı
  iadeler; bekleyen iade = `Pending` + `Failed`; net = brüt − iade edilen; `held` yalnız süresi dolmamış hold'lar.
- Operasyon kuyruğunda "başarılı" sayımı yalnız `Succeeded` sonuçlu denemelerdir; başarısız deneme ve "hâlâ bekliyor" ayrı sayılır.
- Metrik etiketleri kodda izin listesiyle sınırlıdır (`mode, outcome, result, type`); kimlik benzeri değerler reddedilir.
- Erişim logu yol yerine rota şablonunu yazar; gövde, sorgu dizesi, çerez, token loglanmaz.
- Üretim kipinde PSP yapılandırması eksikse uygulama başlamaz.
- Ret/hata olarak kapanmış ödemeye sonradan başarı bildirilirse durum geri dönmez; `review_reason` ile operatöre düşer.
- Gerçek zamanlılık için SignalR eklenmedi (ADR 0010); abonelik yüzeyi yoktur.

**Komutlar ve sonuçlar (Aşama 7 + 9 arka uç)**
| Komut | Çıkış | Sonuç |
|---|---|---|
| `dotnet build -c Release` | 0 | 0 uyarı, 0 hata |
| `dotnet test --project tests/Loca.UnitTests -c Release --no-build` | 0 | bulunan 92 / geçen 92 / başarısız 0 / atlanan 0 |
| `dotnet test --project tests/Loca.IntegrationTests -c Release --no-build` | 0 | bulunan 210 / geçen 210 / 0 / 0 (sonradan eklenen 3 test ayrı koşuda geçti; tam sayı `final-verification.md`'de) |
| `.\scripts\local-stack.ps1 init` + `node scripts/smoke.mjs --project loca` | 0 | 47/47 (iş akışı + restart + DB kesintisi + log taraması) |
| `node web/e2e/run-e2e.mjs` (yalnız orkestrasyon doğrulaması) | 0 | izole stack açıldı, 2/2, yalnız kendi kaynakları silindi |
| `node scripts/scan-repo.mjs` | 0 | bulgu yok |
| `dotnet list package --vulnerable --include-transitive` · `npm audit` | 0 | bulgu yok |

### Aşama 8 — Müşteri, organizatör ve kapı arayüzü — TAMAMLANDI (2026-10-10)

**Dosyalar:** `web/src/` (`lib/` API istemcisi + biçimler + tipler; `auth/`; `components/` SeatPicker, QuantityPicker, Countdown, QrCode,
OrderStatusPanel, GateResultPanel, CameraScanner, CapacityBar; `pages/` müşteri, organizatör, kapı, yönetim; `styles/app.css`),
`web/e2e/*.spec.ts` + `e2e/support/`, `web/e2e/run-e2e.mjs`, `web/playwright.config.ts`, `artifacts/verification/screenshots/` (36 görüntü).
Arayüz bir alt ajana brif verilerek yazdırıldı; sonuç ana oturumda kendi koşularımla ve ekran görüntülerine bakılarak doğrulandı.

**Kararlar**
- Token/secret tarayıcı deposuna yazılmaz (ESLint kuralı + E2E testi); CSRF token'ı yalnız bellekte. Sorgu anahtarları kullanıcıya göre kapsamlı;
  logout ve `401`'de özel önbellek temizlenir.
- Idempotency-Key kullanıcı eylemi başına bir kez üretilir; sonuç bilinmiyorsa aynı anahtarla yeniden denenir (bileşen testi + kapı E2E).
- Geri sayım `expiresAt − serverTime` farkından ve monoton saatle kurulur; süre bitince durum sunucudan yeniden okunur.
- Koltuk planı (SVG) ile eşdeğer koltuk listesi aynı durumu paylaşır; liste klavye/ekran okuyucu için birincil yoldur.
- QR istemcide üretilir; E2E'de ekran görüntüsünden `jsQR` ile çözülüp kapıda kullanılır.
- Gerçek zamanlılık: yoklama + otoriter yeniden okuma (SignalR yok, ADR 0010).

**Prototipten gerekçeli sapmalar:** prototip gezgini/durum değiştirici yok (durum yalnız API'den); gezinme role göre; bilette gerçek QR (kod metni
düğmeyle açılır); kapıya "ÇEVRİMDIŞI" ve "KONTROL EDİLEMEDİ" panelleri eklendi; "Organizatör başvurusu" ekranı eklendi; kapasite çubuğunda
değeri 0 olan dilim çizilmez; "Senin saatinle" satırı yalnız duvar saati gerçekten farklıysa gösterilir.

**İlk koşuda bulunan ve düzeltilen kusurlar:** `final-verification.md` §3, satır 12–15 (N+1 isteği, eksik Demo etiketi, Dockerfile önbellek tuzağı,
mobil satır kırılması / kamera metinleri / ekran görüntüsünde çözülebilir QR) ve bir E2E testindeki sayfa geçişi beklemesi.

Kabul: typecheck / lint / build / bileşen (123/123) ✔; E2E 30/30 ✔; roller, önbellek, logout ✔; klavyeyle koltuk seçimi ✔; mobil taşma yok ✔;
sahte başarı yok (202 "ödendi" gösterilmez; belirsiz sonuç ayrı durumdur) ✔; demo simülatörü seans bazlı ve opt-in ✔.

### Aşama 9 — Operasyon ve sağlamlaştırma — TAMAMLANDI (2026-10-10)

Arka uç kısmı yukarıda kaydedildi. Arayüz kısmı: organizatör operasyon kuyruğu ve yönetim ekranları (tüm kuyruk, dead-letter, demo PSP paneli)
gerçek API'ye bağlı; E2E'de başarısız iade yeniden denemesinin "başarısız", mutabakatın "başarılı" olarak **ayrı** gösterildiği doğrulandı.
Kabul: reconciliation başarı sayımı doğru ✔; metriklerde PII/yüksek kardinalite yok ✔; yetkisiz operasyon erişimi yok, abonelik yüzeyi yok ✔;
yoklama sonrası otoriter veri ✔; kaynak/test kapsamında secret taraması temiz ✔; bağımlılık bulguları dürüstçe raporlandı (bulgu yok) ✔.

### Aşama 10 — Son doğrulama ve teslim — TAMAMLANDI (2026-10-10)

Tüm kapılar aynı kaynak durumu üzerinde yeniden çalıştırıldı; sonuçlar, komutlar, çıkış kodları, sayılar ve log yolları:
[`final-verification.md`](final-verification.md). Özet: birim 92/92, entegrasyon 213/213, bileşen 123/123, E2E 30/30, smoke 48/48, atlanan 0.

Kaynak parmak izi (son): `e56d38db524e440af7212ae1ea23759a01555c0f89237f69880ecd83c8761f13` (hesaplama komutu `final-verification.md` başında).

**Gerçek engeller / çalıştırılmayanlar:** uzak CI (push yok), gerçek Linux ana makine, çok örnekli çalışma, yük testi, gerçek kamera, elle ekran
okuyucu, Chromium dışı tarayıcılar — hepsi `final-verification.md` §5 ve `known-limitations.md` içinde.

**Teslim belgeleri:** `README.md`, `docs/01..09` (+ `04a`, `05a`), `docs/adr/0001..0010`, `docs/final-verification.md`,
`docs/known-limitations.md`, `docs/portfolio-summary.md`, `docs/versions-and-licenses.md`, `.env.example`, `.github/workflows/ci.yml`.

**Yapılmayanlar (bilinçli):** commit, push, PR, remote depo, deployment yok. Global kurulum, makine/WSL/Docker ayarı değişikliği yok.

