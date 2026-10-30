using IPLMerchandise.Web.Components;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorComponents().AddInteractiveWebAssemblyComponents();
builder.Services.AddAuthorization();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}
else
{
    app.UseWebAssemblyDebugging();
}

app.Use((context, next) =>
{
    context.Response.Headers.XContentTypeOptions = "nosniff";
    context.Response.Headers.XFrameOptions = "DENY";          // no clickjacking via iframes
    context.Response.Headers["Referrer-Policy"] = "strict-origin-when-cross-origin";
    return next(context);
});

// Runtime configuration for the WebAssembly client, per environment (ApiBaseUrl comes from this host's config / env vars),
// so one build artifact can be promoted from dev to staging to production unchanged.
app.MapGet("/client-config", (IConfiguration config) => Results.Json(new
{
    ApiBaseUrl = config["Client:ApiBaseUrl"] ?? "http://localhost:5200/"
}));
app.MapGet("/health/live", () => Results.Ok("Healthy"));

app.UseAuthorization();
app.UseAntiforgery();
app.MapStaticAssets();

// This host only serves the WebAssembly app; it never sees the user's token. Pages marked [Authorize] in the client
// are guarded by the client router (UI) and, for real, by the API. So the host must not try to authorise them itself:
// without AllowAnonymous a full page load of e.g. /cart fails with 500 ("authorization middleware not found").
app.MapRazorComponents<App>()
    .AddInteractiveWebAssemblyRenderMode()
    .AddAdditionalAssemblies(typeof(IPLMerchandise.Web.Client._Imports).Assembly)
    .AllowAnonymous();

app.Run();
