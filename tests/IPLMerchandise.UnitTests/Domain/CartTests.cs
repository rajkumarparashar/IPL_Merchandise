using IPLMerchandise.Domain.Carts;
using IPLMerchandise.Domain.Catalog;
using IPLMerchandise.Domain.Common;

namespace IPLMerchandise.UnitTests.DomainModel;

public class CartTests
{
    private static readonly DateTime Now = TestData.Now;

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void Quantities_1_and_10_are_allowed(int quantity)
    {
        var cart = Cart.Empty("c");
        cart.AddItem(TestData.MiJersey(), quantity, Now);
        Assert.Equal(quantity, cart.Lines.Single().Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(11)]
    public void Quantities_outside_1_to_10_are_rejected(int quantity)
    {
        var ex = Assert.Throws<BusinessRuleException>(() => Cart.Empty("c").AddItem(TestData.MiJersey(), quantity, Now));
        Assert.Equal(ErrorCodes.InvalidQuantity, ex.Code);
    }

    [Fact]
    public void Adding_to_an_existing_line_cannot_go_over_10()
    {
        var cart = Cart.Empty("c");
        cart.AddItem(TestData.MiJersey(), 8, Now);
        Assert.Throws<BusinessRuleException>(() => cart.AddItem(TestData.MiJersey(), 3, Now));
        Assert.Equal(8, cart.Lines.Single().Quantity);
    }

    [Fact]
    public void With_stock_of_exactly_one_you_can_add_one_but_not_two()
    {
        var photo = TestData.KkrPhoto(stock: 1);
        var cart = Cart.Empty("c");

        Assert.Throws<InsufficientStockException>(() => cart.AddItem(photo, 2, Now));
        cart.AddItem(photo, 1, Now);
        Assert.Equal(1, cart.Lines.Single().Quantity);
    }

    [Fact]
    public void Limited_editions_are_capped_at_two_per_line() =>
        Assert.Throws<BusinessRuleException>(() => Cart.Empty("c").AddItem(TestData.KkrPhoto(stock: 10), 3, Now));

    [Fact]
    public void Walkthrough_totals()
    {
        var cart = Cart.Empty("asha");
        cart.AddItem(TestData.MiJersey(), 2, Now);
        cart.AddItem(TestData.CskCap(), 3, Now);
        cart.AddItem(TestData.KkrPhoto(), 1, Now);
        Assert.Equal(10494.00m, cart.Subtotal);
    }

    [Fact]
    public void Price_and_stock_changes_are_flagged_and_the_accepted_price_is_kept()
    {
        var cap = TestData.CskCap();
        var jersey = TestData.MiJersey();
        var cart = Cart.Empty("c");
        cart.AddItem(cap, 3, Now);
        cart.AddItem(jersey, 5, Now);

        cap.ChangePrice(549.00m, TestData.Ctx);
        jersey.SetStock(2, TestData.Ctx);
        var review = cart.Review(new Dictionary<string, Product> { [cap.Id] = cap, [jersey.Id] = jersey });

        Assert.True(review.RequiresReview);
        Assert.Equal(CartLineIssue.PriceChanged, review.Lines.Single(l => l.Line.ProductId == cap.Id).Issues);
        Assert.Equal(CartLineIssue.InsufficientStock, review.Lines.Single(l => l.Line.ProductId == jersey.Id).Issues);
        Assert.Equal(499.00m, cart.Find(cap.Id)!.UnitPrice); // never silently changed
    }

    [Fact]
    public void Accepting_changes_moves_to_the_new_price_and_available_stock()
    {
        var cap = TestData.CskCap();
        var jersey = TestData.MiJersey();
        var cart = Cart.Empty("c");
        cart.AddItem(cap, 3, Now);
        cart.AddItem(jersey, 5, Now);
        cap.ChangePrice(549.00m, TestData.Ctx);
        jersey.SetStock(2, TestData.Ctx);
        var current = new Dictionary<string, Product> { [cap.Id] = cap, [jersey.Id] = jersey };

        cart.AcceptChanges(current, Now);

        Assert.Equal(549.00m, cart.Find(cap.Id)!.UnitPrice);
        Assert.Equal(2, cart.Find(jersey.Id)!.Quantity);
        Assert.False(cart.Review(current).RequiresReview);
    }
}
