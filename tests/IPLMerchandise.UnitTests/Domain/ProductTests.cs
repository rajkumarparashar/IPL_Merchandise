using IPLMerchandise.Domain.Catalog;
using IPLMerchandise.Domain.Common;

namespace IPLMerchandise.UnitTests.DomainModel;

public class ProductTests
{
    [Theory]
    [InlineData(0)]          // must be positive
    [InlineData(-1)]
    [InlineData(10.123)]     // at most two decimals
    [InlineData(1000000.01)] // above the cap
    public void Invalid_prices_are_rejected(decimal price)
    {
        var ex = Assert.Throws<BusinessRuleException>(() => TestData.Product(price: price));
        Assert.Equal(ErrorCodes.InvalidPrice, ex.Code);
    }

    [Fact]
    public void Price_change_returns_an_audit_entry_with_old_value_new_value_and_reason()
    {
        var cap = TestData.CskCap();
        var change = cap.ChangePrice(549.00m, new ChangeContext("admin", ChangeReason.ManualAdjustment, TestData.Now, "Season pricing"));

        Assert.NotNull(change);
        Assert.Equal((499.00m, 549.00m, "admin", "Season pricing"), (change.OldValue, change.NewValue, change.ChangedBy, change.Note));
        Assert.Equal(549.00m, cap.Price);
    }

    [Fact]
    public void Selling_the_last_unit_leaves_zero_stock()
    {
        var photo = TestData.KkrPhoto(stock: 1);
        photo.DeductStock(1, TestData.Ctx);
        Assert.Equal(0, photo.StockQuantity);
    }

    [Fact]
    public void Selling_more_than_in_stock_throws_and_changes_nothing()
    {
        var photo = TestData.KkrPhoto(stock: 1);
        Assert.Throws<InsufficientStockException>(() => photo.DeductStock(2, TestData.Ctx));
        Assert.Equal(1, photo.StockQuantity);
    }
}
