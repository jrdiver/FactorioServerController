using System.Runtime.InteropServices;
using System.Security.Claims;
using FactorioLibrary;
using FactorioLibrary.Data;
using FactorioLibrary.Models;
using FactorioLibrary.Services;
using FactorioServerController.Auth;
using FactorioServerController.Components;
using FactorioServerController.Components.Endpoints;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Ensure data directory exists for persistent storage
string baseDataPath = builder.Configuration["HOST_DATA_PATH"];
if (string.IsNullOrWhiteSpace(baseDataPath)) baseDataPath = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? @"C:\Factorio" : "/data";
string dataDir = Path.Combine(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true" ? "/data" : baseDataPath, "app-data");
if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);

// Add services to the container.
string connStr = builder.Configuration.GetConnectionString("DefaultConnection");
if (string.IsNullOrWhiteSpace(connStr)) connStr = $"Data Source={Path.Combine(dataDir, "factorio_manager.db")}";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(connStr));

string dataProtectionPath = builder.Configuration["DataProtection:KeyPath"];
if (string.IsNullOrWhiteSpace(dataProtectionPath)) dataProtectionPath = Path.Combine(dataDir, "keys");

builder.Services.AddDataProtection().PersistKeysToFileSystem(new(dataProtectionPath)).SetApplicationName("FactorioServerController");

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

builder.Services.AddAuthentication().AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>("ApiKey", options => { });

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/login";
    options.LogoutPath = "/api/auth/logout";
    options.AccessDeniedPath = "/not-found";
});

builder.Services.AddAuthorization();

builder.Services.AddIdentityCore<IdentityUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireDigit = false;
        options.Password.RequireUppercase = false;
        options.Password.RequireLowercase = false;
        options.Password.RequiredLength = 8;
    }).AddRoles<IdentityRole>().AddEntityFrameworkStores<AppDbContext>().AddSignInManager().AddDefaultTokenProviders();

// Register Factorio Core Services
string settingsPath = builder.Configuration["GlobalSettings:Path"];
if (string.IsNullOrWhiteSpace(settingsPath)) settingsPath = Path.Combine(dataDir, "settings.json");
builder.Services.AddSingleton<GlobalSettingsService>(sp => new(settingsPath));
builder.Services.AddSingleton<FactorioWebApi>(sp => new(new(), sp.GetRequiredService<GlobalSettingsService>()));
builder.Services.AddSingleton<VersionManager>();
builder.Services.AddSingleton<ModManager>();
builder.Services.AddSingleton<InstanceManager>();
builder.Services.AddSingleton<RconService>();

builder.Services.AddRazorComponents().AddInteractiveServerComponents(options => { options.RootComponents.MaxJSRootComponents = 100; }).AddHubOptions(options => { options.MaximumReceiveMessageSize = 2147483648; });

WebApplication app = builder.Build();

// Automatically apply database migrations on startup so new DBs get tables created
using (IServiceScope scope = app.Services.CreateScope())
{
    AppDbContext dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.Migrate();

    // Seed the first user as Administrator if no administrators exist
    RoleManager<IdentityRole> roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    UserManager<IdentityUser> userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();

    if (!roleManager.RoleExistsAsync("Administrator").GetAwaiter().GetResult())
        roleManager.CreateAsync(new("Administrator")).GetAwaiter().GetResult();

    IList<IdentityUser> admins = userManager.GetUsersInRoleAsync("Administrator").GetAwaiter().GetResult();
    if (admins.Count == 0)
    {
        IdentityUser? firstUser = userManager.Users.FirstOrDefault();
        if (firstUser != null)
            userManager.AddToRoleAsync(firstUser, "Administrator").GetAwaiter().GetResult();
    }
}

// Configure the HTTP request pipeline.
app.UseForwardedHeaders(new()
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

app.MapAuthEndpoints();

// Minimal API endpoint for downloading save files directly
app.MapGet("/api/instances/{id:int}/saves/{filename}", async (int id, string filename, InstanceManager manager, AppDbContext db, UserManager<IdentityUser> userManager, ClaimsPrincipal user) =>
{
    if (!await ApiAuthHelper.HasAccessAsync(db, userManager, user, id, false)) return Results.Forbid();

    // Sanitize filename to prevent directory traversal
    string safeFilename = Path.GetFileName(filename);
    string savesDir = manager.GetSavesDirectory(id);
    string filePath = Path.Combine(savesDir, safeFilename);

    if (!System.IO.File.Exists(filePath))
        return Results.NotFound();

    return Results.File(filePath, "application/zip", safeFilename);
}).RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, "ApiKey").RequireAuthenticatedUser());

// Minimal API endpoint for downloading individual mod files
app.MapGet("/api/instances/{id:int}/mods/{filename}", async (int id, string filename, InstanceManager manager, AppDbContext db, UserManager<IdentityUser> userManager, ClaimsPrincipal user) =>
{
    if (!await ApiAuthHelper.HasAccessAsync(db, userManager, user, id, false)) return Results.Forbid();

    string safeFilename = Path.GetFileName(filename);
    string modsDir = manager.GetModsDirectory(id);
    string filePath = Path.Combine(modsDir, safeFilename);

    if (!System.IO.File.Exists(filePath))
        return Results.NotFound();

    return Results.File(filePath, "application/zip", safeFilename);
}).RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, "ApiKey").RequireAuthenticatedUser());

// Minimal API endpoint for downloading all mods as a zip
app.MapGet("/api/instances/{id:int}/mods/downloadAll", async (int id, InstanceManager manager, AppDbContext db, UserManager<IdentityUser> userManager, ClaimsPrincipal user) =>
{
    if (!await ApiAuthHelper.HasAccessAsync(db, userManager, user, id, false)) return Results.Forbid();

    string modsDir = manager.GetModsDirectory(id);
    if (!Directory.Exists(modsDir))
        return Results.NotFound();

    string tempZipPath = Path.Combine(manager.GetConfigDirectory(id), "modpack_temp.zip");

    if (System.IO.File.Exists(tempZipPath))
        System.IO.File.Delete(tempZipPath);

    System.IO.Compression.ZipFile.CreateFromDirectory(modsDir, tempZipPath, System.IO.Compression.CompressionLevel.Fastest, false);

    return Results.File(tempZipPath, "application/zip", $"instance_{id}_mods.zip");
}).RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, "ApiKey").RequireAuthenticatedUser());

// Minimal API endpoint for listing all instances
app.MapGet("/api/instances", async (AppDbContext db, InstanceManager manager, UserManager<IdentityUser> userManager, ClaimsPrincipal user) =>
{
    IdentityUser? identityUser = await userManager.GetUserAsync(user);
    if (identityUser == null) return Results.Unauthorized();
    bool isGlobalAdmin = await userManager.IsInRoleAsync(identityUser, "Administrator");

    IQueryable<ServerInstance> query = db.ServerInstances.AsQueryable();
    List<FactorioLibrary.Models.UserServerAccess>? accessList = null;

    if (!isGlobalAdmin)
    {
        accessList = await db.UserServerAccesses
            .Where(usa => usa.UserId == identityUser.Id)
            .ToListAsync();

        List<int> accessibleIds = accessList.Select(usa => usa.ServerInstanceId).ToList();
        query = query.Where(si => accessibleIds.Contains(si.Id));
    }

    List<ServerInstance> instancesList = await query.ToListAsync();

    var instances = instancesList.Select(x => new
    {
        x.Id,
        x.Name,
        x.Port,
        x.RconPort,
        IsRunning = manager.IsRunning(x.Id),
        x.ActiveSaveName,
        AccessLevel = isGlobalAdmin ? "Admin" : accessList?.FirstOrDefault(a => a.ServerInstanceId == x.Id)?.AccessLevel.ToString() ?? "Unknown"
    }).ToList();
    return Results.Ok(instances);
}).RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, "ApiKey").RequireAuthenticatedUser());

// Minimal API endpoint for listing saves for an instance
app.MapGet("/api/instances/{id:int}/saves", async (int id, AppDbContext db, InstanceManager manager, UserManager<IdentityUser> userManager, ClaimsPrincipal user) =>
{
    if (!await ApiAuthHelper.HasAccessAsync(db, userManager, user, id, false)) return Results.Forbid();

    ServerInstance? instance = await db.ServerInstances.FindAsync(id);
    if (instance == null) return Results.NotFound();

    string savesDir = manager.GetSavesDirectory(id);
    if (!Directory.Exists(savesDir)) Directory.CreateDirectory(savesDir);
    IEnumerable<string?> saves = Directory.GetFiles(savesDir, "*.zip").Select(Path.GetFileName);

    var result = saves.Select(s => new
    {
        Name = s,
        IsActive = (s == instance.ActiveSaveName)
    }).ToList();
    return Results.Ok(result);
}).RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, "ApiKey").RequireAuthenticatedUser());

// Minimal API endpoint for starting an instance
app.MapPost("/api/instances/{id:int}/start", async (int id, AppDbContext db, InstanceManager manager, UserManager<IdentityUser> userManager, ClaimsPrincipal user) =>
{
    if (!await ApiAuthHelper.HasAccessAsync(db, userManager, user, id, true)) return Results.Forbid();

    ServerInstance? instance = await db.ServerInstances.FindAsync(id);
    if (instance == null) return Results.NotFound();

    (bool Success, bool CleanedCorruptSave) result = await manager.StartInstanceAsync(instance);
    return result.Success ? Results.Ok("Started") : Results.BadRequest("Failed to start instance or already running.");
}).RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, "ApiKey").RequireAuthenticatedUser());

// Minimal API endpoint for stopping an instance
app.MapPost("/api/instances/{id:int}/stop", async (int id, AppDbContext db, InstanceManager manager, UserManager<IdentityUser> userManager, ClaimsPrincipal user) =>
{
    if (!await ApiAuthHelper.HasAccessAsync(db, userManager, user, id, true)) return Results.Forbid();

    ServerInstance? instance = await db.ServerInstances.FindAsync(id);
    if (instance == null) return Results.NotFound();

    await manager.StopInstanceAsync(id);
    return Results.Ok("Stop command sent.");
}).RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, "ApiKey").RequireAuthenticatedUser());

// Minimal API endpoint for setting the active save
app.MapPut("/api/instances/{id:int}/saves/active", async (int id, string saveName, AppDbContext db, UserManager<IdentityUser> userManager, ClaimsPrincipal user) =>
{
    if (!await ApiAuthHelper.HasAccessAsync(db, userManager, user, id, true)) return Results.Forbid();

    ServerInstance? instance = await db.ServerInstances.FindAsync(id);
    if (instance == null) return Results.NotFound();

    instance.ActiveSaveName = saveName;
    await db.SaveChangesAsync();
    return Results.Ok(new { Message = $"Active save set to {saveName}" });
}).RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, "ApiKey").RequireAuthenticatedUser());

app.Run();

public static class ApiAuthHelper
{
    public static async Task<bool> HasAccessAsync(AppDbContext db, UserManager<IdentityUser> userManager, ClaimsPrincipal user, int instanceId, bool requireAdmin)
    {
        IdentityUser? identityUser = await userManager.GetUserAsync(user);
        if (identityUser == null) return false;

        bool isGlobalAdmin = await userManager.IsInRoleAsync(identityUser, "Administrator");
        if (isGlobalAdmin) return true;

        UserServerAccess? access = await db.UserServerAccesses.FirstOrDefaultAsync(usa => usa.UserId == identityUser.Id && usa.ServerInstanceId == instanceId);
        if (access == null) return false;

        if (requireAdmin && access.AccessLevel != FactorioLibrary.Models.ServerAccessLevel.Admin)
            return false;

        return true;
    }
}
