using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using FactorioLibrary;
using FactorioLibrary.Data;
using FactorioLibrary.Services;
using FactorioServerController.Components;
using FactorioServerController.Components.Endpoints;
using FactorioServerController.Auth;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite(builder.Configuration.GetConnectionString("DefaultConnection") ?? "Data Source=factorio_manager.db"));

var dataProtectionPath = builder.Configuration["DataProtection:KeyPath"];
if (!string.IsNullOrEmpty(dataProtectionPath))
{
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionPath))
        .SetApplicationName("FactorioServerController");
}

builder.Services.AddAuthentication(options =>
    {
        options.DefaultScheme = IdentityConstants.ApplicationScheme;
        options.DefaultSignInScheme = IdentityConstants.ExternalScheme;
    })
    .AddIdentityCookies();

builder.Services.AddAuthentication()
    .AddScheme<ApiKeyAuthenticationOptions, ApiKeyAuthenticationHandler>("ApiKey", options => { });

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
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<AppDbContext>()
    .AddSignInManager()
    .AddDefaultTokenProviders();

// Register Factorio Core Services
var settingsPath = builder.Configuration["GlobalSettings:Path"] ?? "settings.json";
builder.Services.AddSingleton<GlobalSettingsService>(sp => new GlobalSettingsService(settingsPath));
builder.Services.AddSingleton<FactorioWebApi>(sp => new FactorioWebApi(new FactorioLibrary.Objects.FactorioCredentials(), sp.GetRequiredService<GlobalSettingsService>()));
builder.Services.AddSingleton<VersionManager>();
builder.Services.AddSingleton<ModManager>();
builder.Services.AddSingleton<InstanceManager>();
builder.Services.AddSingleton<RconService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        // Allow up to 2GB uploads via SignalR for extremely large Factorio save files
        options.RootComponents.MaxJSRootComponents = 100;
    })
    .AddHubOptions(options =>
    {
        options.MaximumReceiveMessageSize = 2147483648; // 2GB
    });

WebApplication app = builder.Build();

// Automatically apply database migrations on startup so new DBs get tables created
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    dbContext.Database.Migrate();
    
    // Seed the first user as Administrator if no administrators exist
    var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
    var userManager = scope.ServiceProvider.GetRequiredService<UserManager<IdentityUser>>();
    
    if (!roleManager.RoleExistsAsync("Administrator").GetAwaiter().GetResult())
    {
        roleManager.CreateAsync(new IdentityRole("Administrator")).GetAwaiter().GetResult();
    }
    
    var admins = userManager.GetUsersInRoleAsync("Administrator").GetAwaiter().GetResult();
    if (admins.Count == 0)
    {
        var firstUser = userManager.Users.FirstOrDefault();
        if (firstUser != null)
        {
            userManager.AddToRoleAsync(firstUser, "Administrator").GetAwaiter().GetResult();
        }
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.MapAuthEndpoints();

// Minimal API endpoint for downloading save files directly
app.MapGet("/api/instances/{id:int}/saves/{filename}", (int id, string filename, InstanceManager manager) => 
{
    // Sanitize filename to prevent directory traversal
    var safeFilename = Path.GetFileName(filename);
    var savesDir = manager.GetSavesDirectory(id);
    var filePath = Path.Combine(savesDir, safeFilename);
    
    if (!System.IO.File.Exists(filePath)) 
        return Results.NotFound();
        
    return Results.File(filePath, "application/zip", safeFilename);
}).RequireAuthorization(policy => policy.AddAuthenticationSchemes(IdentityConstants.ApplicationScheme, "ApiKey").RequireAuthenticatedUser());

app.Run();
