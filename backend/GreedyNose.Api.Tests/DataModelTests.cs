using GreedyNose.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;

namespace GreedyNose.Api.Tests;

/// <summary>
/// Pins the parts of the schema that carry a guarantee, straight from the EF model — no database.
/// Each one exists because loosening it would not fail loudly: a cascade that erases the R11 guard
/// looks like it works until a charge notifies twice, and a dropped limit check looks like it works
/// until a zero limit mutes nothing.
/// </summary>
public class DataModelTests
{
    // No connection is opened: building the model never touches the server.
    private static GreedyNoseDbContext NewContext() => new(
        new DbContextOptionsBuilder<GreedyNoseDbContext>().UseNpgsql("Host=localhost;Database=unused").Options);

    private static IModel DesignTimeModel(GreedyNoseDbContext context) =>
        context.GetService<IDesignTimeModel>().Model;

    [Fact]
    public void Only_the_user_foreign_keys_cascade()
    {
        using var context = NewContext();
        var foreignKeys = DesignTimeModel(context).GetEntityTypes().SelectMany(e => e.GetForeignKeys()).ToList();

        var toUser = foreignKeys.Where(fk => fk.PrincipalEntityType.ClrType == typeof(User)).ToList();
        var crossTable = foreignKeys.Except(toUser).ToList();

        // Guards against this passing vacuously: rule → payee, debit → payee, log → debit.
        Assert.Equal(5, toUser.Count);
        Assert.Equal(3, crossTable.Count);

        // Deleting the user must still empty everything (account deletion is a real delete)…
        Assert.All(toUser, fk => Assert.Equal(DeleteBehavior.Cascade, fk.DeleteBehavior));

        // …but deleting a parent alone must be refused. A cascade here would let deleting a debit
        // erase its NotificationLog row — R11's guard — and a delete-and-reinsert payee upsert wipe the rule.
        Assert.All(crossTable, fk => Assert.Equal(DeleteBehavior.NoAction, fk.DeleteBehavior));
    }

    [Fact]
    public void One_notification_per_debit_is_enforced_by_a_unique_index()
    {
        using var context = NewContext();
        var entry = DesignTimeModel(context).FindEntityType(typeof(NotificationLogEntry))!;

        var index = Assert.Single(
            entry.GetIndexes(),
            i => i.Properties.Select(p => p.Name).SequenceEqual([nameof(NotificationLogEntry.UserId), nameof(NotificationLogEntry.DebitId)]));

        Assert.True(index.IsUnique);
    }

    [Fact]
    public void Rules_carry_check_constraints_for_classification_and_a_positive_limit()
    {
        using var context = NewContext();
        var rule = DesignTimeModel(context).FindEntityType(typeof(Rule))!;
        var constraints = rule.GetCheckConstraints().ToDictionary(c => c.Name!, c => c.Sql);

        // Spelled out here, not built from ClassificationText: the point is to pin what the database
        // actually holds, so a change to the constants would have to be a visible decision.
        Assert.Equal("\"Classification\" IN ('good', 'bad')", constraints["CK_Rules_Classification"]);
        Assert.Equal("\"AmountEUR\" IS NULL OR \"AmountEUR\" > 0", constraints["CK_Rules_AmountEUR_Positive"]);
    }

    /// <summary>
    /// A change to the model without a matching migration is otherwise found only when someone runs
    /// <c>dotnet ef</c> — or not at all, until the code and the database disagree in production.
    /// Compares the model with the migrations snapshot; needs no database connection.
    /// </summary>
    [Fact]
    public void The_model_matches_the_latest_migration()
    {
        using var context = NewContext();

        Assert.False(
            context.Database.HasPendingModelChanges(),
            "The model has changes no migration covers. Run `dotnet ef migrations add <Name> --project GreedyNose.Api`.");
    }
}
