namespace GreedyNose.Api.EnableBanking;

// Request bodies for the consent flow. Property names are PascalCase here and become snake_case
// on the wire through EnableBankingClient.Json's naming policy — ValidUntil → valid_until.

/// <summary>What the consent covers and for how long. The bullet asks for both scopes.</summary>
public sealed record AccessRequest(string ValidUntil, bool Balances, bool Transactions);

/// <summary>Which bank to authorize against. Name and country as returned by GET /aspsps.</summary>
public sealed record AspspRef(string Name, string Country);

/// <summary>POST /auth — starts authorization and returns the URL to send the user to.</summary>
public sealed record AuthRequest(
    AccessRequest Access,
    AspspRef Aspsp,
    string State,
    string RedirectUrl,
    string PsuType);

/// <summary>POST /sessions — exchanges the code from the redirect for a session.</summary>
public sealed record SessionRequest(string Code);
