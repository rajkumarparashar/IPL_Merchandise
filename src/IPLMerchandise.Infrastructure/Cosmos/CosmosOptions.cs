using System.ComponentModel.DataAnnotations;

namespace IPLMerchandise.Infrastructure.Cosmos;

public sealed class CosmosOptions
{
    public const string Section = "Cosmos";

    /// <summary>Connection string. Comes from user-secrets, an environment variable or Key Vault, never from appsettings in production.</summary>
    [Required]
    public string ConnectionString { get; set; } = "";

    [Required]
    public string DatabaseName { get; set; } = "ipl-merchandise";

    /// <summary>Partition key value for the single store. Becomes the franchise/tenant id with multi-tenancy.</summary>
    [Required]
    public string StoreId { get; set; } = "main";

    /// <summary>Gateway mode + LimitToEndpoint is required for the emulator. Production should use Direct mode.</summary>
    public bool UseGatewayMode { get; set; }

    /// <summary>Create database and containers at startup. Production runs the provisioning script instead.</summary>
    public bool InitializeOnStartup { get; set; }

    public bool SeedOnStartup { get; set; }

    /// <summary>Shared autoscale throughput for the database (RU/s max). Null = serverless/emulator default.</summary>
    public int? DatabaseAutoscaleMaxThroughput { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 10;
    public int MaxRetryAttemptsOnThrottling { get; set; } = 5;
    public int MaxRetryWaitSeconds { get; set; } = 15;
}
