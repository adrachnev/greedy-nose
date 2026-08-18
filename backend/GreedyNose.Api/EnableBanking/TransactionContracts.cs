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
    [property: JsonPropertyName("iban")] string Iban);

public sealed record DebitDto(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("payeeId")] string PayeeId,
    [property: JsonPropertyName("amountEUR")] decimal AmountEUR,
    [property: JsonPropertyName("timestamp")] string Timestamp,
    [property: JsonPropertyName("paymentType")] string PaymentType,
    [property: JsonPropertyName("reference")] string Reference);

public sealed record DebitsDto(
    [property: JsonPropertyName("payees")] IReadOnlyList<PayeeDto> Payees,
    [property: JsonPropertyName("debits")] IReadOnlyList<DebitDto> Debits);

/// <summary>
/// What the mapper produced, plus the charges it refused to produce. <c>Skipped</c> exists because
/// silently dropping a charge is the one failure mode this product cannot have: the caller logs
/// every entry, and step 7 counts them against real data before deciding what the real fix is.
/// It stays out of <see cref="DebitsDto"/> — the app is not the audience for it.
/// </summary>
public sealed record MappedDebits(DebitsDto Payload, IReadOnlyList<string> Skipped);
