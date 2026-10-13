namespace GreedyNose.Api.EnableBanking;

/// <summary>
/// Chooses the one account to watch when a bank's consent returns several (v1 = one account per bank, R22a).
/// DKB returned five; the position says nothing about which one is the current account, the type does.
/// </summary>
public static class AccountSelector
{
    /// <summary>ISO 20022 cash account type for a current account, as Enable Banking reports it.</summary>
    private const string CurrentAccountType = "CACC";

    /// <summary>
    /// One account: that one, whatever its type. Several: the single one typed CACC (case-insensitive).
    /// Zero or several CACC: null — nothing is guessed (no fallback to product or position), the caller refuses.
    /// </summary>
    public static ConnectedAccount? Pick(IReadOnlyList<ConnectedAccount> accounts)
    {
        if (accounts.Count == 1)
        {
            return accounts[0];
        }

        var current = accounts
            .Where(a => string.Equals(a.CashAccountType?.Trim(), CurrentAccountType, StringComparison.OrdinalIgnoreCase))
            .Take(2)
            .ToList();

        return current.Count == 1 ? current[0] : null;
    }
}
