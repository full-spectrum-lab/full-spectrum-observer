using System.Net;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>
/// Internal, loopback-only stop channel (ADR-005 L2/L3, M2-FIX-03 T11).
///
/// The Launcher mints a one-time <c>--stop-token</c> (32-byte hex) and passes it to the Web Host.
/// This middleware exposes <c>POST /stop</c> guarded by a token gate that validates the token in
/// constant time (same seam as <see cref="BootstrapTokenGate"/>). The handler calls
/// <see cref="IHostApplicationLifetime.StopApplication"/> to trigger a clean graceful shutdown.
///
/// This is NOT a public control endpoint:
/// <list type="bullet">
///   <item><description>Kestrel binds 127.0.0.1 only (enforced in <c>Program.cs</c>), and this
///     middleware rejects any request whose remote address is not loopback.</description></item>
///   <item><description>The route is token-gated; a missing / wrong / expired token is rejected with 403.</description></item>
/// </list>
/// </summary>
public sealed class StopTokenContext
{
    public string? Token { get; }

    public DateTimeOffset IssuedAt { get; }

    public TimeSpan Lifetime { get; }

    public StopTokenContext(string? token, TimeSpan lifetime)
    {
        Token = token;
        IssuedAt = DateTimeOffset.UtcNow;
        Lifetime = lifetime;
    }

    public bool IsExpired(DateTimeOffset now) => now - IssuedAt > Lifetime;

    /// <summary>Constant-time comparison so the token never leaks via timing (L9).</summary>
    public static bool ConstantTimeEquals(string? a, string? b) =>
        BootstrapTokenContext.ConstantTimeEquals(a, b);
}

/// <summary>Registers the <c>/stop</c> route in the request pipeline.</summary>
public static class StopChannelExtensions
{
    public static IApplicationBuilder MapStopChannel(this IApplicationBuilder app, string stopToken)
    {
        ArgumentNullException.ThrowIfNull(app);
        var context = new StopTokenContext(stopToken, TimeSpan.FromSeconds(30));

        app.Use(async (httpContext, next) =>
        {
            if (httpContext.Request.Path == "/stop"
                && HttpMethods.IsPost(httpContext.Request.Method))
            {
                await HandleStopAsync(httpContext, context).ConfigureAwait(false);
                return;
            }

            await next(httpContext).ConfigureAwait(false);
        });

        return app;
    }

    private static async Task HandleStopAsync(HttpContext httpContext, StopTokenContext context)
    {
        // L2: only loopback callers may reach the stop channel.
        if (!IsLoopback(httpContext.Connection.RemoteIpAddress))
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        string? presented = httpContext.Request.Headers["X-Stop-Token"].FirstOrDefault()
            ?? httpContext.Request.Query["token"];
        if (string.IsNullOrEmpty(presented)
            || !StopTokenContext.ConstantTimeEquals(presented, context.Token)
            || context.IsExpired(DateTimeOffset.UtcNow))
        {
            httpContext.Response.StatusCode = (int)HttpStatusCode.Forbidden;
            return;
        }

        var lifetime = httpContext.RequestServices.GetRequiredService<IHostApplicationLifetime>();
        httpContext.Response.StatusCode = (int)HttpStatusCode.OK;
        await httpContext.Response.WriteAsync("STOP ACCEPTED").ConfigureAwait(false);
        // Triggers IHostApplicationLifetime.ApplicationStopping -> analysis cancellation -> clean exit.
        lifetime.StopApplication();
    }

    private static bool IsLoopback(IPAddress? address)
    {
        if (address is null)
        {
            return false;
        }
        return IPAddress.IsLoopback(address)
            || address.Equals(IPAddress.Loopback)
            || address.Equals(IPAddress.IPv6Loopback);
    }
}
