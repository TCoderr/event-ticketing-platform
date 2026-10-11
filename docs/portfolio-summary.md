# Portföy Özeti — Loca

**Sahip / yazar:** TCoderr · **Tür:** yerelde uçtan uca çalışan portföy projesi (üretimde kullanılmıyor; tüm veri sentetik)

## İş problemi

Bilet satışı, "listele ve sat" gibi görünen ama asıl zorluğu eşzamanlılık ve belirsizlik olan bir alandır:

1. **Aynı koltuk iki kişiye satılmamalı** — yüzlerce kişi aynı anda aynı koltuğa bassa bile.
2. **Ödeme sonucu her zaman net gelmez** — sağlayıcı zaman aşımına uğrar, yanıt kaybolur, bildirim iki kez veya ters sırada gelir, başarı hold süresi dolduktan sonra ulaşır.
3. **Aynı bilet kapıdan iki kez geçmemeli** — iki kapı aynı QR'ı aynı anda okusa bile; ve iptal edilmiş bilet kabul edilmemeli.

Loca bu üç problemi veritabanı düzeyinde zorlanan kurallarla çözer ve her birini gerçek PostgreSQL
üzerinde, sıralaması zorlanmış testlerle gösterir.

## Ne geliştirildi

- Çok organizatörlü katalog: mekan, değişmez koltuk planı sürümleri, etkinlik, seans (koltuklu **veya** genel giriş), yayın/moderasyon.
- Süreli hold (rezervasyon): hep-ya-hiç çok koltuklu claim, veritabanı saatiyle süre dolumu, kalıcı idempotency.
- Sipariş ve simüle ödeme: imzalı webhook, inbox dedupe, reconciliation, geç başarı telafisi, iade.
- QR bilet ve atomik online check-in; kapı ataması.
- Müşteri iptali ve seans iptali (kalıcı, kaldığı yerden devam eden toplu iş).
- Outbox + kiralamalı (lease + fencing) worker'lar, dead-letter, denetim kaydı, operasyon kuyruğu.
- React arayüzü: müşteri, organizatör, kapı ve yönetim ekranları.
- Docker Compose ile HTTPS yerel stack, demo verisi, CI tanımı.

## Teknik kararlar (ve neden)

| Karar | Neden | Vazgeçilen |
|---|---|---|
| Modular monolith, tek PostgreSQL, modül şemaları | Hold/ödeme/iptal tek atomik transaction ister | Mikroservis + saga karmaşıklığı |
| `READ COMMITTED` + sıralı `FOR UPDATE` + koşullu güncelleme + CHECK/unique | Deterministik kilit sırası deadlock'ı önler; kısıtlar uygulama hatasına karşı son savunmadır | Redis kilidi, yalnız optimistic concurrency |
| Tek kilit kökü (rezervasyon satırı) | Ödeme↔süre dolumu ve iptal↔check-in aynı kilitte yarışır; sonuç her zaman tek ve tutarlıdır | Aggregate başına ayrı kilitler |
| Idempotency kaydı iş etkisiyle aynı transaction'da | Çökme sonrası "takılı işlem" kalmaz; yanıt kaybında aynı baytlar döner | Kalıcı "InProgress" + kurtarma mantığı |
| Sağlayıcı çağrısı outbox handler'ında, istek yolunda değil | Dış çağrı ile veritabanı durumu arasında çökme penceresi kalmaz | İstek içinde senkron ödeme |
| Tek ödeme durum uygulama yolu + sağlayıcı sıra numarası | Tekrarlı/sırasız olayın iş etkisi tektir | Kaynağa göre ayrı kod yolları |
| Çerez + sunucu oturum kaydı + CSRF | Aynı origin SPA; logout gerçekten iptal eder | JWT/OIDC (bu kapsamda gereksiz) |
| Veritabanı saati, `IClock` arkasında | Tek otorite; testte saat ilerletilir, uyunmaz | Uygulama saati |
| Broker yok (DB outbox/inbox) | Harici tüketici yok; ek işletim yüzeyi gereksiz | RabbitMQ/Kafka |

Gerekçeler ve alternatifler: [`docs/adr/`](adr).

## Doğrulanmış garantiler

Aşağıdakilerin her biri gerçek PostgreSQL 18.6 üzerinde çalışan otomatik testlerle gösterilmiştir
(sayılar ve komutlar: [`final-verification.md`](final-verification.md)):

- Aynı koltuğa 25 eşzamanlı istek → tam olarak bir hold.
- Genel girişte kapasite altı ve üstü yarışlarda `held + sold ≤ capacity`; çok satırlı istek başarısız olursa hiçbir parça kalmaz.
- Süre sınırı: hold, bitişinden 1 mikrosaniye önce aktif, tam bitiş anında dolmuştur; worker hiç çalışmasa da devralınabilir.
- Aynı Idempotency-Key → aynı durum kodu ve aynı gövde baytları; commit sonrası yanıt kaybında da.
- Aynı hold için 12 eşzamanlı, farklı anahtarlı checkout → tek sipariş, tek tahsilat; veritabanı kısıtı kilit atlansa da ikinciyi reddeder.
- Ödeme başarısı ↔ hold süre dolumu: iki sıra da (gerçek satır kilitleriyle) doğru sonuçlanır; geç başarı siparişi yeniden ödenmiş yapmaz, tek iade üretir.
- Zaman aşımı ret değildir; "sağlayıcı tahsil etti ama yanıt kayboldu" durumu ikinci tahsilat yaratmadan kurtarılır.
- Sahte imza, eski zaman damgası, yeniden serileştirilmiş gövde ve aynı kimlikli farklı içerik reddedilir.
- Tekrarlanan bilet üretimi tek bilet; iki kapıdan 24 eşzamanlı tarama → tek kabul.
- İptal ↔ check-in: iki sıra da doğru; ikisi birden asla başarılı olmaz.
- Seans iptali toplu işi çöküp yeniden başladığında çift iade oluşmaz; lease'ini kaybeden eski sahibin yazısı reddedilir.
- Çalışma zamanı veritabanı rolü DDL çalıştıramaz, denetim kaydını ve değişmez snapshot'ları değiştiremez.
- Loglarda parola, çerez, token, e-posta yoktur; metrik etiketleri sabit ve düşük kardinalitelidir.

Bunlar **doğruluk** garantileridir. Performans, throughput veya üretim kullanımı iddiası yoktur; yük
testi yapılmamıştır (bkz. [`known-limitations.md`](known-limitations.md)).

## Demo yürüyüşü

Önkoşul: `.\scripts\local-stack.ps1 init` → `https://localhost:8643`. Parolalar
`.local/loca/users/<e-posta>.password.txt` dosyalarındadır.

1. **Keşif (girişsiz).** Ana sayfada demo seanslarını şehir/kategoriye göre süzün.
2. **Koltuk seçimi.** "Sis Vadisi Senfonisi (Demo)" seansını açın. Salon planından veya altındaki koltuk listesinden (klavyeyle) 2 koltuk seçin.
3. **Hold.** `customer-ayse@loca.demo` ile giriş yapıp "Koltukları tut"a basın. Geri sayım sunucu saatine göredir.
   İkinci bir tarayıcıda `customer-mehmet@loca.demo` ile aynı koltuğu seçmeyi deneyin: çakışma mesajı alırsınız.
4. **Ödeme.** Bu seansta demo simülatörü açıktır; senaryo seçin:
   - *Başarılı* → "Ödendi" → Biletlerim'de QR.
   - *Ret* → "Reddedildi"; koltuk hemen serbest kalır.
   - *Belirsiz* → önce "Sonuç belirsiz" (ret değildir), kısa süre sonra kendiliğinden "Ödendi".
5. **Bilet.** Biletlerim → bileti açın; QR yalnız geçerli bilette görünür.
6. **Kapı.** "Bu Akşam Stand-up (Demo)" seansından bir bilet alın (check-in penceresi seed anında açık olan seans; `gate-kumru@loca.demo`
   demo seanslarına önceden atanmıştır). Kapı görevlisi olarak girip bilet kodunu elle girin veya kamerayla okutun: **KABUL**.
   Aynı kodu yeniden girin: **ZATEN KULLANILDI** (ilk giriş zamanı ve kapısıyla).
7. **İptal ve iade.** Başlangıcına 24 saatten fazla olan bir seansın siparişini "Siparişlerim"den iptal edin: bilet anında geçersiz olur,
   iade durumu izlenir, koltuk yeniden satışa açılır.
8. **Organizatör.** `organizer-kumru@loca.demo` → seans özeti (kapasite çubuğu, brüt/iade/net tanımlı metrikler) ve operasyon kuyruğu.
   `organizer-marti@loca.demo` ile giriş yapınca Kumru Sahne'nin hiçbir verisi görünmez.
9. **Seans iptali.** Organizatör bir seansı iptal eder: seans keşiften kalkar, tüm siparişler iade edilir, biletler geçersiz olur.
10. **Yönetim.** `platform-admin@loca.demo` → organizasyon onayı, yayından kaldırma, tüm operasyon kuyruğu, demo PSP paneli
    (bekleyen simüle tahsilatı elle sonuçlandırma).

Aynı akışın otomatik sürümü: `node scripts/smoke.mjs --project loca` (API düzeyi) ve `npm run e2e` (`web/`, tarayıcı düzeyi).

## Teknolojiler

C# / .NET 10, ASP.NET Core Minimal APIs, EF Core 10, ASP.NET Core Identity, PostgreSQL 18, Npgsql,
xUnit v3, Testcontainers, React 19, TypeScript, Vite, TanStack Query, Vitest, Playwright, Docker Compose,
GitHub Actions (tanım).
