using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Application.Common;
using IPLMerchandise.Domain.Catalog;
using IPLMerchandise.Domain.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace IPLMerchandise.Infrastructure.Seeding;

public sealed class AdminSeedOptions
{
    public const string Section = "AdminSeed";

    /// <summary>Set via environment (AdminSeed__Email / AdminSeed__Password). Nothing is seeded when either is empty.</summary>
    public string? Email { get; set; }
    public string? Password { get; set; }
    public string DisplayName { get; set; } = "Store Admin";
}

/// <summary>Idempotent seed: safe to run on every start. Existing products and users are left untouched.</summary>
public sealed class DataSeeder(
    IProductRepository products,
    IUserRepository users,
    IPasswordHasher hasher,
    IOptions<AdminSeedOptions> adminOptions,
    TimeProvider clock,
    ILogger<DataSeeder> logger)
{
    // The first four match the assessment walkthrough; CreatedAt is staggered so the stable list order starts MI, CSK, RCB, KKR.
    // The rest broaden the catalogue (other franchises, every product type) and come after them in that order.
    internal static readonly (string Id, string Name, ProductType Type, string Franchise, string Description, decimal Price, int Stock, bool Featured)[] Products =
    [
        ("mi-jersey-2026", "MI Jersey", ProductType.Jersey, "MI", "Official Mumbai Indians 2026 home jersey. Breathable match-grade fabric.", 1999.00m, 50, true),
        ("csk-cap", "CSK Cap", ProductType.Cap, "CSK", "Chennai Super Kings yellow cap with embroidered logo.", 499.00m, 100, true),
        ("rcb-flag", "RCB Flag", ProductType.Flag, "RCB", "Royal Challengers Bengaluru supporters' flag, 90 × 60 cm.", 349.00m, 200, true),
        ("kkr-signed-photo", "KKR Signed Photo", ProductType.SignedPhotograph, "KKR", "Limited-edition photograph signed by the Kolkata Knight Riders captain. Certificate of authenticity included.", 4999.00m, 2, true),
        ("srh-orange-army-jersey", "SRH Orange Army Jersey", ProductType.Jersey, "SRH", "Sunrisers Hyderabad Orange Army fan jersey in lightweight, quick-dry fabric.", 1799.00m, 40, true),
        ("rr-pink-cap", "RR Pink Cap", ProductType.Cap, "RR", "Rajasthan Royals pink cap with an adjustable strap and embroidered team name.", 549.00m, 80, false),
        ("dc-supporters-flag", "DC Supporters' Flag", ProductType.Flag, "DC", "Delhi Capitals supporters' flag, 90 × 60 cm, ideal for match days.", 399.00m, 150, false),
        ("gt-signed-photo", "GT Signed Photo", ProductType.SignedPhotograph, "GT", "Limited-edition photograph signed by the Gujarat Titans squad. Certificate of authenticity included.", 5499.00m, 3, true),
    ];

    private static readonly DateTime SeedEpoch = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public async Task SeedAsync(CancellationToken ct)
    {
        for (var i = 0; i < Products.Length; i++)
        {
            var p = Products[i];
            if (await products.GetAsync(p.Id, ct) is not null) continue;

            var ctx = new ChangeContext("system:seed", ChangeReason.ProductCreated, SeedEpoch.AddMinutes(i), "Initial catalogue");
            var (product, audit) = Product.Create(p.Id, p.Name, p.Type, p.Franchise, p.Description, p.Price, p.Stock, ctx, p.Featured);
            try
            {
                await products.CreateAsync(product, audit, ct);
                logger.LogInformation("Seeded product {ProductId}", p.Id);
            }
            catch (ConflictException)
            {
                // Another instance seeded it at the same time.
            }
        }

        await SeedAdminAsync(ct);
    }

    private async Task SeedAdminAsync(CancellationToken ct)
    {
        var o = adminOptions.Value;
        if (string.IsNullOrWhiteSpace(o.Email) || string.IsNullOrWhiteSpace(o.Password))
        {
            logger.LogInformation("No admin seed credentials configured; skipping admin seed");
            return;
        }

        var admin = new User
        {
            Id = Guid.NewGuid().ToString("D"),
            Email = o.Email.Trim(),
            NormalizedEmail = User.Normalize(o.Email),
            DisplayName = o.DisplayName,
            PasswordHash = hasher.Hash(o.Password),
            Roles = [Roles.Admin, Roles.Customer],
            CreatedAtUtc = clock.GetUtcNow().UtcDateTime
        };
        if (await users.TryCreateAsync(admin, ct))
            logger.LogInformation("Seeded admin user {UserId}", admin.Id);
    }
}
