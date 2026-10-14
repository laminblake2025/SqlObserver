using System.Runtime.Versioning;
using System.Security.Principal;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Negotiate;

namespace SqlObserver.LabDashboardHost;

// The browser authenticates locally; only the launching Windows account may
// exercise the separate, process-owned upstream credentials.
[SupportedOSPlatform("windows")]
public sealed class OwnerIdentityMiddleware(RequestDelegate next, SecurityIdentifier ownerSid)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            await context.ChallengeAsync(NegotiateDefaults.AuthenticationScheme).ConfigureAwait(false);
            return;
        }
        if (context.User.Identity is not WindowsIdentity identity || identity.User != ownerSid)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(context).ConfigureAwait(false);
    }
}