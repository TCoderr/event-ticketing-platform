# 07 — Arayüz Tasarımı

## 1. Skill ve kaynak keşfi

| Aranan | Bulunan | Karar |
|---|---|---|
| Ürün arayüzü için frontend / tasarım / erişilebilirlik skill'i | Oturumda yok (`artifact-design` yalnız Claude artifact sayfaları içindir, ürün arayüzüne uygulanmaz) | Tasarım ilkeleri bu belgede somutlaştırıldı |
| Veri görselleştirme | `dataviz` (Claude Code ile gelen yerleşik skill) | Organizatör özetindeki sayı kutucukları ve kapasite çubuğu için okundu ve uygulandı; palet skill'in `validate_palette.js` betiğiyle doğrulanır |

Harici skill veya betik indirilmedi.

## 2. Kimlik: "Loca"

Ad, tiyatrodaki *loca*dan gelir; ürünün merkezinde salon planı vardır. Görsel dil basılı bilet ve
salon krokisinden beslenir: kâğıt zemin, mürekkep rengi yazı, tek bir sinyal rengi.

| Öğe | Karar |
|---|---|
| Zemin / mürekkep | `#F5F0E6` kâğıt, `#1C1A17` mürekkep; ikincil metin `#5A544A` |
| Vurgu | `#C8371D` (kiremit kırmızısı) — yalnız birincil eylem ve seçili koltuk |
| Durum | Başarı `#1F6B45`, uyarı `#8A5A00`, hata `#A3231A`, bilgi `#1F4E79` — her zaman ikon + metinle |
| Başlık yazısı | Fraunces (değişken, serif) — etkinlik adı, büyük sayılar |
| Arayüz yazısı | IBM Plex Sans; koltuk kodu, fiyat, sayaç için IBM Plex Mono (tabular rakam) |
| Yazı kaynağı | `@fontsource` paketleriyle **yerelden** sunulur (OFL-1.1); harici CDN yok |
| Biçim | Düz yüzeyler, 1px mürekkep çizgileri, bilet kenarı gibi kesik çizgi ayırıcılar. Gölge ve kart yığını yok |

Bilinçli olarak kaçınılanlar: mor gradient, parıltı (glow), her şeyi karta bölme, tanıtım (landing)
sayfası, 3B/dekoratif animasyon.

## 3. Ekranlar

### Müşteri
1. **Keşif** — liste, kart değil satır düzeni: tarih sütunu (gün/ay büyük), etkinlik adı, mekan-şehir,
   en düşük fiyat. Filtreler: şehir, kategori, tarih aralığı.
2. **Etkinlik / seans** — başlık, seans listesi, satış durumu (satışta / X'te başlar / tükendi / kapandı).
3. **Seçim**
   - Koltuklu: SVG salon planı + **eşdeğer koltuk listesi** (sıra bazında gruplanmış onay kutuları).
     İkisi aynı durumu paylaşır; liste klavye ve ekran okuyucu için birincil yoldur.
   - Genel giriş: kategori başına adet seçici.
   - Özet çubuğu: seçilenler, toplam, "Koltukları tut" eylemi. En fazla 6.
4. **Hold + ödeme** — geri sayım (sunucu `expiresAt − serverTime` farkından), satır özeti, ödeme
   yöntemi. Demo seansta senaryo seçici "Demo" etiketiyle.
5. **Sipariş durumu** — `Ödeme işleniyor` / `Ödendi` / `Reddedildi` / `Sonuç belirsiz` / `Süre doldu`
   / `İptal edildi · iade bekliyor|tamamlandı|başarısız`.
6. **Biletlerim** — bilet listesi; bilet ayrıntısında QR, etiket, seans saati, durum.

### Organizatör
Sol gezinme (masaüstü) / üst sekmeler (mobil): Etkinlikler, Mekanlar, Ekip, Operasyon.
- Etkinlik editörü, seans editörü (mod, saat, satış penceresi, plan + kademe fiyatları veya kategoriler).
- Seans özeti: kapasite çubuğu (satıldı / tutuluyor / boş), sayı kutucukları, tanım dipnotlarıyla
  finansal metrikler (§01-5).
- Operasyon kuyruğu: belirsiz ödemeler, başarısız iadeler, geç başarı telafileri, iptal işleri;
  her eylemin sonucu `planlandı / denendi / başarılı / başarısız` olarak ayrı gösterilir.

### Kapı (gate)
Tek sütun, mobil öncelikli, koyu yüksek kontrast.
- Seans seçimi → büyük tarama alanı. Kamera (izin istenir) **veya** elle token girişi.
- Sonuç paneli ekranın üst yarısını kaplar: **KABUL** (✓, düz dolgu) / **ZATEN KULLANILDI** (↺, çapraz
  tarama dokusu, ilk giriş zamanı) / **GEÇERSİZ** (✕, kalın çerçeve, neden) / **BAĞLANTI YOK**
  (⚠, kesik çerçeve, "sonuç bilinmiyor — yeniden dene").
- Renk tek başına anlam taşımaz: her durum ikon + büyük metin + farklı doku/çerçeve ile ayrılır.
- Ağ hatasında aynı Idempotency-Key ile yeniden deneme; başarı varsayılmaz.

## 4. Durum ekranları

Her veri ekranı için: yükleniyor (iskelet değil, sade metin + `aria-busy`), boş, hata (yeniden dene),
yetkisiz (giriş / erişim yok), süre doldu, tükendi, ödeme belirsiz. Hiçbiri sahte başarı veya sahte
metrik göstermez; bağlı olmayan eylem düğmesi yoktur.

## 5. Erişilebilirlik

- Hedef: WCAG 2.2 AA. Metin kontrastı ≥ 4.5:1, büyük metin ve arayüz öğeleri ≥ 3:1.
- Koltuk durumu şekille ayrılır: boş = içi boş çerçeve, seçili = dolu + onay işareti, dolu = çapraz çizgili.
- Tüm etkileşim klavyeyle; görünür `:focus-visible` halkası (2px mürekkep + 2px zemin boşluğu).
- Form hataları alanla `aria-describedby` ile bağlı; özet `role="alert"`.
- Geri sayım `aria-live="off"`; yalnız 2 dakika ve 30 saniye kala `polite` duyuru.
- `prefers-reduced-motion`: geçişler kapatılır.
- Genişlikler: 360, 768, 1440. Yatay taşma yok; dokunma hedefi ≥ 44px.
- Doğrulama: otomatik (axe) + klavye gezinme E2E. **Elle ekran okuyucu testi yapılmadıysa
  `known-limitations.md` içinde açıkça belirtilir.**

## 6. Zaman gösterimi

Seans saati her zaman **seansın timezone'unda** ve timezone etiketiyle gösterilir
(`20 Kas 2026 Cum 20:30 · Europe/Istanbul`). Tarayıcı timezone'u farklıysa ikinci satırda
"Senin saatinle …" eklenir. Satış/iptal/check-in kararlarını tarayıcı saati vermez.

## 7. İstemci durumu ve güvenlik

- Token/secret `localStorage`/`sessionStorage`'a yazılmaz. Oturum HttpOnly çerezdedir; CSRF token'ı
  yalnız bellektedir.
- TanStack Query anahtarları kullanıcı kimliğiyle kapsamlanır (`['u', userId, …]`); public sorgular ayrı
  kapsamdadır. Logout ve kullanıcı değişiminde özel önbellek tamamen temizlenir.
- Müsaitlik ipucudur; kesin sonuç hold API'sindedir. Çakışmada anlaşılır mesaj + plan yenilenir.
- Gerçek zamanlılık: polling (koltuk planı 5 sn, sipariş durumu 1 sn → geri çekilmeli) ve her
  değişiklikten sonra otoriter HTTP yenileme. SignalR yok.

## 8. Prototip

`design/preview.html` — backend bitmeden yazılan statik prototip; tüm veriler "Demo" etiketlidir.
Son arayüz prototipe yön verir; erişilebilirlik ve gerçek API gereği yapılan sapmalar
`implementation-progress.md` içinde gerekçesiyle kaydedilir.

## 9. Tarayıcıda inceleme kaydı

Gerçek tarayıcıda (Chromium), gerçek API ve veritabanına karşı incelenen ekranlar (her biri 360 / 768 / 1440 genişlikte):

| Grup | Ekranlar |
|---|---|
| Müşteri | keşif, etkinlik, koltuk seçimi, genel giriş, ödeme, sipariş durumu, siparişlerim, biletlerim, bilet, giriş (hata), kayıt, 404 |
| Organizatör | etkinlikler, etkinlik, mekanlar, seans editörü (koltuklu / genel giriş / iptal), seans özeti, ekip, operasyon |
| Kapı | seans listesi, tarama (hazır), sonuç panelleri |
| Yönetim | organizasyonlar, etkinlikler, operasyon, dead-letter, demo PSP |

Ekran görüntüleri: `artifacts/verification/screenshots/` (bilet QR'ı maskelidir). Bulunup düzeltilen kusurlar ve doğrulanamayanlar:
`final-verification.md` §3 ve §5. Prototipten gerekçeli sapmalar: `implementation-progress.md`, Aşama 8.
