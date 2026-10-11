using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Loca.Api.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Loca.Api.Infrastructure.Config;

public sealed class HostingOptions
{
    public const string Section = "Hosting";

    /// <summary>0 ise Kestrel uçları koddan yapılandırılmaz (test host'u ve <c>dotnet run</c>).</summary>
    public int HttpsPort { get; set; }

    /// <summary>Yalnız loopback'e bağlanan HTTP dinleyici: simülatör ↔ uygulama ve konteyner healthcheck'i için.</summary>
    public int InternalHttpPort { get; set; }

    public string? CertificatePfxPath { get; set; }
    public string? CertificatePasswordFile { get; set; }
}

public sealed class LocalTlsOptions
{
    public const string Section = "LocalTls";
    public bool AutoGenerate { get; set; }
    public string Directory { get; set; } = "";
}

public sealed class DemoOptions
{
    public const string Section = "Demo";
    public bool SeedEnabled { get; set; }
    public bool SimulatorEnabled { get; set; }
    public string? SeedPasswordDirectory { get; set; }
}

/// <summary>Yerel demo için self-signed sertifika (ADR 0008). Yalnız <c>LocalTls:AutoGenerate=true</c> iken kullanılır.</summary>
public static class LocalCertificate
{
    public const string PfxFileName = "loca-local.pfx";
    public const string PublicCertFileName = "loca-local.crt";

    public static X509Certificate2 LoadOrCreate(string directory)
    {
        Directory.CreateDirectory(directory);
        var pfxPath = Path.Combine(directory, PfxFileName);
        if (File.Exists(pfxPath))
        {
            var existing = X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password: null);
            if (existing.NotAfter > DateTime.Now.AddDays(7)) return existing;
            existing.Dispose();
        }

        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost, O=Loca local demo", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName("localhost");
        san.AddIpAddress(IPAddress.Loopback);
        san.AddIpAddress(IPAddress.IPv6Loopback);
        request.CertificateExtensions.Add(san.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));

        using var created = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(365));
        var pfx = created.Export(X509ContentType.Pfx);
        WritePrivate(pfxPath, pfx);
        File.WriteAllText(Path.Combine(directory, PublicCertFileName), created.ExportCertificatePem());
        return X509CertificateLoader.LoadPkcs12(pfx, password: null);
    }

    private static void WritePrivate(string path, byte[] content)
    {
        File.WriteAllBytes(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}

/// <summary>Hazır olma durumu: veritabanına erişim ve bekleyen migration olmaması.</summary>
public sealed class ReadinessProbe(IServiceScopeFactory scopes)
{
    private volatile bool _migrationsVerified;

    public async Task<(bool Ready, string Reason)> CheckAsync(CancellationToken ct)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            await db.Database.ExecuteSqlRawAsync("SELECT 1", timeout.Token);
            if (!_migrationsVerified)
            {
                var pending = await db.Database.GetPendingMigrationsAsync(timeout.Token);
                if (pending.Any()) return (false, "pending_migrations");
                _migrationsVerified = true;
            }
            return (true, "ok");
        }
        catch (Exception)
        {
            return (false, "database_unavailable");
        }
    }
}
