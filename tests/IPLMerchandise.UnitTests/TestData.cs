using IPLMerchandise.Domain.Catalog;

namespace IPLMerchandise.UnitTests;

public static class TestData
{
    public static readonly DateTime Now = new(2026, 4, 1, 10, 0, 0, DateTimeKind.Utc);
    public static readonly ChangeContext Ctx = new("tester", ChangeReason.ManualAdjustment, Now);

    public static Product Product(string id = "p1", decimal price = 100m, int stock = 10,
        ProductType type = ProductType.Jersey, string franchise = "MI", string? name = null, int minuteOffset = 0)
    {
        var (product, _) = IPLMerchandise.Domain.Catalog.Product.Create(id, name ?? $"Product {id}", type, franchise, "desc", price, stock,
            Ctx with { AtUtc = Now.AddMinutes(minuteOffset) });
        return product;
    }

    // The assessment walkthrough catalogue.
    public static Product MiJersey(int stock = 50) => Product("mi-jersey", 1999.00m, stock, ProductType.Jersey, "MI", "MI Jersey", 0);
    public static Product CskCap(int stock = 100) => Product("csk-cap", 499.00m, stock, ProductType.Cap, "CSK", "CSK Cap", 1);
    public static Product KkrPhoto(int stock = 2) => Product("kkr-photo", 4999.00m, stock, ProductType.SignedPhotograph, "KKR", "KKR Signed Photo", 3);
}
