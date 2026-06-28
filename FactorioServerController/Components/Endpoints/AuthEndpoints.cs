using FactorioLibrary.Data;
using FactorioLibrary.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace FactorioServerController.Components.Endpoints;

public static class AuthEndpoints
{
    public static void MapAuthEndpoints(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/api/auth");

        group.MapPost("/login", async (
            [FromForm] string username, 
            [FromForm] string password,
            SignInManager<IdentityUser> signInManager,
            UserManager<IdentityUser> userManager) =>
        {
            var user = await userManager.FindByNameAsync(username);
            if (user != null)
            {
                var result = await signInManager.PasswordSignInAsync(user, password, isPersistent: true, lockoutOnFailure: false);
                if (result.Succeeded)
                {
                    return Results.Redirect("/");
                }
            }
            return Results.Redirect("/login?error=Invalid credentials");
        }).DisableAntiforgery(); // In a real app we'd use antiforgery, but for simplicity in Blazor forms

        group.MapPost("/setup", async (
            [FromForm] string username, 
            [FromForm] string password,
            [FromForm] string confirmPassword,
            UserManager<IdentityUser> userManager,
            SignInManager<IdentityUser> signInManager,
            AppDbContext db) =>
        {
            if (userManager.Users.Any())
            {
                return Results.Redirect("/login"); // Only allow setup if no users exist
            }

            if (password != confirmPassword)
            {
                return Results.Redirect("/setup?error=Passwords do not match");
            }

            var user = new IdentityUser { UserName = username };
            var result = await userManager.CreateAsync(user, password);
            if (result.Succeeded)
            {
                // Generate default API key for the admin
                var apiKey = new UserApiKey
                {
                    UserId = user.Id,
                    ApiKey = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")
                };
                db.UserApiKeys.Add(apiKey);
                await db.SaveChangesAsync();

                await signInManager.SignInAsync(user, isPersistent: true);
                return Results.Redirect("/");
            }
            
            return Results.Redirect($"/setup?error={Uri.EscapeDataString(result.Errors.FirstOrDefault()?.Description ?? "Error")}");
        }).DisableAntiforgery();

        group.MapPost("/logout", async (SignInManager<IdentityUser> signInManager) =>
        {
            await signInManager.SignOutAsync();
            return Results.Redirect("/login");
        }).DisableAntiforgery();
    }
}
