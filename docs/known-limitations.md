# Bilinen Sınırlar ve Doğrulanmayanlar

Bu belge, ürünün bilinçli olarak yapmadıklarını ve **doğrulanmamış** olanları açıkça listeler.

## Dağıtım ve ölçek

| Konu | Durum |
|---|---|
| Tek / çok örnek | Tasarım çok örnekli çalışmaya uygundur (koordinasyon veritabanında: satır kilitleri, lease + fencing, unique kısıtlar; saat veritabanından). **Doğrulanan** yalnız tek örnekli Compose çalışması ile aynı veritabanına karşı eşzamanlı istek/worker testleridir. Birden çok uygulama örneğiyle dağıtım denenmedi. |
| Yük / performans | Yük testi çalıştırılmadı. Eşzamanlılık testleri doğruluğu kanıtlar; throughput, gecikme veya kapasite iddiası **yoktur**. |
| Uzak CI | Workflow (`.github/workflows/ci.yml`) yazıldı; push yapılmadığı için **GitHub üzerinde çalıştırılmadı**. Yerel komutların geçmesi uzak CI'ın geçtiği anlamına gelmez. |
| Linux | `scripts/local-stack.sh` Windows üzerinde Git Bash ile çalıştırılarak denendi. **Gerçek Linux ana makinede denenmedi** (dosya izinleri — secret dosyalarının konteyner kullanıcılarınca okunabilmesi — orada farklı davranabilir). |
| Üretim sertifikası | Yerel stack self-signed sertifika üretir. Gerçek sertifika yapılandırması (`Hosting:CertificatePfxPath`) kod yolunda vardır, denenmedi. |
| Rate limit | Süreç içi (bellek) sayaçlardır; çok örnekte örnek başına uygulanır. Anonim uçlarda bölümleme bağlantının uzak adresidir; NAT arkasındaki kullanıcılar aynı kovayı paylaşır. |

## Kimlik ve oturum

- Logout, sunucudaki oturum satırını iptal eder; eski çerezin yeniden gönderilmesi reddedilir (testli). Oturum doğrulaması her istekte veritabanına gider; önbellek yoktur.
- Parola sıfırlama, e-posta doğrulama, 2FA, hesap silme **yok**. Kullanıcı yalnız `disabled_at` ile devre dışı bırakılabilir (veritabanı düzeyinde; yönetim ekranı yok).
- CAPTCHA / bot koruması yok; yalnız hız sınırı ve hesap kilidi.
- Data Protection anahtarları volume'da **şifresiz** durur (ASP.NET Core bunun için uyarı loglar). Anahtar volume'u kaybolursa mevcut oturumlar düşer ve mevcut biletlerin QR'ı sahibine yeniden gösterilemez (özet durur; yeniden üretim akışı yazılmadı).

## Ödeme

- **Gerçek ödeme yok.** Sağlayıcı bir simülatördür; uygulamayla aynı süreçte barınır (ayrı şema ve yalnız HTTP + webhook ile konuşur). "Yalnız sağlayıcı kapalı" durumu ayrı bir süreç durdurularak değil, hata senaryosu token'ları ve testlerdeki HTTP hata enjeksiyonu ile üretilir.
- Model anında tahsilattır; provizyon/capture ve dolayısıyla **void yoktur**. Kısmi iade yok.
- Sağlayıcıda kaydı bulunmayan ve hold süresi dolmuş bir ödeme "gönderilmedi" olarak kapatılır. Ret/hata olarak kapanmış bir ödeme için sağlayıcı sonradan başarı bildirirse (gerçek bir sağlayıcıda çok geç ulaşan istekle mümkün; simülatörde oluşmaz) durum geri dönmez ve **otomatik iade yapılmaz**: ödeme `review_reason = success_reported_after_final` ile işaretlenir ve operasyon kuyruğunda görünür; çözümü elle yapılmalıdır.
- Vergi, komisyon, hakediş, fatura hesaplanmaz. Özetteki "net" yalnız `brüt tahsilat − başarılı iade`dir.
- Tutar/para birimi uyuşmayan sağlayıcı olayı uygulanmaz ve `review_reason` ile işaretlenir; bu işareti temizleyen bir operatör eylemi yoktur.

## Bilet ve giriş

- **Offline check-in yok.** Kapı ekranı çevrimdışıyken tarama göndermez; ağ hatasında sonuç "bilinmiyor" gösterilir ve aynı anahtarla yeniden denenir.
- Kamera ile tarama **gerçek cihazda doğrulanmadı**; yalnız Chromium'un sahte kamera aygıtı ve sentetik QR videosuyla (getUserMedia → jsQR → check-in) E2E'de sınandı. Odak, ışık, açı, mobil izin arayüzü ve arka kamera seçimi denenmedi. Elle kod girişi her zaman kullanılabilir alternatiftir.
- Bilet devri, yeniden satış, isim değişikliği yok. QR sabittir (dönen/zamanlı kod değil): QR'ın görüntüsünü ele geçiren kişi bileti kullanabilir; koruma tek kullanımlık olmasıdır.
- Etkinlik düzeyinde iptal ucu yok; iptal seans bazındadır.

## Katalog

- Seans yayınlandıktan sonra saat/mekan değiştirilemez (yalnız fiyat/kapasite, kısıtlarla). Erteleme akışı yok.
- Koltuk planı ızgara bölümlerden üretilir; serbest çizim, eğimli sıralar, engelli erişim işaretleri yok.
- Tutulmuş/satılmış koltuğu olan kademe yeniden fiyatlanamaz (katı kural).
- Demo kataloğunun tarihleri seed anına göre sabitlenir; yakın tarihli demo seansı başladıktan sonra kendiliğinden yenilenmez.

## Arayüz ve erişilebilirlik

- Otomatik erişilebilirlik denetimi (axe) ve klavye senaryoları çalıştırıldı (sonuçlar `final-verification.md`). **Elle ekran okuyucu (NVDA / JAWS / VoiceOver) testi yapılmadı.**
- Yalnız Chromium ile test edildi; Firefox, Safari ve gerçek mobil cihaz denenmedi.
- Yalnız Türkçe arayüz; çoklu dil yok.
- `prefers-reduced-motion` ve `forced-colors` kuralları yazıldı, test edilmedi.
- Organizatör/yönetim tarafında bazı eylemler (plan sürümü oluşturma, kademe fiyatı, etkinlik düzenleme, üye ekleme-çıkarma, kapı ataması, askıya alma, yayından kaldırma, dead-letter yeniden oynatma) arayüzden E2E ile tıklanmadı; arka uçları entegrasyon testlidir.
- İki organizasyonun yöneticisi olan bir kullanıcı, seans ekranını yanlış organizasyon gezinmesi altında açabilir (seans yanıtı `organizationId` taşımıyor). Veri sızmaz; yalnız gezinme bağlamı yanlış görünür.
- Kapı ekibi listesinde üye rolü/e-postası gösterilmez (aynı adlı iki üye ayırt edilemez). Operasyon kuyruğundaki zamanlar tarayıcı saat diliminde, etiketiyle gösterilir.
- Hold süresi sınırları (en az/en çok) istemciye ayrı bir uçla sunulmaz; geçersiz değer sunucu hatasıyla bildirilir.
- Gerçek zamanlılık yoklama (polling) ile sağlanır; müsaitlik birkaç saniye bayat olabilir (kesin sonuç hold API'sindedir).

## Veri ve saklama

- Saklama süreleri demo varsayımıdır; **KVKK uygunluk değerlendirmesi yapılmadı**, uygunluk iddiası yoktur.
- Sipariş, ödeme, iade, bilet ve denetim kayıtları silinmez; kişisel veri silme/anonimleştirme akışı yok.
- Yedekleme/geri yükleme prosedürü yok.

## Gözlemlenebilirlik

- Metrikler `System.Diagnostics.Metrics` sayaçlarıdır ve yönetici ucundan anlık görüntü olarak okunur; Prometheus/OpenTelemetry dışa aktarımı bağlanmadı. Dağıtık izleme yok.

## Bağımlılık bulguları

`dotnet list package --vulnerable` ve `npm audit` çıktıları `final-verification.md` içinde, çalıştırıldıkları
tarihle birlikte olduğu gibi raporlanır; bulgu varsa gizlenmez.
