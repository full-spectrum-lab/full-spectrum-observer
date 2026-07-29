using System.Diagnostics;
using Microsoft.AspNetCore.Builder;

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>
/// Bootstrap-token handshake seam (ADR-005 L3/L4/L9/L12). On the Launcher's first request the
/// token is presented as <c>?bt=&lt;token&gt;</c>; this middleware validates it (constant time,
/// not expired) and marks the handshake verified. The token is then immediately dropped and must
/// not appear again in the URL, logs, or diagnostics.
///
/// SCOPE NOTE (Module 1): this middleware establishes the validation seam only. The full exchange
/// — issuing the HttpOnly+Secure+SameSite=Strict session cookie, one-time token consumption, and
/// rejecting post-handshake requests that lack a valid session (L4/L12) — is implemented in a
/// subsequent module. Until then this middleware validates but does NOT yet block unauthenticated
/// requests, so the Blazor shell can load.
/// </summary>
public sealed class BootstrapTokenGate
{
    private readonly RequestDelegate _next;
    private readonly BootstrapTokenContext _context;

    public BootstrapTokenGate(RequestDelegate next, BootstrapTokenContext context)
    {
        _next = next;
        _context = context;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        if (!_context.HandshakeVerified && !string.IsNullOrEmpty(_context.Token))
        {
            string? presented = context.Request.Query["bt"];
            if (!string.IsNullOrEmpty(presented)
                && BootstrapTokenContext.ConstantTimeEquals(presented, _context.Token)
                && !_context.IsExpired(DateTimeOffset.UtcNow))
            {
                _context.HandshakeVerified = true;
                // Subsequent module: Set-Cookie (HttpOnly; Secure; SameSite=Strict) + invalidate token.
                // Do NOT echo the token back to the client.
            }
        }

        await _next(context);
    }
}

/// <summary>Registers <see cref="BootstrapTokenGate"/> in the pipeline.</summary>
public static class BootstrapTokenGateExtensions
{
    public static IApplicationBuilder UseBootstrapTokenGate(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseMiddleware<BootstrapTokenGate>();
    }
}
