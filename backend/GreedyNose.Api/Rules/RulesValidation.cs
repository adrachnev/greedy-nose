using System.Diagnostics.CodeAnalysis;
using GreedyNose.Api.Domain;

namespace GreedyNose.Api.Rules;

/// <summary>
/// A <c>POST /rules</c> body, validated: every field <see cref="RulesEndpoint.HandleAsync"/> needs,
/// guaranteed non-blank/well-formed. <see cref="Iban"/> defaults to <c>""</c> when the request sent
/// none, matching <c>Data.Payee.Iban</c>'s own default.
/// </summary>
public sealed record ValidatedRule(
    string PayeeId,
    Classification Classification,
    decimal? AmountEUR,
    string Name,
    string Initials,
    string Iban);

/// <summary>
/// What <c>POST /rules</c> accepts, as a pure function — no database, no HTTP — mirroring
/// <c>Notifications/DeviceTokenValidation.cs</c>'s shape for a request with several fields instead of
/// one: every problem is collected rather than stopping at the first, since
/// <c>Results.ValidationProblem</c> already knows how to report more than one field at a time.
/// </summary>
public static class RulesValidation
{
    public static bool TryValidate(
        RulesRequest? request,
        [NotNullWhen(true)] out ValidatedRule? validated,
        out IDictionary<string, string[]> problems)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request?.PayeeId))
        {
            errors["payeeId"] = ["A payee id is required."];
        }

        if (string.IsNullOrWhiteSpace(request?.Name))
        {
            errors["name"] = ["A name is required."];
        }

        if (string.IsNullOrWhiteSpace(request?.Initials))
        {
            errors["initials"] = ["Initials are required."];
        }

        // `request is null` short-circuits before Classification is read. Deliberately
        // ClassificationText.TryParse, never .Parse: .Parse's FormatException is correct for a
        // corrupt stored value (ClassificationConverter), wrong for a client's bad request body,
        // which must never turn into a 500.
        var classification = Classification.Good;
        if (request is null || !ClassificationText.TryParse(request.Classification, out classification))
        {
            errors["classification"] = [$"classification must be '{ClassificationText.Good}' or '{ClassificationText.Bad}'."];
        }

        // Mirrors CK_Rules_AmountEUR_Positive (GreedyNoseDbContext): null (no limit, R4a) or
        // strictly positive. Validated here too rather than left to the database, so a bad value is
        // a 400 instead of a 500 from a check-constraint violation.
        if (request?.AmountEUR is <= 0)
        {
            errors["amountEUR"] = ["amountEUR must be greater than 0 when present."];
        }

        if (errors.Count > 0)
        {
            validated = null;
            problems = errors;
            return false;
        }

        validated = new ValidatedRule(
            request!.PayeeId!,
            classification,
            request.AmountEUR,
            request.Name!,
            request.Initials!,
            request.Iban ?? "");
        problems = errors;
        return true;
    }
}
