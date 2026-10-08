using System.Security.Cryptography;
using System.Text.Json;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Notifications;

namespace GreedyNose.Api.Tests;

/// <summary>
/// Story 2.6: in Azure the two keys arrive as text (app settings), not as files. Path variant, inline
/// variant, precedence, and no key material in any message.
/// </summary>
public sealed class InlineKeyConfigurationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"inline-key-{Guid.NewGuid():N}");

    public InlineKeyConfigurationTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static string NewPem()
    {
        using var rsa = RSA.Create(2048);
        return rsa.ExportPkcs8PrivateKeyPem();
    }

    private static string NewServiceAccountJson()
    {
        return JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["type"] = "service_account",
            ["project_id"] = "test-project",
            ["private_key_id"] = "abc",
            ["private_key"] = NewPem(),
            ["client_email"] = "svc@test-project.iam.gserviceaccount.com",
            ["client_id"] = "1",
            ["token_uri"] = "https://oauth2.googleapis.com/token",
        });
    }

    // --- Enable Banking ---------------------------------------------------------------------------

    [Fact]
    public void Enable_Banking_signs_with_a_key_file()
    {
        var path = Path.Combine(_dir, "eb.pem");
        File.WriteAllText(path, NewPem());

        using var signer = new EnableBankingSigner(new EnableBankingOptions { ApplicationId = "app", PrivateKeyPath = path });

        Assert.Equal(3, signer.CreateToken(DateTimeOffset.UtcNow).Split('.').Length);
    }

    [Fact]
    public void Enable_Banking_signs_with_inline_pem_text()
    {
        using var signer = new EnableBankingSigner(new EnableBankingOptions { ApplicationId = "app", PrivateKey = NewPem() });

        Assert.Equal(3, signer.CreateToken(DateTimeOffset.UtcNow).Split('.').Length);
    }

    [Fact]
    public void Enable_Banking_accepts_inline_pem_with_literal_backslash_n()
    {
        var flattened = NewPem().Replace("\r\n", "\n").Replace("\n", "\\n");

        using var signer = new EnableBankingSigner(new EnableBankingOptions { ApplicationId = "app", PrivateKey = flattened });

        Assert.Equal(3, signer.CreateToken(DateTimeOffset.UtcNow).Split('.').Length);
    }


    [Theory]
    [InlineData("dq")]
    [InlineData("sq")]
    [InlineData("crlf")]
    [InlineData("space")]
    [InlineData("quoted-crlf-space")]
    [InlineData("quoted-flattened")]
    public void Enable_Banking_tolerates_mangled_inline_pem(string kind)
    {
        var pem = NewPem().Replace("\r\n", "\n").TrimEnd();
        var value = kind switch
        {
            "dq" => $"\"{pem}\"",
            "sq" => $"'{pem}'",
            "crlf" => pem.Replace("\n", "\r\n") + "\r\n",
            "space" => $"  \n {pem} \n  ",
            "quoted-crlf-space" => $" \"{pem.Replace("\n", "\r\n")}\" \r\n",
            _ => $"\"{pem.Replace("\n", "\n")}\"",
        };

        using var signer = new EnableBankingSigner(new EnableBankingOptions { ApplicationId = "app", PrivateKey = value });

        Assert.Equal(3, signer.CreateToken(DateTimeOffset.UtcNow).Split('.').Length);
    }

    [Fact]
    public void Enable_Banking_quoted_garbage_still_fails_without_echo()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new EnableBankingSigner(new EnableBankingOptions { ApplicationId = "app", PrivateKey = "\"SECRET-MARKER\"" }));

        Assert.DoesNotContain("SECRET-MARKER", ex.ToString());
    }
    [Fact]
    public void Enable_Banking_inline_pem_wins_over_the_path()
    {
        // The path points nowhere: if it were consulted the constructor would throw.
        var missing = Path.Combine(_dir, "no-such.pem");

        using var signer = new EnableBankingSigner(
            new EnableBankingOptions { ApplicationId = "app", PrivateKey = NewPem(), PrivateKeyPath = missing });

        Assert.Equal(3, signer.CreateToken(DateTimeOffset.UtcNow).Split('.').Length);
    }

    [Fact]
    public void Enable_Banking_with_both_empty_fails_naming_both_settings()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            new EnableBankingSigner(new EnableBankingOptions { ApplicationId = "app", PrivateKey = "  ", PrivateKeyPath = "" }));

        Assert.Contains("EnableBanking:PrivateKey", ex.Message);
        Assert.Contains("EnableBanking:PrivateKeyPath", ex.Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Enable_Banking_invalid_pem_does_not_echo_itself_anywhere(bool inline)
    {
        const string Garbage = "-----BEGIN PRIVATE KEY-----\nSECRET-MARKER\n-----END PRIVATE KEY-----";
        var options = new EnableBankingOptions { ApplicationId = "app" };
        if (inline)
        {
            options.PrivateKey = Garbage;
        }
        else
        {
            options.PrivateKeyPath = Path.Combine(_dir, "bad.pem");
            File.WriteAllText(options.PrivateKeyPath, Garbage);
        }

        var ex = Assert.Throws<InvalidOperationException>(() => new EnableBankingSigner(options));

        Assert.DoesNotContain("SECRET-MARKER", ex.ToString());
        Assert.Null(ex.InnerException);
    }

    // --- Firebase ---------------------------------------------------------------------------------

    [Fact]
    public void Firebase_loads_a_key_file()
    {
        var path = Path.Combine(_dir, "fb.json");
        File.WriteAllText(path, NewServiceAccountJson());

        Assert.NotNull(FirebaseAppFactory.LoadCredential(path));
    }

    [Fact]
    public void Firebase_loads_inline_json_through_the_options()
    {
        var options = new FirebaseOptions { ServiceAccountJson = NewServiceAccountJson() };

        // GetOrCreate would create the process-wide default FirebaseApp; the credential is the unit here.
        Assert.NotNull(FirebaseAppFactory.LoadCredentialFromJson(options.ServiceAccountJson));
    }

    [Fact]
    public void Firebase_with_both_empty_fails_naming_both_settings()
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FirebaseAppFactory.GetOrCreate(new FirebaseOptions { ServiceAccountJson = " ", ServiceAccountPath = "" }));

        Assert.Contains("Firebase:ServiceAccountJson", ex.Message);
        Assert.Contains("Firebase:ServiceAccountPath", ex.Message);
    }

    [Fact]
    public void Firebase_inline_json_wins_over_the_path()
    {
        // A bad inline value must fail as inline even though the path is also set (and missing):
        // proof that the JSON, not the path, was consulted.
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FirebaseAppFactory.GetOrCreate(new FirebaseOptions
            {
                ServiceAccountJson = "not json",
                ServiceAccountPath = Path.Combine(_dir, "no-such.json"),
            }));

        Assert.Contains("Firebase:ServiceAccountJson", ex.Message);
        Assert.DoesNotContain("no-such.json", ex.Message);
    }

    [Theory]
    [InlineData("not json at all — SECRET-MARKER")]
    [InlineData("""{ "type": "authorized_user", "client_secret": "SECRET-MARKER" }""")]
    public void Firebase_invalid_inline_json_does_not_echo_itself_anywhere(string json)
    {
        var ex = Assert.Throws<InvalidOperationException>(() =>
            FirebaseAppFactory.GetOrCreate(new FirebaseOptions { ServiceAccountJson = json }));

        Assert.DoesNotContain("SECRET-MARKER", ex.ToString());
        Assert.Null(ex.InnerException);
    }
}
