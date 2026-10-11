# ADR 0004 — Çerez oturumu, sunucu oturum kaydı ve CSRF

**Durum:** Kabul edildi (2026-10-10)

## İhtiyaç
Aynı origin'den sunulan tek tarayıcı uygulaması. Logout sonrası eski çerez yeniden kullanılamamalı;
rol/üyelik değişikliği anında geçerli olmalı.

## Karar
- ASP.NET Core Identity (kullanıcı deposu + parola + kilitlenme) ve çerez kimlik doğrulaması.
- Çerez yalnız kullanıcı kimliği ve oturum kimliği (`sid`) taşır. `identity.user_sessions` her istekte
  doğrulanır; logout satırı iptal eder.
- Yetki verisi (platform yöneticiliği, üyelik, kapı ataması) çerezde taşınmaz, istek başına veritabanından okunur.
- CSRF: ASP.NET antiforgery; token yalnız `GET /api/auth/csrf` ve login yanıtında üretilir.
- Data Protection anahtarları kalıcı volume'da tutulur (restart çerezleri düşürmez).

## Alternatifler
- *JWT (durumsuz):* iptal için yine sunucu durumu gerekir; XSS'te token sızıntısı riski; bu kapsamda kazanç yok.
- *OAuth2/OIDC:* üçüncü taraf istemci veya harici kimlik sağlayıcı yok.
- *Yalnız çerez (oturum kaydı olmadan):* logout sonrası çalınmış çerez süresi dolana kadar geçerli kalır.

## Maliyet
Kimliği doğrulanmış her istekte bir birincil anahtar sorgusu.

## Ne zaman değişir
Mobil uygulama / üçüncü taraf API istemcisi gerekirse (OIDC + token).
