using Npgsql;

namespace Loca.Api.Infrastructure.Config;

/// <summary>
/// Veritabanı bağlantısı. Parolalar yapılandırma değerinde değil, dosyada tutulur (<c>*PasswordFile</c>).
/// Çalışma zamanı <c>AppUser</c> ile, migration <c>OwnerUser</c> ile bağlanır.
/// </summary>
public sealed class DatabaseOptions
{
    public const string Section = "Database";

    /// <summary>Test ve tasarım zamanı için tam bağlantı dizesi; verilirse diğer alanların önüne geçer.</summary>
    public string? AppConnectionString { get; set; }
    public string? OwnerConnectionString { get; set; }

    public string Host { get; set; } = "";
    public int Port { get; set; } = 5432;
    public string Name { get; set; } = "";
    public string AppUser { get; set; } = "loca_app";
    public string? AppPasswordFile { get; set; }
    public string OwnerUser { get; set; } = "loca_owner";
    public string? OwnerPasswordFile { get; set; }
    public int MaxPoolSize { get; set; } = 50;

    public string BuildAppConnectionString() =>
        AppConnectionString ?? Build(AppUser, AppPasswordFile, "app");

    public string BuildOwnerConnectionString() =>
        OwnerConnectionString ?? Build(OwnerUser, OwnerPasswordFile, "owner");

    private string Build(string user, string? passwordFile, string role)
    {
        if (string.IsNullOrWhiteSpace(Host) || string.IsNullOrWhiteSpace(Name))
            throw new InvalidOperationException($"Database configuration is incomplete: Database:Host and Database:Name are required for the {role} connection.");
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = Host,
            Port = Port,
            Database = Name,
            Username = user,
            Password = SecretFile.ReadRequired(passwordFile, $"Database:{(role == "app" ? "AppPasswordFile" : "OwnerPasswordFile")}"),
            MaxPoolSize = MaxPoolSize,
            Timeout = 5,
            ApplicationName = $"loca-{role}",
            IncludeErrorDetail = false,
            // Kerberos/GSS kullanılmaz; imajda kütüphanesi de yoktur. Denemeyi kapatmak gürültülü uyarıyı önler.
            GssEncryptionMode = GssEncryptionMode.Disable,
        };
        return builder.ConnectionString;
    }
}

public static class SecretFile
{
    public static string ReadRequired(string? path, string settingName)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"{settingName} is not configured. Secrets are read from files; no default is provided.");
        if (!File.Exists(path))
            throw new InvalidOperationException($"{settingName} points to a file that does not exist.");
        var value = File.ReadAllText(path).Trim();
        if (value.Length == 0)
            throw new InvalidOperationException($"{settingName} points to an empty file.");
        return value;
    }

    public static string? ReadOptional(string? path) =>
        string.IsNullOrWhiteSpace(path) || !File.Exists(path) ? null : File.ReadAllText(path).Trim() is { Length: > 0 } v ? v : null;
}
