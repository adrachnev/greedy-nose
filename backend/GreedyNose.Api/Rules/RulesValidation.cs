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

    /// <summary>The most rules one <c>POST /rules/batch</c> takes (bead gn-5g6); far above what the app holds.</summary>
    public const int MaxBatchItems = 2000;

    /// <summary>
    /// A <c>POST /rules/batch</c> body: 1 to <see cref="MaxBatchItems"/> items, each valid by
    /// <see cref="TryValidate"/>, no payee id twice. Problems are keyed by item (<c>rules[3].classification</c>),
    /// and any one makes the whole batch invalid — nothing is applied.
    /// </summary>
    public static bool TryValidateBatch(
        RulesBatchRequest? request,
        [NotNullWhen(true)] out IReadOnlyList<ValidatedRule>? validated,
        out IDictionary<string, string[]> problems)
    {
        var errors = new Dictionary<string, string[]>();
        var items = request?.Rules;

        if (items is null || items.Count == 0)
        {
            errors["rules"] = ["At least one rule is required."];
        }
        else if (items.Count > MaxBatchItems)
        {
            errors["rules"] = [$"At most {MaxBatchItems} rules per request."];
        }
        else
        {
            var result = new List<ValidatedRule>(items.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 0; i < items.Count; i++)
            {
                if (!TryValidate(items[i], out var rule, out var itemProblems))
                {
                    foreach (var (field, messages) in itemProblems)
                    {
                        errors[$"rules[{i}].{field}"] = messages;
                    }
                }
                else if (!seen.Add(rule.PayeeId))
                {
                    errors[$"rules[{i}].payeeId"] = ["The same payee id appears twice in one batch."];
                }
                else
                {
                    result.Add(rule);
                }
            }

            if (errors.Count == 0)
            {
                validated = result;
                problems = errors;
                return true;
            }
        }

        validated = null;
        problems = errors;
        return false;
    }
}
