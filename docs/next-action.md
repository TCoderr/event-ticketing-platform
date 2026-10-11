# Sonraki Adım

**Durum:** Aşama 0–10 tamamlandı (2026-10-10). Planlanan kapsamda açık iş yok.

Kanıt: [`final-verification.md`](final-verification.md). Bilinen sınırlar: [`known-limitations.md`](known-limitations.md).

**Kullanıcı kararı bekleyenler (bu görevde bilinçli olarak yapılmadı):**
- İlk commit, remote depo, push ve uzak CI'ın çalıştırılması.

**Yeni bir oturumda devam edilecekse:** eski "geçti" kaydına güvenme; kaynak parmak izini `final-verification.md` başındaki komutla
yeniden hesapla. Farklıysa kapıları yeniden çalıştır:

```powershell
dotnet restore --locked-mode; dotnet build -c Release --no-restore
dotnet test --project tests/Loca.UnitTests -c Release --no-build
dotnet test --project tests/Loca.IntegrationTests -c Release --no-build
cd web; npm ci; npm run typecheck; npm run lint; npm run test; npm run build; npm run e2e -- --build; cd ..
.\scripts\local-stack.ps1 init; node scripts/smoke.mjs --project loca; node scripts/scan-repo.mjs
```
