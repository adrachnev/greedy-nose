using GreedyNose.Api.EnableBanking;

namespace GreedyNose.Api.Tests;

public sealed class AccountSelectorTests
{
    private static ConnectedAccount Account(string uid, string? type, string? product = null) =>
        new(uid, $"DE{uid}", "Name", "EUR", null, CashAccountType: type, Product: product);

    // The real DKB shape: every account is CACC, only the product tells them apart.
    private static readonly string[] DkbProducts = ["Tagesgeld", "Girokonto u18", "Girokonto", "Tagesgeld", "Girokonto u18"];

    [Theory]
    [InlineData("CACC", "Girokonto")]
    [InlineData("SVGS", "Tagesgeld")]
    [InlineData(null, null)]
    public void One_account_is_chosen_whatever_its_type_or_product(string? type, string? product)
    {
        var only = Account("1", type, product);

        Assert.Same(only, AccountSelector.Pick([only]));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(2)]
    [InlineData(4)]
    public void The_real_dkb_shape_chooses_the_girokonto_in_any_position(int position)
    {
        var accounts = Enumerable.Range(0, 5)
            .Select(i => Account(i.ToString(), "CACC", i == position ? "Girokonto" : DkbProducts[i == 2 ? 0 : i]))
            .ToList();

        Assert.Same(accounts[position], AccountSelector.Pick(accounts));
    }

    [Fact]
    public void Type_and_product_are_compared_case_insensitively_and_trimmed()
    {
        var current = Account("2", " cacc ", " GIROKONTO ");

        Assert.Same(current, AccountSelector.Pick([Account("1", "CACC", "Tagesgeld"), current]));
    }

    [Fact]
    public void The_real_dkb_shape_without_a_girokonto_chooses_nothing()
    {
        var accounts = new[] { "Tagesgeld", "Girokonto u18", "Tagesgeld", "Girokonto u18" }
            .Select((p, i) => Account(i.ToString(), "CACC", p)).ToList();

        Assert.Null(AccountSelector.Pick(accounts));
    }

    [Fact]
    public void Two_girokonto_accounts_choose_nothing()
    {
        Assert.Null(AccountSelector.Pick([Account("1", "CACC", "Girokonto"), Account("2", "CACC", "girokonto"), Account("3", "CACC", "Tagesgeld")]));
    }

    [Fact]
    public void A_girokonto_that_is_not_typed_CACC_is_not_eligible()
    {
        Assert.Null(AccountSelector.Pick([Account("1", "SVGS", "Girokonto"), Account("2", "CACC", "Tagesgeld")]));
    }

    [Fact]
    public void Missing_types_or_products_choose_nothing_when_there_are_several_accounts()
    {
        Assert.Null(AccountSelector.Pick([Account("1", null, null), Account("2", "CACC", null)]));
    }

    [Fact]
    public void No_accounts_choose_nothing()
    {
        Assert.Null(AccountSelector.Pick([]));
    }
}
