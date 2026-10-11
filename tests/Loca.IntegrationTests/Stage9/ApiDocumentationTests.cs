using System.Net;
using System.Text.RegularExpressions;
using Loca.IntegrationTests.Infrastructure;

namespace Loca.IntegrationTests.Stage9;

/// <summary>Belgelenen operasyon listesi ile çalışan uygulamanın OpenAPI çıktısı birebir aynı olmalıdır.</summary>
public sealed class ApiDocumentationTests(PostgresFixture postgres) : IntegrationTest(postgres)
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Loca.slnx"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root (Loca.slnx) not found.");
    }

    [Fact]
    public async Task Documented_operations_match_the_live_openapi_document()
    {
        var document = (await Factory.NewClient().GetAsync("/openapi/v1.json")).ShouldBe(HttpStatusCode.OK).Json;
        var live = document["paths"]!.AsObject()
            .SelectMany(path => path.Value!.AsObject().Select(operation => $"{operation.Key.ToUpperInvariant()} {path.Key}"))
            .Order(StringComparer.Ordinal).ToList();

        var markdown = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "docs", "05a-api-operations.md"));
        var documented = Regex.Matches(markdown, @"^\| (GET|POST|PUT|DELETE|PATCH) \| `([^`]+)` \|$", RegexOptions.Multiline)
            .Select(m => $"{m.Groups[1].Value} {m.Groups[2].Value}").Order(StringComparer.Ordinal).ToList();

        Assert.NotEmpty(documented);
        Assert.Equal(live, documented);
    }

    [Fact]
    public async Task Every_documented_error_code_exists_in_the_domain_catalog()
    {
        var markdown = await File.ReadAllTextAsync(Path.Combine(RepositoryRoot(), "docs", "05-api-contracts.md"));
        var section = markdown[markdown.IndexOf("## 10. Hata kodu kataloğu", StringComparison.Ordinal)..];
        var documented = Regex.Matches(section, @"[a-z]+(?:_[a-z]+)+").Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        var defined = typeof(Loca.Domain.ErrorCodes).GetFields().Where(f => f.IsLiteral).Select(f => (string)f.GetRawConstantValue()!).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(documented.Except(defined).Order());
    }
}
