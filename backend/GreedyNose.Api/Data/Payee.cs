namespace GreedyNose.Api.Data;

/// <summary>Whoever took the money (R3), as <c>TransactionMapper</c> identified them.</summary>
public sealed class Payee
{
    public Guid UserId { get; set; }

    /// <summary>
    /// The mapper's payee key, stored verbatim — <c>iban:DE89…</c>, <c>name:LIDL CONNECT</c> or
    /// <c>unknown</c>. Text, not a generated id: it is the identity R3a defines, and rules and
    /// debits from the app refer to it by exactly this string.
    /// </summary>
    public required string Id { get; set; }

    public required string Name { get; set; }

    public required string Initials { get; set; }

    /// <summary>Empty when the bank sent none, which is the common case for card payments — same as <c>PayeeDto.Iban</c>.</summary>
    public string Iban { get; set; } = "";

    public DateTimeOffset FirstSeenAt { get; set; }
}
