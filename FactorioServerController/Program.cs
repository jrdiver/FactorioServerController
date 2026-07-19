using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using FactorioLibrary;
using FactorioLibrary.Data;
using FactorioLibrary.Services;
using FactorioServerController.Components;
using FactorioServerController.Components.Endpoints;
using FactorioServerController.Auth;
using Microsoft.AspNetCore.HttpOverrides;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Ensure data directory exists for persistent storage
string dataDir = Path.Combine(Directory.GetCurrentDirectory(), "data");
if (!Directory.Exists(dataDir)) Directory.CreateDirectory(dataDir);

// Add services to the container.
string defaultDb = $"Data Source={Path.Combine(dataDir, "factorio_manager.db")}";
builder.Services.AddDbContext<AppDbContext>(options => options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? defaultDb));

string defaultKeyPath = Path.Combine(dataDir, "keys");
string dataProtectionPath = builder.Configuration["DataProtection:KeyPath"] ?? defaultKeyPath;

builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath)).SetApplicationName("FactorioServerController");

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
string settingsPath = builder.Configuration["GlobalSettings:Path"] ?? Path.Combine(dataDir, "settings.json");
builder.Services.AddSingleton<GlobalSettingsService>(sp => new GlobalSettingsService(settingsPath));
builder.Services.AddSingleton<FactorioWebApi>(sp => new FactorioWebApi(new FactorioLibrary.Objects.FactorioCredentials(), sp.GetRequiredService<GlobalSettingsService>()));
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
        roleManager.CreateAsync(new IdentityRole("Administrator")).GetAwaiter().GetResult();

    IList<IdentityUser> admins = userManager.GetUsersInRoleAsync("Administrator").GetAwaiter().GetResult();
    if (admins.Count == 0)
    {
        IdentityUser? firstUser = userManager.Users.FirstOrDefault();
        if (firstUser != null)
            userManager.AddToRoleAsync(firstUser, "Administrator").GetAwaiter().GetResult();
    }
}

// Configure the HTTP request pipeline.
app.UseForwardedHeaders(new ForwardedHeadersOptions
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
app.MapGet("/api/instances/{id:int}/saves/{filename}", (int id, string filename, InstanceManager manager) => 
{
    // Sanitize filename to prevent directory traversal
    string safeFilename = Path.GetFileName(filename);
    string savesDir = manager.GetSavesDirectory(id);
    string filePath = Path.Combine(savesDir, safeFilename);
    
    if (!System.IO.File.Exists(filePath)) 
        return Results.NotFound();
        
    return Results.File(filePath, "application/zip", safeFilename);
}).RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, "ApiKey").RequireAuthenticatedUser());

// Minimal API endpoint for downloading individual mod files
app.MapGet("/api/instances/{id:int}/mods/{filename}", (int id, string filename, InstanceManager manager) => 
{
    string safeFilename = Path.GetFileName(filename);
    string modsDir = manager.GetModsDirectory(id);
    string filePath = Path.Combine(modsDir, safeFilename);
    
    if (!System.IO.File.Exists(filePath)) 
        return Results.NotFound();
        
    return Results.File(filePath, "application/zip", safeFilename);
}).RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, "ApiKey").RequireAuthenticatedUser());

// Minimal API endpoint for downloading all mods as a zip
app.MapGet("/api/instances/{id:int}/mods/downloadAll", (int id, InstanceManager manager) => 
{
    string modsDir = manager.GetModsDirectory(id);
    if (!Directory.Exists(modsDir))
        return Results.NotFound();

    string tempZipPath = Path.Combine(manager.GetConfigDirectory(id), "modpack_temp.zip");
    
    if (System.IO.File.Exists(tempZipPath))
        System.IO.File.Delete(tempZipPath);

    System.IO.Compression.ZipFile.CreateFromDirectory(modsDir, tempZipPath, System.IO.Compression.CompressionLevel.Fastest, false);
    
    return Results.File(tempZipPath, "application/zip", $"instance_{id}_mods.zip");
}).RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, "ApiKey").RequireAuthenticatedUser());

app.Run();
