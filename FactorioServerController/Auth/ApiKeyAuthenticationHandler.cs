using System.Security.Claims;
using System.Text.Encodings.Web;
using FactorioLibrary.Data;
using FactorioLibrary.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace FactorioServerController.Auth;

public class ApiKeyAuthenticationOptions : AuthenticationSchemeOptions { }

public class ApiKeyAuthenticationHandler(IOptionsMonitor<ApiKeyAuthenticationOptions> options, ILoggerFactory logger, UrlEncoder encoder, AppDbContext dbContext) : AuthenticationHandler<ApiKeyAuthenticationOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        // 1. Try to get API key from headers
        if (!Request.Headers.TryGetValue("x-api-key", out StringValues apiKeyHeaderValues))
            // 2. Try to get API key from query string (useful for direct download links in scripts/browser)
            if (!Request.Query.TryGetValue("apikey", out apiKeyHeaderValues))
                return AuthenticateResult.NoResult();

        string? providedApiKey = apiKeyHeaderValues.FirstOrDefault();

        if (string.IsNullOrEmpty(providedApiKey))
            return AuthenticateResult.NoResult();

        UserApiKey? apiKeyRecord = await dbContext.UserApiKeys.Include(x => x.User).FirstOrDefaultAsync(x => x.ApiKey == providedApiKey);

        if (apiKeyRecord == null || apiKeyRecord.User == null)
            return AuthenticateResult.Fail("Invalid API Key provided.");

        Claim[] claims =
        [
            new Claim(ClaimTypes.NameIdentifier, apiKeyRecord.UserId),
            new Claim(ClaimTypes.Name, apiKeyRecord.User.UserName ?? "API_User"),
        ];

        ClaimsIdentity identity = new(claims, Scheme.Name);
        ClaimsPrincipal principal = new(identity);
        AuthenticationTicket ticket = new(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}
