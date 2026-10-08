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

        // The inline PEM text (cloud: a Key Vault reference in an app setting) wins over the path.
        string pem;
        if (!string.IsNullOrWhiteSpace(options.PrivateKey))
        {
            pem = NormalizeInlinePem(options.PrivateKey);
        }
        else if (string.IsNullOrWhiteSpace(options.PrivateKeyPath))
        {
            throw new InvalidOperationException(
                "No Enable Banking private key is configured. Set EnableBanking:PrivateKey (PEM text) " +
                "or EnableBanking:PrivateKeyPath (path to the PEM file).");
        }
        else if (!File.Exists(options.PrivateKeyPath))
        {
            throw new InvalidOperationException(
                $"Enable Banking private key not found at '{options.PrivateKeyPath}'. " +
                "Set EnableBanking:PrivateKeyPath via dotnet user-secrets, or EnableBanking:PrivateKey.");
        }
        else
        {
            pem = File.ReadAllText(options.PrivateKeyPath);
        }

        var key = RSA.Create();
        try
        {
            key.ImportFromPem(pem);
        }
        catch (Exception ex)
        {
            key.Dispose();
            // Type only, never chained: the parser was looking at private key material.
            throw new InvalidOperationException(
                $"The Enable Banking private key could not be read as a PEM key ({ex.GetType().Name}). " +
                "Check EnableBanking:PrivateKey / EnableBanking:PrivateKeyPath.");
        }

        _key = key;
    }


    /// <summary>
    /// Settings stores mangle multi-line values: surrounding whitespace/CRLF, wrapping quotes, or newlines
    /// flattened to a literal backslash-n. Undo all of that before parsing; never log the value.
    /// </summary>
    private static string NormalizeInlinePem(string value)
    {
        var pem = value.Trim();
        if (pem.Length >= 2 && (pem[0] == '"' || pem[0] == '\'') && pem[^1] == pem[0])
        {
            pem = pem[1..^1].Trim();
        }

        return pem.Contains('\n') ? pem : pem.Replace("\\r", "").Replace("\\n", "\n");
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
