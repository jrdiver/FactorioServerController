using System.Security.Claims;
using System.Text.Encodings.Web;
using FactorioLibrary.Data;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace FactorioServerController.Auth;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions { }

public class ApiKeyAuthenticationHandler : AuthenticationHandler<ApiKeyAuthenticationOptions>
{
    private readonly AppDbContext _dbContext;

    public ApiKeyAuthenticationHandler(
        IOptionsMonitor<ApiKeyAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        AppDbContext dbContext)
        : base(options, logger, encoder)
    {
        _dbContext = dbContext;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 1. Try to get API key from headers
        if (!Request.Headers.TryGetValue("x-api-key", out var apiKeyHeaderValues))
        {
            // 2. Try to get API key from query string (useful for direct download links in scripts/browser)
            if (!Request.Query.TryGetValue("apikey", out apiKeyHeaderValues))
            {
                return AuthenticateResult.NoResult();
            }
        }

        var providedApiKey = apiKeyHeaderValues.FirstOrDefault();

        if (string.IsNullOrEmpty(providedApiKey))
        {
            return AuthenticateResult.NoResult();
        }

        var apiKeyRecord = await _dbContext.UserApiKeys
            .Include(x => x.User)
            .FirstOrDefaultAsync(x => x.ApiKey == providedApiKey);

        if (apiKeyRecord == null || apiKeyRecord.User == null)
        {
            return AuthenticateResult.Fail("Invalid API Key provided.");
        }

        var claims = new[] 
        {
            new Claim(ClaimTypes.NameIdentifier, apiKeyRecord.UserId),
            new Claim(ClaimTypes.Name, apiKeyRecord.User.UserName ?? "API_User"),
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
