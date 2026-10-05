using System.Text.Json;
using GreedyNose.Api.Auth;
using GreedyNose.Api.Data;
using GreedyNose.Api.Debits;
using GreedyNose.Api.EnableBanking;
using GreedyNose.Api.Ingestion;
using GreedyNose.Api.Consent;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;

namespace GreedyNose.Api.Tests;

/// <summary>Story 1.6: <c>GET /debits</c> reads stored rows and makes no bank call.</summary>
public class DebitsEndpointTests
{
    private static GreedyNoseDbContext NewInMemoryContext(string databaseName) =>
        new(new DbContextOptionsBuilder<GreedyNoseDbContext>().UseInMemoryDatabase(databaseName).Options);

    private static Payee NewPayee(Guid userId, string id, string name = "Shop", string iban = "") => new()
    {
        UserId = userId, Id = id, Name = name, Initials = "SH", Iban = iban,
    };

    private static Debit NewDebit(
        Guid userId, string id, string payeeId, DateTimeOffset timestamp,
        string accountKey = "DE00", bool hasTime = false, decimal amount = 12.34m, string reference = "") => new()
    {
        UserId = userId, Id = id, PayeeId = payeeId, AmountEUR = amount, Timestamp = timestamp,
        HasTime = hasTime, PaymentType = "Card payment", Reference = reference, AccountKey = accountKey,
    };

    private static async Task<DebitsDto> CallAsync(string dbName, ICurrentUser user)
    {
        await using var db = NewInMemoryContext(dbName);
        var result = await DebitsEndpoint.HandleAsync(user, db, CancellationToken.None);
        var ok = Assert.IsType<Ok<DebitsDto>>(result);
        return ok.Value!;
    }

    [Fact]
    public async Task Stored_rows_come_back_in_the_wire_shape_newest_first()
    {
        var dbName = Guid.NewGuid().ToString();
        var owner = TestUsers.Owner.UserId;
        await using (var seed = NewInMemoryContext(dbName))
        {
            seed.Payees.Add(NewPayee(owner, "iban:DE11", "Lidl", "DE11"));
            seed.Debits.AddRange(
                NewDebit(owner, "old", "iban:DE11", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)),
                NewDebit(owner, "new", "iban:DE11", new DateTimeOffset(2026, 9, 3, 8, 15, 30, TimeSpan.Zero),
                    hasTime: true, amount: 5m, reference: "ref"));
            await seed.SaveChangesAsync();
        }

        var dto = await CallAsync(dbName, TestUsers.Owner);

        Assert.Equal(["new", "old"], dto.Debits.Select(d => d.Id));
        Assert.Equal(0, dto.Skipped);
        var payee = Assert.Single(dto.Payees);
        Assert.Equal(("iban:DE11", "Lidl", "SH", "DE11"), (payee.Id, payee.Name, payee.Initials, payee.Iban));
        Assert.Equal("", payee.CreditorAgent);
        Assert.Equal("", payee.CreditorName);
        var newest = dto.Debits[0];
        Assert.Equal("iban:DE11", newest.PayeeId);
        Assert.Equal(5m, newest.AmountEUR);
        Assert.True(newest.HasTime);
        Assert.Equal("Card payment", newest.PaymentType);
        Assert.Equal("ref", newest.Reference);

        // The JSON property names are what app/src/data/backendFeed.ts validates.
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(dto, EnableBankingClient.Json));
        var root = json.RootElement;
        Assert.Equal(
            ["payees", "debits", "skipped"],
            root.EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["id", "payeeId", "amountEUR", "timestamp", "hasTime", "paymentType", "reference"],
            root.GetProperty("debits")[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal(
            ["id", "name", "initials", "iban"],
            root.GetProperty("payees")[0].EnumerateObject().Select(p => p.Name));
    }

    [Fact]
    public async Task Debits_at_the_same_instant_keep_a_stable_order()
    {
        var dbName = Guid.NewGuid().ToString();
        var owner = TestUsers.Owner.UserId;
        var at = new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await using (var seed = NewInMemoryContext(dbName))
        {
            seed.Payees.Add(NewPayee(owner, "name:A"));
            seed.Debits.AddRange(
                NewDebit(owner, "c", "name:A", at),
                NewDebit(owner, "a", "name:A", at),
                NewDebit(owner, "b", "name:A", at));
            await seed.SaveChangesAsync();
        }

        var first = await CallAsync(dbName, TestUsers.Owner);
        var second = await CallAsync(dbName, TestUsers.Owner);

        Assert.Equal(["a", "b", "c"], first.Debits.Select(d => d.Id));
        Assert.Equal(first.Debits.Select(d => d.Id), second.Debits.Select(d => d.Id));
    }

    [Fact]
    public async Task Two_accounts_are_merged_into_one_date_ordered_list()
    {
        var dbName = Guid.NewGuid().ToString();
        var owner = TestUsers.Owner.UserId;
        await using (var seed = NewInMemoryContext(dbName))
        {
            seed.Payees.AddRange(NewPayee(owner, "name:A"), NewPayee(owner, "name:B"));
            seed.Debits.AddRange(
                NewDebit(owner, "n26-1", "name:A", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero), accountKey: "DE_N26"),
                NewDebit(owner, "dkb-1", "name:B", new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero), accountKey: "DE_DKB"),
                NewDebit(owner, "n26-2", "name:A", new DateTimeOffset(2026, 9, 3, 0, 0, 0, TimeSpan.Zero), accountKey: "DE_N26"));
            await seed.SaveChangesAsync();
        }

        var dto = await CallAsync(dbName, TestUsers.Owner);

        Assert.Equal(["n26-2", "dkb-1", "n26-1"], dto.Debits.Select(d => d.Id));
        Assert.Equal(2, dto.Payees.Count);
    }

    [Fact]
    public async Task Another_users_rows_and_payees_without_debits_are_not_returned()
    {
        var dbName = Guid.NewGuid().ToString();
        var owner = TestUsers.Owner.UserId;
        var other = Guid.NewGuid();
        await using (var seed = NewInMemoryContext(dbName))
        {
            seed.Payees.AddRange(
                NewPayee(owner, "name:MINE"),
                NewPayee(owner, "name:NO-DEBIT"),
                NewPayee(other, "name:MINE", "Other user's copy"),
                NewPayee(other, "name:THEIRS"));
            seed.Debits.AddRange(
                NewDebit(owner, "mine", "name:MINE", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)),
                NewDebit(other, "theirs", "name:THEIRS", new DateTimeOffset(2026, 9, 2, 0, 0, 0, TimeSpan.Zero)));
            await seed.SaveChangesAsync();
        }

        var dto = await CallAsync(dbName, TestUsers.Owner);

        Assert.Equal(["mine"], dto.Debits.Select(d => d.Id));
        var payee = Assert.Single(dto.Payees);
        Assert.Equal("name:MINE", payee.Id);
        Assert.Equal("Shop", payee.Name);

        var asOther = await CallAsync(dbName, new TestUsers.FixedCurrentUser(other));
        Assert.Equal(["theirs"], asOther.Debits.Select(d => d.Id));
    }

    [Fact]
    public async Task Nothing_stored_answers_200_with_empty_lists()
    {
        var dto = await CallAsync(Guid.NewGuid().ToString(), TestUsers.Owner);

        Assert.Empty(dto.Debits);
        Assert.Empty(dto.Payees);
        Assert.Equal(0, dto.Skipped);
    }

    // Checks only HandleAsync's parameter types; the real "no bank request" proof is a live check.
    [Fact]
    public void The_handler_signature_has_no_bank_dependency()
    {
        var parameterTypes = typeof(DebitsEndpoint)
            .GetMethod(nameof(DebitsEndpoint.HandleAsync))!
            .GetParameters()
            .Select(p => p.ParameterType)
            .ToList();

        Assert.DoesNotContain(typeof(EnableBankingClient), parameterTypes);
        Assert.DoesNotContain(typeof(ConsentStore), parameterTypes);
        Assert.DoesNotContain(typeof(IDebitsFetcher), parameterTypes);
    }

    [Fact]
    public async Task A_debit_stored_after_a_call_appears_on_the_next_call()
    {
        var dbName = Guid.NewGuid().ToString();
        var owner = TestUsers.Owner.UserId;
        Assert.Empty((await CallAsync(dbName, TestUsers.Owner)).Debits);

        await using (var worker = NewInMemoryContext(dbName))
        {
            worker.Payees.Add(NewPayee(owner, "name:A"));
            worker.Debits.Add(NewDebit(owner, "fresh", "name:A", new DateTimeOffset(2026, 9, 1, 0, 0, 0, TimeSpan.Zero)));
            await worker.SaveChangesAsync();
        }

        Assert.Equal(["fresh"], (await CallAsync(dbName, TestUsers.Owner)).Debits.Select(d => d.Id));
    }

    [Theory]
    [InlineData("2026-08-18", false)]
    [InlineData("2026-08-18T14:05:09Z", true)]
    public async Task Timestamp_string_matches_what_the_mapper_produces(string bookingDate, bool hasTime)
    {
        var mapped = TransactionMapper.Map(
            new EbTransactionsResponse(
                [new EbTransaction(
                    EntryReference: "e1",
                    TransactionAmount: new EbAmount("EUR", "9.99"),
                    Creditor: new EbParty("Shop"),
                    CreditorAccount: null,
                    CreditorAgent: null,
                    BankTransactionCode: null,
                    CreditDebitIndicator: "DBIT",
                    Status: "BOOK",
                    BookingDate: bookingDate,
                    ValueDate: null,
                    TransactionDate: null,
                    RemittanceInformation: null)],
                null),
            "DE00");
        var fromMapper = Assert.Single(mapped.Payload.Debits);
        Assert.Equal(hasTime, fromMapper.HasTime);

        var dbName = Guid.NewGuid().ToString();
        var owner = TestUsers.Owner.UserId;
        await using (var seed = NewInMemoryContext(dbName))
        {
            seed.Payees.Add(NewPayee(owner, fromMapper.PayeeId));
            seed.Debits.Add(NewDebit(
                owner, fromMapper.Id, fromMapper.PayeeId,
                DateTimeOffset.Parse(fromMapper.Timestamp, System.Globalization.CultureInfo.InvariantCulture),
                hasTime: fromMapper.HasTime));
            await seed.SaveChangesAsync();
        }

        var stored = Assert.Single((await CallAsync(dbName, TestUsers.Owner)).Debits);
        Assert.Equal(fromMapper.Timestamp, stored.Timestamp);
    }
}
