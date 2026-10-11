using System.Text;
using System.Text.Json;
using Loca.Domain;

namespace Loca.UnitTests;

public sealed class CanonicalRequestTests
{
    private static string Hash(string json, string method = "POST", string path = "/api/sessions/1/holds") =>
        CanonicalRequest.ComputeHash(method, path, Encoding.UTF8.GetBytes(json));

    [Fact]
    public void Key_order_and_whitespace_do_not_change_the_hash()
    {
        var a = Hash("""{"seatIds":["b","a"],"note":"x"}""");
        var b = Hash("""
            { "note" : "x",
              "seatIds" : [ "b", "a" ] }
            """);
        Assert.Equal(a, b);
    }

    [Fact]
    public void Array_order_values_method_and_path_do_change_the_hash()
    {
        var baseline = Hash("""{"seatIds":["a","b"]}""");
        Assert.NotEqual(baseline, Hash("""{"seatIds":["b","a"]}"""));
        Assert.NotEqual(baseline, Hash("""{"seatIds":["a","c"]}"""));
        Assert.NotEqual(baseline, Hash("""{"seatIds":["a","b"]}""", method: "PUT"));
        Assert.NotEqual(baseline, Hash("""{"seatIds":["a","b"]}""", path: "/api/sessions/2/holds"));
    }

    [Fact]
    public void Integer_notation_and_unicode_normalization_are_canonicalized()
    {
        Assert.Equal(Hash("""{"quantity":2}"""), Hash("""{"quantity":2.0}"""));
        Assert.NotEqual(Hash("""{"quantity":2}"""), Hash("""{"quantity":"2"}"""));

        var composed = "{\"name\":\"Şule\"}";      // Ş (tek kod noktası)
        var decomposed = "{\"name\":\"Şule\"}";   // S + birleşen çengel
        Assert.Equal(Hash(composed), Hash(decomposed));
    }

    [Fact]
    public void Canonical_form_is_stable_bytes()
    {
        var canonical = CanonicalRequest.CanonicalizeJson(Encoding.UTF8.GetBytes("""{ "b": [1, {"d": null, "c": true}], "a": "x" }"""));
        Assert.Equal("""{"a":"x","b":[1,{"c":true,"d":null}]}""", Encoding.UTF8.GetString(canonical));
    }

    [Fact]
    public void Empty_body_hashes_and_differs_from_an_empty_object()
    {
        Assert.NotEqual(Hash(""), Hash("{}"));
        Assert.Equal(64, Hash("").Length);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("""{"a":1} trailing""")]
    [InlineData("""{"a":1,"a":2}""")]
    [InlineData("""{"a":1,}""")]
    public void Malformed_or_ambiguous_json_is_rejected(string json) =>
        Assert.ThrowsAny<JsonException>(() => Hash(json));

    [Theory]
    [InlineData("0123456789abcdef", true)]
    [InlineData("order:2026-11-20_A.b-c", true)]
    [InlineData("short", false)]
    [InlineData("has space 0123456789", false)]
    [InlineData("türkçe-karakter-0123", false)]
    [InlineData(null, false)]
    public void Key_format(string? key, bool valid) => Assert.Equal(valid, CanonicalRequest.IsValidKey(key));

    [Fact]
    public void Key_length_bounds()
    {
        Assert.False(CanonicalRequest.IsValidKey(new string('k', 15)));
        Assert.True(CanonicalRequest.IsValidKey(new string('k', 16)));
        Assert.True(CanonicalRequest.IsValidKey(new string('k', 128)));
        Assert.False(CanonicalRequest.IsValidKey(new string('k', 129)));
    }
}

public sealed class WebhookSignatureTests
{
    private static readonly byte[] Secret = Encoding.UTF8.GetBytes("unit-test-secret-current");
    private static readonly byte[] Previous = Encoding.UTF8.GetBytes("unit-test-secret-previous");
    private static readonly DateTimeOffset Now = new(2026, 11, 20, 17, 30, 0, TimeSpan.Zero);
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("""{"id":"evt_1","type":"charge.succeeded"}""");

    private static WebhookVerification Verify(string? header, byte[]? body = null, params byte[][] secrets) =>
        WebhookSignature.Verify(header, body ?? Body, secrets.Length == 0 ? [Secret] : secrets, Now, WebhookSignature.DefaultTolerance);

    [Fact]
    public void Valid_signature_over_raw_bytes_is_accepted()
    {
        var header = WebhookSignature.Sign(Secret, Now.ToUnixTimeSeconds(), Body);
        Assert.Equal(WebhookVerification.Valid, Verify(header));
    }

    [Fact]
    public void Semantically_equal_but_byte_different_body_fails()
    {
        var header = WebhookSignature.Sign(Secret, Now.ToUnixTimeSeconds(), Body);
        var reserialized = Encoding.UTF8.GetBytes("""{ "id": "evt_1", "type": "charge.succeeded" }""");
        Assert.Equal(WebhookVerification.SignatureInvalid, Verify(header, reserialized));
    }

    [Fact]
    public void Wrong_secret_and_tampered_timestamp_fail()
    {
        var forged = WebhookSignature.Sign(Encoding.UTF8.GetBytes("attacker"), Now.ToUnixTimeSeconds(), Body);
        Assert.Equal(WebhookVerification.SignatureInvalid, Verify(forged));

        var header = WebhookSignature.Sign(Secret, Now.ToUnixTimeSeconds(), Body);
        var shifted = header.Replace($"t={Now.ToUnixTimeSeconds()}", $"t={Now.ToUnixTimeSeconds() + 1}");
        Assert.Equal(WebhookVerification.SignatureInvalid, Verify(shifted));
    }

    [Theory]
    [InlineData(-301, WebhookVerification.TimestampOutOfRange)]
    [InlineData(-300, WebhookVerification.Valid)]
    [InlineData(300, WebhookVerification.Valid)]
    [InlineData(301, WebhookVerification.TimestampOutOfRange)]
    public void Correctly_signed_but_stale_or_future_timestamps_are_outside_the_replay_window(int offsetSeconds, WebhookVerification expected)
    {
        var header = WebhookSignature.Sign(Secret, Now.ToUnixTimeSeconds() + offsetSeconds, Body);
        Assert.Equal(expected, Verify(header));
    }

    [Fact]
    public void Any_active_secret_verifies_during_rotation_and_a_removed_secret_does_not()
    {
        var signedWithPrevious = WebhookSignature.Sign(Previous, Now.ToUnixTimeSeconds(), Body);
        Assert.Equal(WebhookVerification.Valid, Verify(signedWithPrevious, null, Secret, Previous));
        Assert.Equal(WebhookVerification.SignatureInvalid, Verify(signedWithPrevious, null, Secret));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("v1=abcd")]
    [InlineData("t=notanumber,v1=00")]
    [InlineData("t=1700000000")]
    [InlineData("t=1700000000,v1=zz")]
    [InlineData("garbage")]
    public void Malformed_headers_are_rejected(string? header) =>
        Assert.Equal(WebhookVerification.Malformed, Verify(header));
}

public sealed class TicketTokenTests
{
    [Fact]
    public void Tokens_are_well_formed_unique_and_carry_no_structure_beyond_the_version_prefix()
    {
        var tokens = Enumerable.Range(0, 2000).Select(_ => TicketToken.Generate()).ToList();
        Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
        Assert.All(tokens, t =>
        {
            Assert.True(TicketToken.IsWellFormed(t));
            Assert.StartsWith("LT1.", t);
            Assert.Equal(47, t.Length);
        });
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("LT1.")]
    [InlineData("LT2.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("LT1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA+")]
    [InlineData("LT1.AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Malformed_tokens_are_detected_without_a_database_lookup(string? token) =>
        Assert.False(TicketToken.IsWellFormed(token));

    [Fact]
    public void Hash_is_deterministic_and_does_not_contain_the_token()
    {
        var token = TicketToken.Generate();
        var hash = TicketToken.Hash(token);
        Assert.Equal(32, hash.Length);
        Assert.Equal(hash, TicketToken.Hash(token));
        Assert.NotEqual(hash, TicketToken.Hash(TicketToken.Generate()));
    }
}

public sealed class TicketIdentityTests
{
    [Fact]
    public void Same_order_line_and_ordinal_always_produce_the_same_version_5_uuid()
    {
        var order = Guid.Parse("018f4c5e-0000-7000-8000-000000000001");
        var line = Guid.Parse("018f4c5e-0000-7000-8000-000000000002");

        var first = TicketIdentity.For(order, line, 1);
        Assert.Equal(first, TicketIdentity.For(order, line, 1));
        Assert.NotEqual(first, TicketIdentity.For(order, line, 2));
        Assert.NotEqual(first, TicketIdentity.For(line, order, 1));
        Assert.Equal(5, first.Version);
    }

    [Fact]
    public void Uuid_v5_matches_the_rfc_test_vector()
    {
        // RFC 9562 Ek A.4: DNS ad alanı + "www.example.com"
        var dns = Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8");
        Assert.Equal(Guid.Parse("2ed6657d-e927-568b-95e1-2665a8aea6a2"), TicketIdentity.UuidV5(dns, "www.example.com"));
    }
}
