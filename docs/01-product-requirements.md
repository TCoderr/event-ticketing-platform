# 01 — Ürün Gereksinimleri

**Ürün:** Loca — birden çok organizatörlü etkinlik bileti satışı ve kapı (giriş) kontrolü.
**Sahip / yazar:** TCoderr
**Durum:** Yerelde uçtan uca çalışan portföy ürünü. Tüm kişiler, etkinlikler ve ödemeler sentetiktir ("Demo").

> Bu belgedeki süreler, iptal sınırları ve saklama süreleri **demo için varsayımdır**. Hukuki inceleme,
> KVKK uygunluk değerlendirmesi veya sektör standardı olarak sunulmaz.

## 1. İş problemi

Bilet satışında asıl zorluk ekran çizmek değil, aynı koltuğun iki kişiye satılmaması, ödeme sonucu
belirsizken envanterin doğru yönetilmesi ve kapıda aynı biletin iki kez kabul edilmemesidir. Loca bu
üç sorunu veritabanı düzeyinde kanıtlanabilir invariant'larla çözer; arayüz bu garantilerin dürüst
bir yansımasıdır.

## 2. Roller

| Rol | Kaynağı | Yapabildikleri |
|---|---|---|
| Customer | Kayıt olan her kullanıcı | Keşif, kendi hold/sipariş/biletleri, izinli tam iptal |
| OrganizerAdmin | `organization_members.role` | Yalnız kendi organizasyonunun mekan, etkinlik, seans, envanter, giriş ekibi, satış özeti, operasyon kuyruğu |
| GateStaff | `organization_members.role` + `gate_assignments` | Yalnız atandığı seanslarda online check-in |
| PlatformAdmin | `users.is_platform_admin` (yalnız seed/CLI ile) | Organizasyon onayı/askıya alma, yayından kaldırma, tüm operasyon kuyruğu, audit, dead-letter |

Rol/kaynak matrisi `06-security-and-failure-behavior.md` §3'tedir. İstemciden gelen `organizationId`
veya `userId` yetki kanıtı değildir; her istekte üyelik sunucuda veritabanından doğrulanır.

## 3. Kapsam (işlevsel)

### 3.1 Katalog
- Organizasyon başvurusu → PlatformAdmin onayı → `Active`. Yalnız `Active` organizasyon yayın yapabilir.
- Mekan (şehir, adres, IANA timezone), değişmez koltuk planı sürümleri.
- Etkinlik (`Draft / Published / Cancelled`), seans (`Draft / Published / Cancelled`).
- Seans tek satış modu kullanır: `ReservedSeating` **veya** `GeneralAdmission`. Karma mod yok.
- Public keşif: şehir / kategori / tarih filtresi, deterministik keyset sayfalama. Yalnız
  yayınlanmış etkinliğin, yayınlanmış, başlamamış ve aktif organizasyona ait seansları görünür.

### 3.2 Rezervasyon (hold)
- Tek seans, en fazla 6 bilet. Çok seanslı sepet yok.
- Varsayılan hold süresi 10 dakika (seans bazında ayarlanabilir, platform sınırları içinde).
- Saat otoritesi veritabanıdır; tarayıcı sayacı yalnız gösterimdir.
- Kullanıcı başına seans başına tek aktif hold.
- "Hold oluşturuldu" yanıtı satış garantisi değildir: `expiresAt` anına kadar envanterin başkasına
  verilmeyeceğini garanti eder, ödeme başarısını garanti etmez.

### 3.3 Sipariş ve ödeme
- Para birimi yalnız TRY; tutarlar minor unit (kuruş) tam sayı. Float para yok.
- Fiyat sunucuda belirlenir; hold anında değişmez satır snapshot'ı alınır.
- Bir hold için tek sipariş ve tek ödeme (iş kuralı düzeyinde unique).
- Simüle PSP: başarı, ret, geçici hata, timeout/belirsiz, tekrarlı callback, sırasız callback, geç başarı.
- PSP modeli: **anında tahsilat (immediate charge)**. Telafi her zaman **iade (refund)** olur; void
  yalnız tahsil edilmemiş provizyon için anlamlıdır ve bu modelde uygulanmaz.

### 3.4 Bilet ve giriş
- Yalnız `Paid` sipariş için QR bilet. QR içeriği rastgele (CSPRNG) taşıyıcı token'dır; kişisel veri içermez.
- Online check-in atomik ve tek kullanımlıktır. Offline check-in yok.

### 3.5 İptal ve iade
- Müşteri: tam sipariş iptali, seans başlangıcından 24 saat öncesine kadar (yapılandırılabilir demo kuralı),
  hiçbir bileti kullanılmamışsa.
- Organizatör: seans iptali; tüm ödenmiş siparişler kalıcı toplu iş ile iade edilir.
- Kısmi iade, bilet devri, yeniden satış yok.

### 3.6 Operasyon
- Organizatör satış/kapasite özeti; tanımlı finansal metrikler (§5).
- Operasyon kuyruğu: belirsiz ödemeler, başarısız iadeler, geç başarı telafileri, iptal işleri.
- Audit, outbox/inbox, kiralamalı (leased) worker'lar.
- Demo simülatörü: seans bazında ve opt-in (§6).

## 4. Zaman kuralları (demo varsayımı)

| Kural | Tanım |
|---|---|
| Satış penceresi | `salesStartAt <= now < salesEndAt`; `salesEndAt <= startsAt` (varsayılan `startsAt`) |
| Hold aktifliği | `now < expiresAt`. `now == expiresAt` anında hold **süresi dolmuştur** |
| Müşteri iptal sınırı | `now < startsAt - 24 saat` |
| Check-in penceresi | `startsAt - 3 saat <= now < startsAt + 6 saat` |
| İptal edilen seans | Yeniden satışa açılmaz |

`now` her zaman veritabanı saatidir.

## 5. Finansal metrik tanımları

Bir seans için:

| Metrik | Tanım |
|---|---|
| `grossCapturedMinor` | `Succeeded` ödemelerin tutar toplamı (geç başarılar dahil) |
| `refundedMinor` | `Succeeded` iadelerin tutar toplamı |
| `refundPendingMinor` | `Pending` veya `Failed` iadelerin tutar toplamı (henüz geri ödenmemiş) |
| `netMinor` | `grossCapturedMinor - refundedMinor` |
| `ticketsSold` | `Valid` + `CheckedIn` bilet sayısı |
| `ticketsCheckedIn` | `CheckedIn` bilet sayısı |
| `capacity / held / sold / available` | Envanter sayaçları; `held` yalnız süresi dolmamış hold'ları sayar |

Vergi, komisyon, hakediş (payout) ve muhasebe kaydı hesaplanmaz.

## 6. Demo simülatörü

Seans bazında `demoSimulatorEnabled` bayrağı (organizatör açar, varsayılan kapalı) ve platform
yapılandırması `Demo:SimulatorEnabled` birlikte gerekir.

- Açıksa ödeme adımında PSP senaryo seçici görünür (başarı / ret / belirsiz / asenkron / geç başarı).
  Kapalı seansta sunucu yalnız varsayılan başarı senaryosunu kabul eder.
- Açıksa arka plan worker'ı o seansta sentetik "demo izleyici" alımları üretir (aynı uygulama
  komutlarıyla; ayrı kod yolu yok).

## 7. Kapsam dışı

Gerçek PSP / e-posta / SMS, kampanya-kupon, dinamik fiyat, bekleme odası, CAPTCHA servisi, offline
check-in, native mobil uygulama, harita/GPS, yeniden satış, çoklu para birimi, marketplace
hakediş/komisyon/muhasebe, yasal fatura, Kubernetes ve bulut dağıtımı, AI/LLM özelliği. Bunlar için
boş iskelet üretilmez.

## 8. Kabul ölçütleri

Aşama bazlı kabul ölçütleri `08-implementation-plan.md`, doğrulama yöntemi
`09-test-and-verification-plan.md` içindedir.
