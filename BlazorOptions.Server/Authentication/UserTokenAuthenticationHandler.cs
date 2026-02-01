using System.Security.Claims;
using System.Text.Encodings.Web;
using BlazorOptions.Server.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace BlazorOptions.Server.Authentication;

public sealed class UserTokenAuthenticationHandler : AuthenticationHandler<UserTokenAuthenticationOptions>
{
    private readonly UserRegistryService _registry;

    public UserTokenAuthenticationHandler(
        IOptionsMonitor<UserTokenAuthenticationOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        UserRegistryService registry)
        : base(options, logger, encoder)
    {
        _registry = registry;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(UserTokenAuthenticationOptions.TokenHeaderName, out var values))
        {
            return AuthenticateResult.NoResult();
        }

        var token = values.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        var user = await _registry.GetUserByTokenAsync(token);
        if (user is null)
        {
            return AuthenticateResult.Fail("Invalid token.");
        }

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id),
            new Claim(ClaimTypes.Name, user.UserName)
        };

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return AuthenticateResult.Success(ticket);
    }
}

public sealed class UserTokenAuthenticationOptions : AuthenticationSchemeOptions
{
    public const string SchemeName = "UserToken";
    public const string TokenHeaderName = "X-User-Token";
}
