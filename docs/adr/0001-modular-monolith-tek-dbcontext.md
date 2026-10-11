# ADR 0001 — Modular monolith, tek veritabanı, tek DbContext, modül şemaları

**Durum:** Kabul edildi (2026-10-10)

## İhtiyaç
Hold oluşturma, ödeme sonucunu uygulama ve iptal; envanter, sipariş, bilet, outbox ve audit'i **tek
atomik transaction** içinde değiştirmek zorunda.

## Karar
Tek süreç, tek PostgreSQL veritabanı, tek `AppDbContext`. Her modülün kendi şeması
(`identity, catalog, inventory, orders, tickets, ops`). İki üretim assembly'si: `Loca.Domain` (saf
kurallar) ve `Loca.Api` (modüller + altyapı). PSP simülatörü ayrı DbContext ve şema kullanır (ADR 0007).

## Daha basit / başka alternatifler
- *Katmansız tek proje:* domain kuralları HTTP/EF'ten ayrılamaz, birim testleri zorlaşır.
- *Modül başına DbContext:* sınırı derleyici zorlar, ama çok modüllü tek transaction için ortak
  bağlantı/transaction paylaşımı ve birden çok migration seti gerekir.
- *Mikroservis + broker:* dağıtık transaction/saga karmaşıklığı; bu kapsamda karşılığı yok.

## Maliyet
Modül sınırı disiplinle korunur: bir modülün tablolarına yalnız sahibi olan servis yazar
(`03-target-architecture.md` §2).

## Ne zaman değişir
Bir modül bağımsız ölçeklenmek veya ayrı ekipçe yayınlanmak zorunda kalırsa; o zaman şema sınırı
servis sınırına, in-process handler'lar broker tüketicisine dönüşür.
