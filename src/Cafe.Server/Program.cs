using Cafe.Server.Agents;
using Cafe.Server.Auth;
using Cafe.Shared.Agents;
using Cafe.Server.Components;
using Cafe.Server.Dashboard;
using Cafe.Server.Data;
using Cafe.Server.Pricing;
using Cafe.Server.Sessions;
using Cafe.Server.Stations;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;

var builder = WebApplication.CreateBuilder(args);

// Run as a Windows service when installed as one (no-op on macOS/dev and in a console).
builder.Host.UseWindowsService();

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Connection string is read lazily so configuration overrides (e.g. the test factory) take effect.
builder.Services.AddDbContextFactory<CafeDbContext>((sp, options) =>
{
    var connectionString = sp.GetRequiredService<IConfiguration>().GetConnectionString("Cafe")
                           ?? "Data Source=cafe.db";
    options.UseSqlite(connectionString);
});
builder.Services.TryAddSingleton(TimeProvider.System);
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddScoped<AdminPasswordService>();
builder.Services.AddScoped<StationTypeService>();
builder.Services.AddScoped<StationService>();
builder.Services.AddScoped<Cafe.Server.Pricing.RateRuleService>();
builder.Services.AddSingleton<DefaultStationStatusProvider>();
builder.Services.AddSingleton<IStationStatusProvider, SessionAwareStatusProvider>();
builder.Services.AddSingleton<SessionNotifier>();
builder.Services.AddSingleton<SessionSettingsService>();
builder.Services.AddSingleton<PricingConfigService>();
builder.Services.AddSingleton<SessionCharges>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddSingleton<IExpiryActionHandler, LoggingExpiryActionHandler>();
builder.Services.AddSingleton<ExpiryEvaluator>();
builder.Services.AddSingleton<IExpiryEvaluator>(sp => sp.GetRequiredService<ExpiryEvaluator>());
if (SessionTicker.IsEnabled(builder.Configuration))
{
    builder.Services.AddHostedService<SessionTicker>();
}

builder.Services.AddAgentServices();
builder.Services.AddScoped<SessionHistoryService>();
builder.Services.AddScoped<DashboardService>();

// Single shared admin login: one persistent, sliding 30-day cookie. No Identity users table.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = ".Cafe.Auth";
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.ExpireTimeSpan = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        // The LAN serves plain HTTP in v1, so the cookie cannot be Secure-only.
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

// Currency and number formatting follows Cafe:Culture (empty = the server's culture).
CafeCulture.Apply(CafeCulture.Resolve(app.Configuration[CafeCulture.Key], app.Logger));

using (var scope = app.Services.CreateScope())
{
    var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<CafeDbContext>>();
    using var db = dbFactory.CreateDbContext();
    db.Database.Migrate();
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
// Re-executing a failed POST (e.g. a /logout without an antiforgery token) would re-read the rejected form and
// throw, so friendly status pages are only produced for GET/HEAD requests.
app.UseWhen(
    context => HttpMethods.IsGet(context.Request.Method) || HttpMethods.IsHead(context.Request.Method),
    branch => branch.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true));
app.MapStaticAssets();
app.UseMiddleware<SetupGateMiddleware>();
app.UseMiddleware<AgentAuthMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Binding a form value makes the endpoint validate the antiforgery token (400 when missing).
// Anonymous is allowed so an expired cookie can never trap the user.
app.MapPost("/logout", async (HttpContext context, [FromForm] string? returnUrl) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).AllowAnonymous();

app.MapHub<AgentHub>(AgentProtocol.HubPath).AllowAnonymous();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
