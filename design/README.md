# Loca — tasarım prototipi

`preview.html` tek dosyalık statik prototiptir; tarayıcıda doğrudan açılır (sunucu, derleme, ağ isteği yok).
Gerçek API'ye bağlı değildir: eylemler yalnız ekran durumunu değiştirir, tüm veriler sentetiktir ("Demo").
Tasarım otoritesi `docs/07-ui-design.md` belgesidir.

## Ekranlar (hash ile)

| Hash | Ekran |
|---|---|
| `#kesif` | Müşteri — keşif (satır düzeni, şehir / kategori / tarih filtresi) |
| `#koltuk` | Müşteri — koltuk seçimi (SVG plan + eşdeğer onay kutusu listesi, en fazla 6) |
| `#genel-giris` | Müşteri — genel giriş kategori / adet seçimi |
| `#odeme` | Müşteri — rezervasyon sayacı, satır özeti, demo ödeme senaryosu |
| `#siparis` | Müşteri — sipariş durumu varyantları (durum değiştirici ile) |
| `#bilet` | Müşteri — bilet ayrıntısı (QR yer tutucu) |
| `#organizator` | Organizatör — seans özeti, kapasite çubuğu, finans kutucukları, operasyon kuyruğu |
| `#kapi` | Kapı — koyu tema, tarama alanı, elle token, sonuç paneli varyantları |

## Notlar

- Yazı tipleri: yığında Fraunces / IBM Plex Sans / IBM Plex Mono ilk sıradadır; prototip font yüklemez,
  kurulu değilse sistem yedekleri (Georgia, Segoe UI, Consolas) kullanılır.
- Kapasite çubuğu paleti: satıldı `#2B5F9E`, tutuluyor `#B8650A`, boş `#0F8A78`
  (`validate_palette.js`, light, yüzey `#F5F0E6`: tüm kontroller geçti).
- Salon planı fare/dokunma içindir ve yardımcı teknolojiden gizlidir; klavye ve ekran okuyucu yolu koltuk listesidir.
  Plandaki koltuklar 44px'ten küçüktür; eşdeğer 44px hedef listededir.
- Elle ekran okuyucu testi yapılmamıştır.
