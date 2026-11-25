using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TenantHub.Application.Exports;
using TenantHub.Application.Integrations;

namespace TenantHub.Application.Tests.Integrations;

/// <summary>SPEC-V2 5.8: the key format, the signature as documented, and CSV cells a spreadsheet cannot run.</summary>
public sealed class IntegrationHelpersTests
{
    [Fact]
    public void A_key_is_thk_and_32_random_bytes_and_only_its_sha256_is_kept()
    {
        var (key, prefix, hash) = ApiKeySecrets.New();
        var (other, _, _) = ApiKeySecrets.New();

        Assert.Matches("^thk_[A-Za-z0-9_-]{43}$", key);
        Assert.Equal(key[..12], prefix);
        Assert.Equal(SHA256.HashData(Encoding.UTF8.GetBytes(key)), hash);
        Assert.NotEqual(key, other);
        Assert.True(ApiKeySecrets.LooksLikeKey(key));
        Assert.False(ApiKeySecrets.LooksLikeKey(key[..^1]));
        Assert.False(ApiKeySecrets.LooksLikeKey("thk_" + new string('+', 43)));
        Assert.False(ApiKeySecrets.LooksLikeKey(null));
    }

    [Fact]
    public void The_signature_matches_an_independent_implementation_of_the_documented_algorithm()
    {
        var at = DateTimeOffset.FromUnixTimeSeconds(1_795_000_000);
        const string body = """{"id":"0193","type":"job.created"}""";
        const string current = "whsec_current";
        const string previous = "whsec_previous";

        var header = WebhookSignature.Sign([current, previous], at, body);

        // The documented recipe, step by step: "<t>.<body>", HMAC-SHA256 with the secret, lowercase hex.
        static string Expected(string secret, string t, string body)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            return string.Concat(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{t}.{body}")).Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));
        }

        Assert.Equal($"t=1795000000,v1={Expected(current, "1795000000", body)},v1={Expected(previous, "1795000000", body)}", header);
        Assert.Matches("^whsec_[0-9a-f]{64}$", WebhookSignature.NewSecret());
    }

    [Fact]
    public void The_payload_has_the_documented_shape()
    {
        var id = Guid.NewGuid();
        var org = Guid.NewGuid();
        var body = WebhookDeliveryMessage.BodyFor(id, "job.created", DateTimeOffset.FromUnixTimeSeconds(0), org, JsonSerializer.SerializeToElement(new { n = 1 }));

        using var doc = JsonDocument.Parse(body);
        Assert.Equal(["id", "type", "occurredAt", "organizationId", "data"], doc.RootElement.EnumerateObject().Select(p => p.Name));
        Assert.Equal(id, doc.RootElement.GetProperty("id").GetGuid());
        Assert.Equal(org, doc.RootElement.GetProperty("organizationId").GetGuid());
    }

    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+1", "'+1")]
    [InlineData("-1", "'-1")]
    [InlineData("@A1", "'@A1")]
    [InlineData("\tx", "'\tx")]
    [InlineData("\rx", "\"'\rx\"")]
    [InlineData("a=b", "a=b")]
    [InlineData("plain", "plain")]
    [InlineData("", "")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("two\nlines", "\"two\nlines\"")]
    [InlineData("=x,\"y\"", "\"'=x,\"\"y\"\"\"")]
    public void A_cell_is_neutralized_and_quoted_as_spreadsheets_need(string value, string expected) =>
        Assert.Equal(expected, CsvWriter.Field(value));

    [Fact]
    public async Task Rows_end_with_crlf_and_the_encoding_writes_a_byte_order_mark()
    {
        await using var writer = new StringWriter(CultureInfo.InvariantCulture);
        var csv = new CsvWriter(writer);

        await csv.WriteRowAsync(["a", null, "=b"]);
        await csv.WriteRowAsync(["c"]);

        Assert.Equal("a,,'=b\r\nc\r\n", writer.ToString());
        Assert.Equal([0xEF, 0xBB, 0xBF], CsvWriter.Utf8WithBom.GetPreamble());
    }
}
