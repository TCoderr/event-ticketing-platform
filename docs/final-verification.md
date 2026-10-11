# Son Doğrulama Raporu

**Tarih (UTC):** 2026-10-10, son koşu 16:26–16:35
**Ortam:** Windows 11 Home 10.0.26300 · Docker Desktop (Engine 29.8.2, Compose v5.5.1, linux/x86_64) · .NET SDK 10.0.401 · Node 24.18.0
**Doğrulanan kaynak:** aşağıdaki tüm "son koşu" sonuçları aynı kaynak durumu üzerinde alınmıştır.

| Parmak izi | Değer |
|---|---|
| Kaynak (src, tests, web, scripts, deploy, .github, design, kök yapılandırma — 177 dosya) | `e56d38db524e440af7212ae1ea23759a01555c0f89237f69880ecd83c8761f13` |
| Komut | `git ls-files -co --exclude-standard -z -- src tests web scripts deploy .github Dockerfile compose.yaml global.json Directory.Build.props Directory.Packages.props Loca.slnx dotnet-tools.json .dockerignore .gitattributes .gitignore .env.example design \| sort -z \| xargs -0 sha256sum \| sha256sum` |

Git durumu: yerel depo (`main`), **commit yok, remote yok, push yok.**
Log dosyaları: `artifacts/verification/logs/` (secret içermez; depo taraması bu dosyaları da kapsar).

## 1. Kapı sonuçları (son koşu)

| # | Kapı | Komut | Çıkış | Sonuç | Log |
|---|---|---|---|---|---|
| 1 | Kilitli restore | `dotnet restore --locked-mode` | 0 | — | `01-restore.log` |
| 1 | Release build | `dotnet build -c Release --no-restore` | 0 | 0 uyarı, 0 hata | `02-build.log` |
| 2 | Birim testleri | `dotnet test --project tests/Loca.UnitTests -c Release --no-build` | 0 | bulunan 92 / geçen 92 / başarısız 0 / **atlanan 0** | `03-unit.log` |
| 2 | Entegrasyon (gerçek PostgreSQL 18.6, Testcontainers) | `dotnet test --project tests/Loca.IntegrationTests -c Release --no-build` | 0 | bulunan 213 / geçen 213 / başarısız 0 / **atlanan 0** (49 sn) | `04-integration.log` |
| 3 | Web kilitli kurulum | `npm ci --no-audit --no-fund` | 0 | — | `05-npm-ci.log` |
| 3 | Typecheck / lint | `npm run typecheck` · `npm run lint` | 0 · 0 | 0 hata, 0 uyarı | `06`, `07` |
| 3 | Bileşen testleri | `npm run test` | 0 | 7 dosya; bulunan 123 / geçen 123 / 0 / **0** | `08-component.log` |
| 3 | Üretim build'i | `npm run build` | 0 | — | `09-web-build.log` |
| 4, 7 | Gerçek tarayıcı E2E (izole stack, imaj yeniden derlendi) | `npm run e2e -- --build` | 0 | bulunan 30 / geçen 30 / başarısız 0 / **atlanan 0** / flaky 0 (2,6 dk) | `10-e2e.log` |
| 5 | Model ↔ migration | `dotnet ef migrations has-pending-model-changes --context AppDbContext` · `--context PspSimDbContext` | 0 · 0 | değişiklik yok | — |
| 5 | Migration betiği uygulama | `dotnet ef migrations script --idempotent` (iki bağlam) → boş, tek kullanımlık `postgres:18.6` konteynerine `psql` ile | 0 | 33 tablo, 6 + 1 geçmiş satırı; ikinci uygulama etkisiz; `pg_dump --schema-only` özeti EF ile kurulan veritabanıyla **birebir aynı** | bu belge §4 |
| 5 | Boş kurulum, çift migrate, önceki aşama verisinden yükseltme | `MigrationUpgradeTests` (entegrasyon içinde) | 0 | 4/4 | `04-integration.log` |
| 6 | Compose HTTPS smoke | `.\scripts\local-stack.ps1 init` → `node scripts/smoke.mjs --project loca` | 0 · 0 | 48/48 | `11`, `12` |
| 8 | Depo secret / sahiplik taraması | `node scripts/scan-repo.mjs` | 0 | 232 dosya, 11 yerel secret değeriyle karşılaştırma, bulgu yok | `13-repo-scan.log` |
| 8 | Bağımlılık taraması | `dotnet list package --vulnerable --include-transitive` · `npm audit` | 0 · 0 | bulgu yok (2026-10-10 danışma verisi) | — |

Toplam otomatik test: **458** (92 birim + 213 entegrasyon + 123 bileşen + 30 E2E), ayrıca 48 smoke denetimi. Atlanan test yoktur.

## 2. Kapı ayrıntıları

### Kapı 2 — Kritik invariant'lar (gerçek PostgreSQL)
Invariant → test sınıfı eşlemesi: `09-test-and-verification-plan.md` §4. Öne çıkanlar:
- 25 eşzamanlı istek → tek hold; GA kapasite altı/üstü yarış; çok satırlı hep-ya-hiç.
- Süre sınırı (1 µs öncesi aktif, tam anında dolmuş), worker çalışmadan devralma.
- Replay bayt eşitliği, commit sonrası yanıt kaybı, 15 eşzamanlı aynı anahtar, kilit zaman aşımı → `request_in_progress`.
- 12 eşzamanlı farklı anahtarlı checkout → tek sipariş/tahsilat; DB unique kısıtları doğrudan SQL ile sınandı.
- Ödeme↔süre dolumu ve iptal↔check-in: her iki sıra, failpoint + `pg_stat_activity` ile gerçek kilit beklemesi gözlenerek.
- Webhook: sahte imza, yeniden serileştirilmiş gövde, eski/ileri zaman damgası, aynı kimlik farklı içerik, secret rotasyonu.
- Lease dolumu + fencing: outbox, reconciliation, seans iptal işi.
- Çalışma zamanı rolü: DDL, audit/scan_log/snapshot değiştirme, TRUNCATE reddi (`42501`).

### Kapı 4 — E2E senaryoları (Chromium, gerçek API + veritabanı)
| Gereken | Test |
|---|---|
| Müşteri keşif → koltuk/GA hold → ödeme → bilet (QR ekran görüntüsü `jsQR` ile çözülür) | `customer.spec.ts` 1, 2 |
| Organizatör yalnız kendi seansı ve satışları | `organizer.spec.ts` 3 |
| Kapı: tarama → kabul → ikinci tarama zaten kullanıldı | `gate.spec.ts` 4 |
| Hold süre dolumu, tükendi/çakışma, ret, belirsiz → kurtarılan ödeme | `payments.spec.ts` 5–8 (+ geç başarı) |
| İptal → iade → bilet geçersiz | `cancellation.spec.ts` 9 |
| Seans iptali, yanlış organizatör, logout replay, CSRF | `cancellation.spec.ts` 10, `organizer.spec.ts` 3, `security.spec.ts` 11–12 |

### Kapı 6 — Compose HTTPS smoke (48 denetim)
Sabitlenmiş sertifikayla HTTPS; demo kullanıcı/rol eşlemesi; logout replay; CSRF; organizatörün seans kurması; hold + **aynı anahtarla bayt eşit replay**;
çakışma; ödeme; bilet; check-in (kabul / replay / zaten kullanıldı); simülatör uçlarının dış porttan erişilememesi; organizatör özeti; yabancı organizatör 404;
belirsiz ödemenin reconciliation worker'ı ile kurtarılması; ret; seed idempotency; root olmayan konteyner (uid 1654);
**restart sonrası veri ve oturum kalıcılığı**; **veritabanı durdurulunca `ready` 503 + istek 503, açılınca kurtarma**; konteyner loglarında secret/parola/çerez/token/e-posta yok.

### Kapı 7 — Arayüz
- Otomatik erişilebilirlik (axe, WCAG A/AA): 19 ekran durumu, ihlal 0, istisna yok.
- 360 / 768 / 1440: müşteri (keşif, koltuk seçimi, genel giriş, ödeme, sipariş, biletlerim, bilet), organizatör (etkinlikler, özet), kapı (seanslar, tarama, sonuç) ekranlarında yatay taşma yok; 36 ekran görüntüsü `artifacts/verification/screenshots/`.
- Klavye: koltuk listesinden yalnız klavyeyle seçim + tutma (E2E 13).
- **Elle inceleme:** ekran görüntülerine tek tek bakıldı (arayüzü yazan alt ajan tümüne; son gözden geçirmede keşif 1440, koltuk seçimi 1440/360, ödeme 768, bilet 360, organizatör özeti 1440, kapı sonucu 360). Bulunup düzeltilen kusurlar §3'te.
- **Elle ekran okuyucu testi yapılmadı.**

### Kapı 8 — Güvenlik sınırları
- Sahiplik: `Authors`/`Company` = TCoderr, `package.json` author, README, imaj etiketi — tarama ile doğrulandı. AI yazar/ortak yazar/"generated by" imzası yok.
- PII sınırları: kapı ve organizatör yanıtlarında müşteri verisi yok, public yanıtlarda token yok, loglarda PII/secret yok (`TicketIssuanceTests`, `CheckInTests`, `SalesSummaryTests`, `ObservabilityTests`).
- Çalışma zamanı: API konteyneri uid 1654; PostgreSQL süreci `postgres` kullanıcısı.

### Kapı 9 — Temizlik ve Docker durumu
- Bu görevin açtığı tüm geçici kaynaklar silindi: Testcontainers konteynerleri (Ryuk), E2E stack'leri (`loca-e2e-*`), betik denemesi (`loca-shtest`), migration betiği konteyneri (`loca-migscript-*`). Koşu sonunda `loca-e2e`, `migscript` adlı konteyner/volume yok.
- **Açık bırakılan (kasıtlı):** geliştirme stack'i `loca` (`loca-api-1`, `loca-postgres-1`; volume `loca_pgdata`, `loca_keys`; ağ `loca_default`) — `https://localhost:8643`.
  Geliştirme veritabanında smoke testinin ve arayüz incelemesinin oluşturduğu sentetik seanslar da durur. Geliştirme volume'u silinmedi.
- Dokunulmayan: `lst` projesi (çalışmaya devam ediyor).
- İmajlar: `loca-api:local` (357 MB, bu görevde üretildi). Önceden mevcut olanlar (`postgres:18.6`, .NET SDK/ASP.NET, `testcontainers/ryuk`) duruyor.
  `node:24-alpine` yalnız derleme önbelleğinde kullanılır. Docker Desktop açık bırakıldı (başka proje çalışıyor).

## 3. İlk koşuda başarısız olan → düzeltme → son durum

Doğrulamayı gevşeterek yeşile çevrilen test yoktur. Ürün kodunda bulunan kusurlar:

| # | Nerede yakalandı | Kusur | Düzeltme |
|---|---|---|---|
| 1 | Birim testi | Kanonik JSON `2.0` ile `2`'yi farklı özetliyordu | Tam sayı değerli ondalıklar normalize edildi |
| 2 | Entegrasyon | Bilinmeyen alanlı gövde ProblemDetails yerine gövdesiz `400` dönüyordu | `RouteHandlerOptions.ThrowOnBadRequest` |
| 3 | Betik ilk koşusu | PowerShell 5.1'de yönlendirilmiş stderr betiği durduruyordu | Başarı yalnız çıkış koduyla değerlendiriliyor |
| 4 | Stack logu | Npgsql her açılışta eksik `libgssapi` uyarısı yazıyordu | `GssEncryptionMode=Disable` |
| 5 | Entegrasyon | `inbox.outcome` sütunu 24 karakter; bilinmeyen referanslı webhook `500` veriyordu | 40 karakter |
| 6 | Kod incelemesi | Geç başarıda seans iptaliyse sipariş `Cancelled` yerine `Expired` oluyordu | Durum, rezervasyon kapatılmadan önce atanıyor |
| 7 | Entegrasyon | İptal edilmiş seansta aktif hold'u olan kullanıcı yanlış hata kodu alıyordu | Seans iptali kontrolü öne alındı |
| 8 | Kod incelemesi | Saklama işinin zamanlayıcısı her turda sıfırlanıyordu | Süreç ömürlü zamanlayıcı |
| 9 | Kod incelemesi | Terminal ödemeye sonradan başarı bildirimi sessizce yok sayılıyordu | `review_reason` + denetim kaydı + operasyon kuyruğu |
| 10 | Kod incelemesi | Simülatör uçları yayınlanan HTTPS portundan da erişilebilirdi (API anahtarıyla) | İç dinleyici dışında `404` |
| 11 | Kod incelemesi | Kademe fiyatı güncellemesi koltukları sırasız kilitliyordu (teorik deadlock) | Kimlik sırasıyla ön kilitleme |
| 12 | Arayüz geliştirme | Keşif listesi satış başlangıcını taşımadığı için satır başına ek istek (N+1) | `salesStartAt` listeye eklendi |
| 13 | Arayüz geliştirme | Sipariş/bilet yanıtlarında `isDemo` yoktu; "Demo" etiketi gösterilemiyordu | Alan eklendi, etiket gösteriliyor |
| 14 | E2E `--build` | `npm ci` isteğe bağlı yerel bağlayıcıyı sessizce atlayınca bozuk katman önbelleğe alındı | Aynı `RUN` içinde `require('rolldown')` denetimi |
| 15 | Arayüz incelemesi | 360'ta satır etiketi bölünüyordu; kamera hata metinleri belirsizdi; kayıtlı bilet görüntüsünde çözülebilir QR vardı | Düzeltildi / maskelendi |

Test veya test altyapısı kaynaklı (ürün davranışı doğruydu): test konteynerinde `max_connections` tükenmesi (havuz temizliği + sınır artırıldı);
deadlock testinde sayaç erken okunuyordu; saat 24 saat ilerletilince oturumun doğal olarak dolması; camelCase'e çevrilen kademe anahtarı; bir E2E testinin
sayfa geçişini beklemeden arama yapması.

## 4. Migration betiği doğrulaması (ayrıntı)

```
dotnet ef migrations script --idempotent --context AppDbContext  …  → app.sql   (48 277 bayt)
dotnet ef migrations script --idempotent --context PspSimDbContext … → pspsim.sql (4 163 bayt)
docker run -d --rm --name loca-migscript-<rastgele> postgres:18.6        # tek kullanımlık
psql -U loca_owner -d loca -v ON_ERROR_STOP=1 -f app.sql ; -f pspsim.sql   # boş veritabanına: başarılı
psql … -f app.sql ; -f pspsim.sql                                          # ikinci kez: etkisiz, hatasız
tablo sayısı 33 · ops.__ef_migrations_history 6 · pspsim.__ef_migrations_history 1
pg_dump --schema-only özeti: betikle kurulan = EF ile kurulan (4ba67cba91052e88)
docker stop loca-migscript-<rastgele>                                      # konteyner silindi
```

## 5. Çalıştırılmayanlar ve doğrulama sınırları

| Konu | Durum |
|---|---|
| Uzak CI (GitHub Actions) | **Çalıştırılmadı** — push yasak. Workflow yalnız YAML olarak ayrıştırıldı; işlerin komutları yerelde ayrı ayrı çalıştırıldı. |
| Gerçek Linux ana makine | **Çalıştırılmadı.** `local-stack.sh` Git Bash ile denendi. WSL dağıtımında Docker entegrasyonu kapalı; ayar değiştirilmedi. |
| Çok örnekli çalışma | Denenmedi. |
| Yük / performans testi | Yapılmadı; throughput/latency iddiası yok. |
| Gerçek kamera ile tarama | **Gerçek cihazda doğrulanmadı.** Chromium'un sahte kamera aygıtı ve QR içeren sentetik videoyla getUserMedia → jsQR → check-in zinciri E2E'de çalışıyor; izin reddi ve desteklenmeyen tarayıcı durumları test edildi. |
| Elle ekran okuyucu (NVDA/JAWS/VoiceOver) | Yapılmadı. |
| Firefox / Safari / gerçek mobil cihaz | Denenmedi (yalnız Chromium). |
| `prefers-reduced-motion`, `forced-colors` | Kurallar yazıldı; test edilmedi. |
| E2E ile çalıştırılmayan organizatör/yönetim eylemleri | Plan sürümü oluşturma, kademe fiyatı, etkinlik düzenleme/taslağa alma, üye ekleme-çıkarma, kapı ataması düğmeleri, askıya alma/geri alma, yayından kaldırma, dead-letter yeniden oynatma — ekranları açılıp incelendi; arka uçları entegrasyon testlidir, arayüzden tıklanarak E2E yapılmadı. |
| Gerçek ödeme / e-posta / SMS | Kapsam dışı; yok. |

Ayrıntılı sınır listesi: [`known-limitations.md`](known-limitations.md).
