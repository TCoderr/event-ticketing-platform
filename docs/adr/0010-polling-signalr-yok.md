# ADR 0010 — Gerçek zamanlılık: polling + otoriter HTTP yenileme

**Durum:** Kabul edildi (2026-10-10)

## İhtiyaç
Koltuk müsaitliği ve sipariş durumu ekranda makul gecikmeyle güncellenmeli.

## Karar
SignalR eklenmedi. Koltuk planı ve GA müsaitliği birkaç saniyede bir, sipariş durumu geri çekilmeli
aralıkla yoklanır; her değişiklik eyleminden sonra ilgili sorgular yeniden çekilir. Müsaitlik
arayüzde yalnız **ipucudur**; kesin sonuç hold API'sindedir.

## Alternatifler
- *SignalR ile invalidation:* daha düşük gecikme; bağlantı yetkilendirmesi, yeniden bağlanma ve
  kaçan mesaj telafisi gerektirir. Kaçan mesaj nedeniyle yine otoriter HTTP yenileme şarttır.
- *Veritabanı sequence imleciyle "değişiklik akışı":* sequence değerleri commit sırasını yansıtmaz;
  kayıpsız akış gibi sunulamaz.

## Maliyet
Birkaç saniyelik bayat görünüm; boşta istek trafiği.

## Ne zaman değişir
Ölçüm, yoklama trafiğinin veya gecikmenin sorun olduğunu gösterirse.
