using System.Text.Json.Serialization;

namespace GreedyNose.Api.EnableBanking;

// Enable Banking's transaction shape, only the parts step 4 maps. Property names are PascalCase
// and match snake_case on the wire through EnableBankingClient.Json's naming policy
// (EntryReference → entry_reference). Unmatched properties are ignored, so fields we do not read
// yet — exchange_rate, balance_after_transaction, the *_additional_identification pair — cost
// nothing by being absent here.
//
// Everything is nullable on purpose. The first real dump (raw/transactions-20260818-122928.json)
// had entry_reference, transaction_id, creditor_agent and merchant_category_code null on all 100
// rows, and creditor.name missing on two debits. Optimism about a bank field is how a mapper
// starts throwing on a Tuesday.

public sealed record EbAmount(string? Currency, string? Amount);

public sealed record EbParty(string? Name);

public sealed record EbAccountId(string? Iban);

/// <summary>The creditor's bank. Null throughout the first dump; declared so R3a's tier 2 can use
/// it the moment a bank does send one.</summary>
public sealed record EbAgent(string? BicFi, string? Name);

/// <summary>ISO 20022 domain/family/sub-family, e.g. PMNT / CCRD / POSD for a card purchase.</summary>
public sealed record EbBankTransactionCode(string? Description, string? Code, string? SubCode);

public sealed record EbTransaction(
    string? EntryReference,
    EbAmount? TransactionAmount,
    EbParty? Creditor,
    EbAccountId? CreditorAccount,
    EbAgent? CreditorAgent,
    EbBankTransactionCode? BankTransactionCode,
    string? CreditDebitIndicator,
    string? Status,
    string? BookingDate,
    string? ValueDate,
    string? TransactionDate,
    IReadOnlyList<string>? RemittanceInformation);

public sealed record EbTransactionsResponse(
    IReadOnlyList<EbTransaction>? Transactions,
    string? ContinuationKey);

// --- What we hand to the app ------------------------------------------------------------------

// These mirror app/src/domain/model.ts exactly — it is the contract, and this is the other half
// of it. Classification is deliberately absent: R6 says the client derives it from the current
// rule, so a debit that arrives here carries no verdict, only facts.

public sealed record PayeeDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("initials")] string Initials,
    [property: JsonPropertyName("iban")] string Iban,
    // Internal only, [JsonIgnore]'d on purpose: app/src/domain/model.ts has no such field and R3a's
    // matching key never reads it (ResolvePayeeKey resolves the agent from the transaction directly
    // and is unchanged). Exists so BuildPayee can hand IngestionRunner's Payee.RecordSeen the same
    // "distinct set" raw material ARCHITECTURE.md's "Payee identity" section asks for — see
    // Data/Payee.cs's CreditorAgentsSeen. Breaking this out keeps the wire contract to /debits
    // exactly what it was before this change.
    [property: JsonIgnore] string CreditorAgent,
    // Internal only, [JsonIgnore]'d, same reasoning as CreditorAgent above — and the same fix as
    // that field's own addition, one review round later: the bank's raw creditor name
    // (transaction.Creditor?.Name, exactly what ResolvePayeeKey's tier-2 key itself normalizes),
    // never Name above, which BuildPayee may have substituted with a display fallback (the raw
    // IBAN, or "Unknown payee") when the bank sent no creditor name at all. Feeding that fallback
    // into NormalizedNamesSeen instead of this field mangled an IBAN into a meaningless
    // digit-stripped fragment — see IngestionRunner.UpsertPayeesAsync's own call site (2026-09-28
    // review, Finding 1).
    [property: JsonIgnore] string CreditorName);

public sealed record DebitDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("payeeId")] string PayeeId,
    [property: JsonPropertyName("amountEUR")] decimal AmountEUR,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    // Whether the bank booked to a moment or only to a day. False on all 100 rows of the first
    // dump — booking_date is a plain date and transaction_date was null throughout — so the
    // timestamp's 00:00:00Z is padding, not information, and the client must not print it as a
    // time. Named positively so an absent field reads as "no time", which is the safe direction:
    // hiding a time we never had beats inventing midnight.
    [property: JsonPropertyName("hasTime")] bool HasTime,
    [property: JsonPropertyName("paymentType")] string PaymentType,
    [property: JsonPropertyName("reference")] string Reference);

public sealed record DebitsDto(
    [property: JsonPropertyName("payees")] IReadOnlyList<PayeeDto> Payees,
    [property: JsonPropertyName("debits")] IReadOnlyList<DebitDto> Debits,
    // How many charges the mapper refused to represent — a count, never the detail. An empty list
    // has two very different causes ("you have no charges" and "we could not read your charges"),
    // and R19's whole point is that the app must never present the second as the first. The
    // detail strings name merchants and amounts and stay server-side; see MappedDebits.Skipped.
    [property: JsonPropertyName("skipped")] int Skipped);

/// <summary>
/// What the mapper produced, plus the charges it refused to produce. <c>Skipped</c> exists because
/// silently dropping a charge is the one failure mode this product cannot have: the caller logs
/// every entry, and step 7 counts them against real data before deciding what the real fix is.
///
/// The <em>strings</em> stay out of <see cref="DebitsDto"/> — they name merchants and amounts, and
/// the app is not their audience. Only their count travels, as <see cref="DebitsDto.Skipped"/>,
/// because "we dropped some of your charges" is something the user has to be able to find out.
/// </summary>
public sealed record MappedDebits(DebitsDto Payload, IReadOnlyList<string> Skipped);
