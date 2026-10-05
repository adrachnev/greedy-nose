using GreedyNose.Api.Auth;
using GreedyNose.Api.Data;
using GreedyNose.Api.Rules;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace GreedyNose.Api.Tests;

/// <summary>Test doubles for <see cref="ICurrentUser"/>.</summary>
internal static class TestUsers
{
    public static ICurrentUser Owner { get; } = new FixedCurrentUser(SeedData.UserId);

    public sealed class FixedCurrentUser(Guid userId) : ICurrentUser
    {
        public Guid UserId { get; } = userId;
    }
}

/// <summary>
/// Story 1.5: endpoints take their user from <see cref="ICurrentUser"/>, never from
/// <see cref="SeedData.UserId"/>. A different user id must end up on every row the endpoint writes.
/// </summary>
public class CurrentUserTests
{
    private static GreedyNoseDbContext NewInMemoryContext(string databaseName) =>
        new(new DbContextOptionsBuilder<GreedyNoseDbContext>().UseInMemoryDatabase(databaseName).Options);

    [Fact]
    public void OwnerCurrentUser_returns_the_seeded_owner()
    {
        Assert.Equal(SeedData.UserId, new OwnerCurrentUser().UserId);
    }

    [Fact]
    public async Task A_saved_rule_and_its_payee_belong_to_the_current_user()
    {
        var dbName = Guid.NewGuid().ToString();
        var other = Guid.NewGuid();
        Assert.NotEqual(SeedData.UserId, other);

        await using (var db = NewInMemoryContext(dbName))
        {
            var result = await RulesEndpoint.HandleAsync(
                new RulesRequest("iban:DE00", "bad", null, "Shop", "SH", "DE00"),
                db, new TestUsers.FixedCurrentUser(other), TimeProvider.System, NullLoggerFactory.Instance, CancellationToken.None);
            Assert.IsType<NoContent>(result);
        }

        await using var verify = NewInMemoryContext(dbName);
        Assert.Equal(1, await verify.Payees.CountAsync(p => p.UserId == other && p.Id == "iban:DE00"));
        Assert.Equal(1, await verify.Rules.CountAsync(r => r.UserId == other && r.PayeeId == "iban:DE00"));
        Assert.Equal(0, await verify.Payees.CountAsync(p => p.UserId != other));
        Assert.Equal(0, await verify.Rules.CountAsync(r => r.UserId != other));
    }

    // No device-token test here: its refresh step is ExecuteUpdateAsync, which EF Core's in-memory
    // provider does not support (DeviceTokenTests leaves the upsert to real Postgres for the same reason).
}
