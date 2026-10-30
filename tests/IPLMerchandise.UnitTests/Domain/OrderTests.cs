using IPLMerchandise.Domain.Orders;

namespace IPLMerchandise.UnitTests.DomainModel;

public class OrderTests
{
    [Fact]
    public void Later_price_changes_do_not_alter_a_placed_order()
    {
        var cap = TestData.CskCap();
        var order = Order.Place(Guid.NewGuid(), "asha", [new OrderLine(cap.Id, cap.Name, cap.FranchiseCode, cap.Price, 3)], TestData.Now);

        cap.ChangePrice(549.00m, TestData.Ctx);

        Assert.Equal(499.00m, order.Lines.Single().UnitPrice);
        Assert.Equal(1497.00m, order.Total);
    }

    [Theory]
    [InlineData(998.99, 99.00)]
    [InlineData(999.00, 0)]
    public void Shipping_is_free_from_999(decimal subtotal, decimal fee) =>
        Assert.Equal(fee, ShippingPolicy.FeeFor(subtotal));
}
