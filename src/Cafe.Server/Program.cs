using Cafe.Server.Agents;
using Cafe.Server.Auth;
using Cafe.Shared.Agents;
using Cafe.Server.Components;
using Cafe.Server.Dashboard;
using Cafe.Server.Data;
using Cafe.Server.Devices;
using Cafe.Server.Hosting;
using Cafe.Server.Payments;
using Cafe.Server.Receipts;
using Cafe.Server.Reports;
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

// Database/log/backup locations: an explicit ConnectionStrings:Cafe wins; Production or a Windows service
// defaults to %ProgramData%\CafeManager; Development keeps cafe.db in the content root.
var dataLocation = CafeDataPaths.Resolve(
    builder.Configuration.GetConnectionString("Cafe"),
    builder.Configuration["Cafe:DataDirectory"],
    builder.Environment.IsProduction(),
    Microsoft.Extensions.Hosting.WindowsServices.WindowsServiceHelpers.IsWindowsService(),
    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
    builder.Environment.ContentRootPath);
dataLocation.EnsureDirectories();
builder.Services.AddSingleton(dataLocation);
if (dataLocation.UseProductionLayout)
{
    builder.Logging.AddProvider(new FileLoggerProvider(dataLocation.LogDirectory, () => DateTimeOffset.Now));
}

builder.Services.AddDbContextFactory<CafeDbContext>(options => options.UseSqlite(dataLocation.ConnectionString));
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
builder.Services.AddSingleton<Cafe.Server.Customers.BalanceLock>();
builder.Services.AddScoped<Cafe.Server.Customers.BalanceService>();
builder.Services.AddScoped<Cafe.Server.Customers.CustomerService>();
builder.Services.AddScoped<Cafe.Server.Customers.PackageService>();
builder.Services.AddScoped<SessionService>();
builder.Services.AddSingleton<IExpiryActionHandler, LoggingExpiryActionHandler>();
builder.Services.AddSingleton<ExpiryEvaluator>();
builder.Services.AddSingleton<IExpiryEvaluator>(sp => sp.GetRequiredService<ExpiryEvaluator>());
if (SessionTicker.IsEnabled(builder.Configuration))
{
    builder.Services.AddHostedService<SessionTicker>();
}

// Nightly database backup (also injected into Settings for "Back up now"); the loop itself honours Backup:RunScheduler.
builder.Services.AddSingleton<Cafe.Server.Backups.BackupService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Cafe.Server.Backups.BackupService>());

builder.Services.AddPosServices();
builder.Services.AddReceiptServices();
builder.Services.AddReportServices();
builder.Services.AddAgentServices();
builder.Services.AddTvServices(builder.Configuration);
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

// Log only the database file path, never the raw connection string.
app.Logger.LogInformation("Database: {DatabaseFile}; logs: {LogDirectory}", dataLocation.DatabaseFile, dataLocation.LogDirectory);

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

app.MapReportEndpoints();

app.MapHub<AgentHub>(AgentProtocol.HubPath).AllowAnonymous();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
