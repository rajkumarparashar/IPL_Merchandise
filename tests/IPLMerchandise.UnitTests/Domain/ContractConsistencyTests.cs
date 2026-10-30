using System.Reflection;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Domain.Common;

namespace IPLMerchandise.UnitTests.DomainModel;

/// <summary>The wire contract duplicates the domain's error codes on purpose; this stops the two drifting apart.</summary>
public class ContractConsistencyTests
{
    [Fact]
    public void Problem_codes_match_domain_error_codes()
    {
        static Dictionary<string, string?> Constants(Type t) => t.GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(f => f.IsLiteral).ToDictionary(f => f.Name, f => (string?)f.GetRawConstantValue());

        Assert.Equal(Constants(typeof(ErrorCodes)).OrderBy(x => x.Key), Constants(typeof(ProblemCodes)).OrderBy(x => x.Key));
    }

    [Fact]
    public void Image_limits_published_to_clients_match_the_domain_rules()
    {
        Assert.Equal(Domain.Catalog.ProductImageRules.MaxBytes, ApiLimits.MaxImageBytes);
        Assert.Equal(Domain.Catalog.ProductImageRules.AllowedContentTypes, ApiLimits.ImageContentTypes);
    }
}
