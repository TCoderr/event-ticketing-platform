# ADR 0005 — Saat otoritesi veritabanıdır

**Durum:** Kabul edildi (2026-10-10)

## İhtiyaç
Hold süresi, satış penceresi, iptal sınırı ve check-in penceresi kararları tek ve tutarlı bir saatle
verilmeli; tarayıcı saati ve örnekler arası saat kayması karar vermemeli. Testler deterministik olmalı.

## Karar
`IClock.NowAsync()` üretimde aynı bağlantı/transaction üzerinde `SELECT clock_timestamp()` çalıştırır
ve değeri mikrosaniyeye yuvarlar. Komut, bu değeri tüm koşullu SQL'lere parametre olarak geçirir.
Test host'unda `IClock` DI ile `ManualClock` olur.

Sınır: hold `now < expires_at` iken aktiftir; `now == expires_at` anında süresi dolmuştur.

Zaman okuması tek başına güvenlik sağlamaz: iki işlem farklı anlarda saat okuyabilir. Doğruluğu
sağlayan, kararın satır kilidi altında verilip **kalıcı duruma** yazılmasıdır (ADR 0002).

## Alternatifler
- *Uygulama saati (`TimeProvider`):* testte kolay; çok örnekte saat kayması sınırları kaydırır.
- *SQL içinde doğrudan `now()`:* tek otorite, fakat test için veritabanına saat kancası koymayı gerektirir.

## Maliyet
Komut başına bir küçük ek sorgu.

## Ne zaman değişir
Öngörülmüyor.
