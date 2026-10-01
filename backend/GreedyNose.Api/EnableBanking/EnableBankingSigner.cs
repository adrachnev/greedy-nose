using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace GreedyNose.Api.EnableBanking;

/// <summary>
/// Mints the RS256 JWT every Enable Banking request carries. Their API has no token endpoint and
/// no refresh flow: the client signs its own bearer token with the application's private key.
///
/// Header : { "typ": "JWT", "alg": "RS256", "kid": "&lt;application-id&gt;" }
/// Body   : { "iss": "enablebanking.com", "aud": "api.enablebanking.com", "iat": …, "exp": … }
///
/// The key is loaded once at startup and reused — reading a 4096-bit key per request would be
/// the slowest thing in the path.
/// </summary>
public sealed class EnableBankingSigner : IDisposable
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    private readonly EnableBankingOptions _options;
    private readonly RSA _key;

    public EnableBankingSigner(EnableBankingOptions options)
    {
        _options = options;

        if (string.IsNullOrWhiteSpace(options.ApplicationId))
        {
            throw new InvalidOperationException(
                "EnableBanking:ApplicationId is not configured. See TRACER-01-BANK-DATA.md, step 1.");
        }

        if (!File.Exists(options.PrivateKeyPath))
        {
            throw new InvalidOperationException(
                $"Enable Banking private key not found at '{options.PrivateKeyPath}'. " +
                "Set EnableBanking:PrivateKeyPath via dotnet user-secrets.");
        }

        _key = RSA.Create();
        _key.ImportFromPem(File.ReadAllText(options.PrivateKeyPath));
    }

    public string CreateToken(DateTimeOffset now)
    {
        var header = new Dictionary<string, object>
        {
            ["typ"] = "JWT",
            ["alg"] = "RS256",
            ["kid"] = _options.ApplicationId,
        };

        var body = new Dictionary<string, object>
        {
            ["iss"] = "enablebanking.com",
            ["aud"] = "api.enablebanking.com",
            ["iat"] = now.ToUnixTimeSeconds(),
            ["exp"] = now.Add(_options.TokenLifetime).ToUnixTimeSeconds(),
        };

        var signingInput = $"{Encode(header)}.{Encode(body)}";
        var signature = _key.SignData(
            Encoding.UTF8.GetBytes(signingInput), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);

        return $"{signingInput}.{Base64Url.EncodeToString(signature)}";
    }

    private static string Encode(Dictionary<string, object> part) =>
        Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(part, Json));

    public void Dispose() => _key.Dispose();
}
