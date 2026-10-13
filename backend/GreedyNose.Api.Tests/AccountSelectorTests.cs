using GreedyNose.Api.EnableBanking;

namespace GreedyNose.Api.Tests;

public sealed class AccountSelectorTests
{
    private static ConnectedAccount Account(string uid, string? type) =>
        new(uid, $"DE{uid}", "Name", "EUR", null, CashAccountType: type);

    [Theory]
    [InlineData("CACC")]
    [InlineData("SVGS")]
    [InlineData(null)]
    public void One_account_is_chosen_whatever_its_type(string? type)
    {
        var only = Account("1", type);

        Assert.Same(only, AccountSelector.Pick([only]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void The_single_current_account_is_found_in_any_position(int position)
    {
        var accounts = Enumerable.Range(0, 5).Select(i => Account(i.ToString(), i == position ? "CACC" : "SVGS")).ToList();

        Assert.Same(accounts[position], AccountSelector.Pick(accounts));
    }

    [Fact]
    public void The_type_is_compared_case_insensitively()
    {
        var current = Account("2", " cacc ");

        Assert.Same(current, AccountSelector.Pick([Account("1", "CARD"), current]));
    }

    [Fact]
    public void No_current_account_chooses_nothing()
    {
        Assert.Null(AccountSelector.Pick([Account("1", "SVGS"), Account("2", "CARD")]));
    }

    [Fact]
    public void Several_current_accounts_choose_nothing()
    {
        Assert.Null(AccountSelector.Pick([Account("1", "CACC"), Account("2", "CACC"), Account("3", "SVGS")]));
    }

    [Fact]
    public void Missing_types_choose_nothing_when_there_are_several_accounts()
    {
        Assert.Null(AccountSelector.Pick([Account("1", null), Account("2", null)]));
    }

    [Fact]
    public void No_accounts_choose_nothing()
    {
        Assert.Null(AccountSelector.Pick([]));
    }
}
