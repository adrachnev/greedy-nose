namespace GreedyNose.Api.EnableBanking;

/// <summary>
/// Chooses the one account to watch when a bank's consent returns several (v1 = one account per bank, R22a).
/// DKB returned five; the position says nothing about which one is the current account. The type alone does not
/// either: DKB marks every account CACC, Tagesgeld included, so the product name has to separate them.
/// </summary>
public static class AccountSelector
{
    /// <summary>ISO 20022 cash account type for a current account, as Enable Banking reports it.</summary>
    private const string CurrentAccountType = "CACC";

    /// <summary>
    /// The product name of the current account at DKB; exact, so "Girokonto u18" does not match. It applies to every
    /// bank that returns several accounts: a bank with another product word is refused, never guessed.
    /// </summary>
    private const string CurrentAccountProduct = "Girokonto";

    /// <summary>
    /// One account: that one, whatever its type or product. Several: the single one typed CACC and with the product
    /// "Girokonto" (both case-insensitive, trimmed, exact). Zero or several eligible: null — nothing is guessed
    /// (no fallback to position), the caller refuses.
    /// </summary>
    public static ConnectedAccount? Pick(IReadOnlyList<ConnectedAccount> accounts)
    {
        if (accounts.Count == 1)
        {
            return accounts[0];
        }

        var eligible = accounts
            .Where(a => Matches(a.CashAccountType, CurrentAccountType) && Matches(a.Product, CurrentAccountProduct))
            .Take(2)
            .ToList();

        return eligible.Count == 1 ? eligible[0] : null;
    }

    private static bool Matches(string? value, string expected) =>
        string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
}
