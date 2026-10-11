# 09 — Test ve Doğrulama Planı

## 1. Katmanlar

| Katman | Kapsam | Araç |
|---|---|---|
| Birim | Durum makineleri, `Money`, zaman politikaları, kanonik hash, webhook imzası, token | xUnit v3 |
| Entegrasyon | Kısıtlar, kilitler, rollback, roller, idempotency, worker'lar, HTTP sözleşmesi | xUnit v3 + Testcontainers (PostgreSQL 18.6) + `WebApplicationFactory` |
| Bileşen | Koltuk seçimi, geri sayım, kapı sonuç paneli, QR gidiş-dönüş | Vitest + Testing Library + jsQR |
| Uçtan uca | Gerçek tarayıcı + API + veritabanı | Playwright (Chromium) + axe |
| Smoke | Compose HTTPS stack | `scripts/smoke.ps1` |

**SQLite ve EF InMemory kullanılmaz.** Eşzamanlılık iddiaları yalnız gerçek PostgreSQL üzerinde kanıtlanır.

## 2. Test veritabanı güvenliği

Test altyapısı yalnız **kendi başlattığı** Testcontainers PostgreSQL'ine yazar. Koruma (`TestDatabaseGuard`),
ilk yazmadan önce üç koşulu birlikte ister:

1. **Açık onay:** fixture'ın ürettiği koşu kimliği (run id) bağlantı hedefiyle birlikte verilir.
2. **Sunucu kimliği:** `pg_control_system().system_identifier`, konteyner başlatılırken kaydedilen değerle eşleşir.
3. **Koşuya ait işaret:** hedef veritabanında `loca_test_marker` tablosu ve içinde aynı koşu kimliği bulunur
   (işaret, veritabanı fixture tarafından yaratılırken yazılır; ad öneki tek başına yeterli sayılmaz).

Yanlış hedefte korumanın **hiçbir yazma yapmadan** reddettiği ayrı bir testle doğrulanır. Geliştirme
stack'inin veya başka projelerin veritabanına migration/seed/TRUNCATE/hata enjeksiyonu uygulanmaz.

## 3. Deterministiklik

- **Saat:** üretimde veritabanı saati; test host'unda DI ile `ManualClock`. Süre dolumu testleri saati
  ilerletir, uyumaz. Veritabanı saati yolunu doğrulayan ayrı testler vardır.
- **Yarışlar:** `Barrier` / `TaskCompletionSource` ve test failpoint'leriyle eşzamanlı başlatma ve
  kontrollü sıralama. Her "iki sıra" senaryosu için sıra açıkça zorlanır (kilit elde tutulurken diğer
  taraf başlatılır); rastgele bekleme ve "5 denemeden 4'ü" kanıt sayılmaz.
- **Failpoint:** `IFailpoints` üretimde no-op'tur; kontrol edilebilir sürüm yalnız test host'unda kayıtlıdır.
- **PSP simülatörü:** gerçek HTTP sınırı üzerinden çağrılır; "sağlayıcı kaydetti, yanıt kayboldu" durumu
  simülatörün işlemi commit edip yanıtı düşürmesiyle üretilir (mock dönüşü değil).

## 4. Kritik invariant → test eşlemesi

Sınıflar `tests/Loca.IntegrationTests/` altındadır (aksi belirtilmedikçe).

| Invariant | Test sınıfı |
|---|---|
| Aynı koltuk, 25 eşzamanlı istek → tek hold; ters sıralı çok koltuklu istekler | `SeatHoldConcurrencyTests` |
| GA: `held + sold <= capacity`, kapasite altı/üstü yarış, çok kategorili istek | `GeneralAdmissionConcurrencyTests` |
| Çok satırlı hold hep-ya-hiç; seçim kuralları; fiyat snapshot'ı değişmez | `HoldAtomicityTests` |
| Süre sınırı eşitliği, gecikmiş worker, devralma, GA tembel toplama | `HoldExpiryTests` |
| Üretim saat yolu (veritabanı saati) | `DatabaseClockTests` |
| Replay bayt eşitliği, farklı gövde 409, yanıt kaybı, eşzamanlı aynı anahtar, TTL, güncel kimlik doğrulama | `IdempotencyTests` |
| Deadlock kurbanı yeniden deneme, bütçe | `TransactionRetryTests` |
| Aynı hold farklı anahtar → tek sipariş/ödeme; DB unique | `CheckoutUniquenessTests` |
| Başarı / ret / belirsiz / yanıt kaybı / asenkron / tekrarlı / sırasız sonuçlar | `PaymentOutcomeTests` |
| Ödeme başarısı ↔ süre dolumu, iki sıra (gerçek kilitlerle) | `PaymentExpiryRaceTests` |
| Sahte imza / zaman damgası / ham bayt / event-id çakışması / rotasyon / tutar uyuşmazlığı | `WebhookTests` |
| Reconciliation (planned/attempted/succeeded/failed), lease, iade yanıt kaybı, dead-letter + operatör | `ReconciliationAndRefundTests` |
| Lease dolumu, fencing, geri çekilme, zehirli mesaj, çökme sonrası devralma | `OutboxWorkerTests` |
| Tekrarlı issuance tek bilet; token saklama; sahiplik | `TicketIssuanceTests` |
| İki kapı eşzamanlı → tek kabul; replay; yanlış seans/org; pencere; commit öncesi hata | `CheckInTests` |
| Müşteri iptali, iade senaryoları, iptal sınırı | `CustomerCancellationTests` |
| İptal ↔ check-in, iki sıra + eşzamanlı invariant | `CancelCheckInRaceTests` |
| Seans iptali toplu işi, çökme sonrası imleçten devam, fencing, iptal-geç ödeme | `SessionCancellationTests` |
| Saklama (yalnız teknik kayıtlar), FK bağımsızlığı, worker kapanışı | `RetentionAndShutdownTests` |
| `loca_app` DDL / audit güncelleme / TRUNCATE yapamaz; readiness | `DatabaseRoleTests`, `DatabaseOutageTests` |
| Organizasyonlar arası erişim, yetki yükseltme, mass assignment | `AuthorizationBoundaryTests`, `CatalogOwnershipTests` |
| Yayınlanmamış içerik sızıntısı, cache başlıkları, sayfalama | `CatalogVisibilityTests`, `CatalogPaginationTests` |
| Logout sonrası çerez replay, CSRF, oturum süresi | `SessionAndCsrfTests` |
| KPI tanımları, operasyon kuyruğu sayımı, yetki | `SalesSummaryTests`, `OperationsQueueTests` |
| Log ve metriklerde PII / yüksek kardinalite yok; OpenAPI'de secret yok | `ObservabilityTests` |
| Hız sınırı bölümlemesi, iletilen başlıkların yok sayılması | `RateLimitTests` |
| Boş kurulum, çift migrate, önceki aşama verisinden yükseltme (uçtan uca satış dahil) | `MigrationUpgradeTests` |
| Test hedefi koruması yanlış hedefte yazmadan reddeder | `TestDatabaseGuardTests` |
| Belgelenen API operasyonları = canlı OpenAPI | `ApiDocumentationTests` |
| Durum makineleri, para, zaman politikaları, kanonik özet, imza, token | `tests/Loca.UnitTests/*` |
| QR gidiş-dönüş (gerçek çözücü), koltuk seçimi, sayaç, API istemcisi | `web/src/**/*.test.ts(x)` |

## 5. Migration doğrulaması

1. `dotnet ef migrations has-pending-model-changes` (iki DbContext).
2. Boş veritabanına tam uygulama.
3. Bir önceki aşamanın migration'ına kadar uygula → sentetik veri ekle → son migration'a yükselt →
   veri ve kısıtları doğrula (`MigrationUpgradeTests`).
4. SQL betiği üretimi (`dotnet ef migrations script --idempotent`) — yalnız betik üretmek doğrulama sayılmaz.

## 6. Komutlar (xUnit v3 / .NET 10 SDK)

```powershell
dotnet restore --locked-mode
dotnet build -c Release --no-restore
dotnet test tests/Loca.UnitTests -c Release --no-build
dotnet test tests/Loca.IntegrationTests -c Release --no-build
npm ci            # web/
npm run typecheck; npm run lint; npm run test; npm run build
npm run e2e       # ayrı, koşuya özel Compose stack'i
```

Her koşu için bulunan / geçen / başarısız / atlanan sayıları kaydedilir. Atlanan test geçmiş sayılmaz;
fixture hatası uygulama hatası olarak etiketlenmez.

## 7. E2E ortamı

Geliştirme verisi kullanılmaz. Her koşu kendi Compose project adı, portu ve volume'u ile boş bir stack
açar (`loca-e2e-<koşu>`); bitince sahipliği doğrulanarak (manifest + Compose etiketi) silinir.

## 8. Final kapılar

1. Kilitli restore + Release build.
2. Birim + gerçek PostgreSQL entegrasyon; kritik invariant'larda atlama yok.
3. Web typecheck / lint / üretim build'i / bileşen testleri.
4. Gerçek tarayıcı E2E: müşteri (koltuk + GA) → ödeme → bilet; organizatör yalnız kendi verisi; kapı
   tarama → kabul → ikinci tarama zaten-kullanıldı; hold süre dolumu; tükendi çakışması; ret;
   belirsiz → kurtarılan ödeme; iptal → iade → bilet geçersiz; seans iptali; yanlış organizatör;
   logout replay; CSRF.
5. Migration/model tutarlılığı, boş kurulum, önceki aşama verisinden yükseltme.
6. Compose HTTPS smoke: oluştur / hold / replay / ödeme / check-in; restart kalıcılığı; DB kesinti-kurtarma.
7. Arayüz: müşteri/organizatör/kapı kritik ekranları 3 genişlikte, klavye ve otomatik erişilebilirlik.
8. Depo ve log secret/yazar taraması; root olmayan çalışma; PII yanıt/güvenlik sınırları.
9. Oluşturulan test kaynaklarının sahiplik doğrulamasıyla temizlenmesi.

## 9. İddia sınırları

Kontrollü eşzamanlılık testleri doğruluğu kanıtlar, performansı değil. Ayrı bir yük testi
çalıştırılmadıkça throughput/latency iddiası yapılmaz. Yerel komutların geçmesi uzak CI'ın geçtiği
anlamına gelmez; push yapılmadığı için uzak CI "çalıştırılmadı" olarak raporlanır.
