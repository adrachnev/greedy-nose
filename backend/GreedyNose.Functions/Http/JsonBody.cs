using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace GreedyNose.Functions.Http;

/// <summary>
/// Reads an optional JSON body the way the minimal-API binder did for a nullable parameter: no body is
/// <c>null</c> (the handler's own validation then answers 400), a body that is not JSON is 415, broken
/// JSON is 400. The size cap replaces the Kestrel request limit, which does not reach a Function: the
/// body is read at most one byte past the cap and refused with 413, so nobody makes us buffer megabytes.
/// </summary>
public static class JsonBody
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    /// <summary>Rules and other app payloads: far above anything the app sends, still bounded.</summary>
    public const int DefaultMaxBytes = 1_000_000;

    public readonly record struct Result<T>(T? Value, IResult? Error) where T : class;

    public static async Task<Result<T>> ReadAsync<T>(HttpRequest request, int maxBytes, CancellationToken ct) where T : class
    {
        if (request.ContentLength > maxBytes)
        {
            return new(null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > maxBytes)
            {
                return new(null, Results.StatusCode(StatusCodes.Status413PayloadTooLarge));
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return new(null, null);
        }

        if (!request.HasJsonContentType())
        {
            return new(null, Results.StatusCode(StatusCodes.Status415UnsupportedMediaType));
        }

        try
        {
            return new(JsonSerializer.Deserialize<T>(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), Options), null);
        }
        catch (JsonException)
        {
            return new(null, Results.Problem(statusCode: StatusCodes.Status400BadRequest, title: "The request body is not valid JSON."));
        }
    }
}
