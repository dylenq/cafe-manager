using Cafe.Server.Auth;
using Cafe.Server.Components;
using Cafe.Server.Data;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddScoped<AdminPasswordService>();

var app = builder.Build();

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
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseMiddleware<SetupGateMiddleware>();
app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

public partial class Program;
