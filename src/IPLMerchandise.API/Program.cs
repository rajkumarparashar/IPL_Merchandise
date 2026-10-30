using System.Text.Json.Serialization;
using Asp.Versioning;
using IPLMerchandise.API.BackgroundServices;
using IPLMerchandise.API.Infrastructure;
using IPLMerchandise.API.Security;
using IPLMerchandise.Application;
using IPLMerchandise.Application.Abstractions;
using IPLMerchandise.Contracts.V1;
using IPLMerchandise.Domain.Identity;
using IPLMerchandise.Infrastructure;
using IPLMerchandise.Infrastructure.Cosmos;
using IPLMerchandise.Infrastructure.Seeding;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpLogging;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
var services = builder.Services;
var config = builder.Configuration;

// ---- Layers ----
// Real-time "product changed" pushes to open carts. Registered before AddApplication, which only adds a no-op default.
// With several API instances, add Azure SignalR Service (AddSignalR().AddAzureSignalR()) so a change made on one
// instance reaches shoppers connected to another (DESIGN.md ADR-12).
services.AddSignalR();
services.AddSingleton<IProductChangeNotifier, IPLMerchandise.API.Realtime.SignalRProductChangeNotifier>();
services.AddApplication();
services.AddInfrastructure(config);

// ---- API surface ----
// Validation errors are keyed by JSON property name ("quantity"), matching what clients send.
services.AddControllers(o => o.ModelMetadataDetailsProviders.Add(new Microsoft.AspNetCore.Mvc.ModelBinding.Metadata.SystemTextJsonValidationMetadataProvider()))
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

services.AddApiVersioning(o =>
    {
        o.DefaultApiVersion = new ApiVersion(1, 0);
        o.ReportApiVersions = true; // api-supported-versions header tells clients what exists
        o.ApiVersionReader = new UrlSegmentApiVersionReader();
    })
    .AddMvc()
    .AddApiExplorer(o =>
    {
        o.GroupNameFormat = "'v'V";
        o.SubstituteApiVersionInUrl = true;
    });

services.AddOpenApi("v1", o => o.AddDocumentTransformer((document, _, _) =>
{
    // Lets Swagger UI send a bearer token ("Authorize" button) to the protected endpoints.
    document.Components ??= new Microsoft.OpenApi.OpenApiComponents();
    document.Components.SecuritySchemes ??= new Dictionary<string, Microsoft.OpenApi.IOpenApiSecurityScheme>();
    document.Components.SecuritySchemes["Bearer"] = new Microsoft.OpenApi.OpenApiSecurityScheme
    {
        Type = Microsoft.OpenApi.SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "Token from POST /api/v1/auth/login"
    };
    document.Security ??= [];
    document.Security.Add(new Microsoft.OpenApi.OpenApiSecurityRequirement
    {
        [new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer", document)] = []
    });
    document.Info.Title = "IPL Merchandise Store API";
    return Task.CompletedTask;
}));

// Every error, including framework ones (400 model binding, 404, 405), is RFC 9457 problem details with a correlation id.
services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
{
    var extensions = ctx.ProblemDetails.Extensions;
    extensions[ProblemExtensions.CorrelationId] = CorrelationIdMiddleware.Get(ctx.HttpContext);
    extensions.TryAdd("traceId", System.Diagnostics.Activity.Current?.Id ?? ctx.HttpContext.TraceIdentifier);
    if (ctx.ProblemDetails.Status == 400) extensions.TryAdd(ProblemExtensions.Code, ProblemCodes.ValidationFailed);
});
services.AddExceptionHandler<ApplicationExceptionHandler>(); // registered after Cosmos handler; types don't overlap

// ---- Security ----
services.AddOptions<JwtOptions>().Bind(config.GetSection(JwtOptions.Section)).ValidateDataAnnotations().ValidateOnStart();
services.AddSingleton<ITokenService, JwtTokenService>();

services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<IOptions<JwtOptions>>((bearer, jwt) =>
    {
        var o = jwt.Value;
        bearer.MapInboundClaims = false; // keep "sub", "role", "email" as issued
        bearer.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = o.Issuer,
            ValidateAudience = true,
            ValidAudience = o.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = JwtTokenService.SigningKey(o),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            NameClaimType = "name",
            RoleClaimType = AuthConstants.RoleClaim
        };
        bearer.Events = new JwtBearerEvents
        {
            // Problem details for 401/403 too, so the client handles every error the same way.
            OnChallenge = async ctx =>
            {
                ctx.HandleResponse();
                ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
                var expired = ctx.AuthenticateFailure is SecurityTokenExpiredException;
                await ctx.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = ctx.HttpContext,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = 401,
                        Title = "Unauthorized",
                        Detail = expired ? "Your session has expired. Please sign in again." : "Sign in to continue.",
                        Extensions = { [ProblemExtensions.Code] = expired ? "token-expired" : "unauthorized" }
                    }
                });
            },
            OnForbidden = async ctx =>
            {
                ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
                await ctx.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new ProblemDetailsContext
                {
                    HttpContext = ctx.HttpContext,
                    ProblemDetails = new ProblemDetails
                    {
                        Status = 403, Title = "Forbidden", Detail = "You do not have permission to do this.",
                        Extensions = { [ProblemExtensions.Code] = "forbidden" }
                    }
                });
            }
        };
    });

services.AddAuthorizationBuilder()
    .AddPolicy(AuthConstants.CustomerPolicy, p => p.RequireAuthenticatedUser().RequireRole(Roles.Customer))
    .AddPolicy(AuthConstants.AdminPolicy, p => p.RequireAuthenticatedUser().RequireRole(Roles.Admin));

services.AddCors(o =>
{
    o.AddDefaultPolicy(p => p
        .WithOrigins(config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        .WithHeaders("Authorization", "Content-Type", "If-Match", ApiHeaders.IdempotencyKey, ApiHeaders.CorrelationId)
        .WithMethods("GET", "POST", "PUT", "DELETE")
        .WithExposedHeaders("ETag", "Location", "Retry-After", ApiHeaders.CorrelationId, ApiHeaders.IdempotentReplay, "api-supported-versions"));
    // SignalR's browser client sends credentials on its negotiate request, which CORS only allows with an explicit
    // origin list. Scoped to the hub, so the rest of the API keeps the stricter default policy.
    o.AddPolicy("realtime", p => p
        .WithOrigins(config.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [])
        .AllowAnyHeader().WithMethods("GET", "POST").AllowCredentials());
});

services.AddStoreRateLimiting(config);

// ---- Operability ----
// Request log: method, path, status, duration only. No headers or bodies, so no tokens or passwords in logs.
services.AddHttpLogging(o => o.LoggingFields =
    HttpLoggingFields.RequestMethod | HttpLoggingFields.RequestPath | HttpLoggingFields.ResponseStatusCode | HttpLoggingFields.Duration);
services.AddHostedService<PendingCheckoutRecoveryWorker>();

var app = builder.Build();

await PrepareDatabaseAsync(app);

app.UseMiddleware<CorrelationIdMiddleware>();
app.Use((context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    // Personal data (cart, orders, admin views) must never be stored by browsers or shared proxies.
    if (context.Request.Headers.Authorization.Count > 0) context.Response.Headers.CacheControl = "no-store";
    return next(context);
});
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseHttpLogging();

if (app.Environment.IsDevelopment() || config.GetValue<bool>("Api:EnableSwagger"))
{
    app.MapOpenApi();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("/openapi/v1.json", "IPL Merchandise API v1"));
}
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
app.MapHub<IPLMerchandise.API.Realtime.ProductUpdatesHub>(IPLMerchandise.API.Realtime.ProductUpdatesHub.Path).RequireCors("realtime");

// Liveness: the process is up (no dependencies, so a DB outage does not get pods restarted).
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
// Readiness: dependencies reachable; load balancers stop routing here when Cosmos is down.
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = c => c.Tags.Contains("ready") });

app.Run();

static async Task PrepareDatabaseAsync(WebApplication app)
{
    var cosmos = app.Services.GetRequiredService<IOptions<CosmosOptions>>().Value;
    if (cosmos.InitializeOnStartup)
        await app.Services.GetRequiredService<CosmosDbInitializer>().InitializeAsync(CancellationToken.None);
    if (cosmos.SeedOnStartup)
    {
        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<DataSeeder>().SeedAsync(CancellationToken.None);
    }
}

public partial class Program;
