# Sürümler, Kaynakları ve Lisanslar

Sürümler 2026-10-10 tarihinde üretici kaynaklarından doğrulandı ve kilit dosyalarıyla sabitlendi
(`packages.lock.json`, `web/package-lock.json`). Görev sırasında paket yükseltmesi yapılmadı.

## Sürüm kararları

| Bileşen | Sürüm | Doğrulama kaynağı | Karar notu |
|---|---|---|---|
| .NET SDK / runtime | 10.0.401 / 10.0.12 | `dotnet --list-sdks`, api.nuget.org | .NET 10 LTS |
| EF Core / Identity / OpenAPI / Mvc.Testing | 10.0.12 | api.nuget.org flat container | Runtime ile aynı servis sürümü |
| Npgsql + EF sağlayıcısı | 10.0.3 | api.nuget.org | EF Core 10 ile uyumlu ana sürüm |
| EFCore.NamingConventions | 10.0.1 | api.nuget.org | snake_case adlandırma |
| xUnit v3 | 4.0.2 | api.nuget.org | .NET 10 SDK'da Microsoft Testing Platform ile (`global.json` → `test.runner`) |
| Testcontainers.PostgreSql | 4.16.0 | api.nuget.org | |
| PostgreSQL | 18.6 | Docker Hub resmi imajı | Test ve yerel stack aynı sürümü kullanır |
| Node.js / npm | 24.18.0 / 12.1.0 | `node --version` | |
| React / React Router / TanStack Query | 19.3.0 / 7.18.4 / 5.104.1 | npm registry | |
| Vite / Vitest | 8.3.4 / 5.0.3 | npm registry | |
| TypeScript | 6.0.3 | npm registry | 7.x seçilmedi: `typescript-eslint@8.71.1` peer aralığı `<6.1.0` |
| ESLint | 9.39.5 | npm registry | 10.x seçilmedi: `eslint-plugin-jsx-a11y@6.10.2` peer aralığı `^9` |
| Playwright | 1.63.0 | npm registry + `browsers.json` | 1.64 seçilmedi: yerel önbellekteki Chromium (rev. 1243) 1.63.0 ile eşleşiyor; tarayıcı indirmesi gerekmedi |
| GitHub Actions | checkout v7.0.1, setup-dotnet v6.0.0, setup-node v7.1.0, upload-artifact v7.0.2 | `gh api` (sürüm → commit SHA) | Workflow'da commit SHA ile sabitlendi |

## Üçüncü taraf lisansları

Lisans bilgisi, kurulu paketlerin kendi metadatasından (`package.json` / `.nuspec`) okunmuştur. Üçüncü
taraf lisans ve atıf bildirimleri paketlerin içinde olduğu gibi durur; kaldırılmamış veya değiştirilmemiştir.

### Çalışma zamanı (uygulamayla birlikte dağıtılan)

| Paket | Lisans |
|---|---|
| ASP.NET Core, EF Core, ASP.NET Core Identity, Microsoft.AspNetCore.OpenApi | MIT |
| Npgsql, Npgsql.EntityFrameworkCore.PostgreSQL | PostgreSQL License |
| EFCore.NamingConventions | Apache-2.0 |
| react, react-dom, react-router-dom, @tanstack/react-query | MIT |
| qrcode-generator | MIT |
| jsqr | Apache-2.0 |
| @fontsource-variable/fraunces (Fraunces), @fontsource/ibm-plex-sans, @fontsource/ibm-plex-mono (IBM Plex) | SIL Open Font License 1.1 |

### Geliştirme ve test

| Paket | Lisans |
|---|---|
| xunit.v3, xunit.runner.visualstudio | Apache-2.0 |
| Microsoft.NET.Test.Sdk, Microsoft.AspNetCore.Mvc.Testing, Testcontainers.PostgreSql, dotnet-ef | MIT |
| vite, @vitejs/plugin-react, vitest, jsdom, eslint, @eslint/js, typescript-eslint, eslint-plugin-react-hooks, eslint-plugin-jsx-a11y, globals, @testing-library/*, pngjs, @types/* | MIT |
| typescript, @playwright/test | Apache-2.0 |
| @axe-core/playwright | MPL-2.0 |

### Konteyner imajları

| İmaj | Not |
|---|---|
| `postgres:18.6` | PostgreSQL License |
| `mcr.microsoft.com/dotnet/sdk:10.0.401`, `mcr.microsoft.com/dotnet/aspnet:10.0.12` | Microsoft .NET imaj lisans koşulları (MIT lisanslı .NET + temel imajın bileşen lisansları) |
| `node:24-alpine` | Yalnız SPA derleme aşamasında |

## Kullanılan skill ve araçlar

| Skill / araç | Kaynak | Kullanım |
|---|---|---|
| `dataviz` | Claude Code ile gelen yerleşik skill (harici indirme yok) | Organizatör özetindeki sayı kutucukları ve kapasite çubuğu: form seçimi ve `scripts/validate_palette.js` ile palet doğrulaması |
| Ürün arayüzü için frontend/erişilebilirlik skill'i | Oturumda uygun skill bulunamadı | Tasarım ilkeleri `docs/07-ui-design.md` içinde elle somutlaştırıldı |

Harici skill, betik veya şablon indirilmedi.

## Bağımlılık güvenlik taraması (2026-10-10)

| Komut | Sonuç |
|---|---|
| `dotnet list package --vulnerable --include-transitive` | 4 projede bilinen güvenlik açığı raporlanmadı |
| `npm audit` (`web/`) | 0 bulgu |
| `npm audit --omit=dev` (`web/`) | 0 bulgu |

Bu sonuçlar taramanın çalıştırıldığı andaki danışma veritabanlarına göredir; sonradan yayımlanacak
bulguları kapsamaz.
