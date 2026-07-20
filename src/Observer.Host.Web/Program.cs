using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using FullSpectrum.Observer.EngineFacade;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using FullSpectrum.Observer.Host.Web;
using FullSpectrum.Observer.Host.Web.Services;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Store;
using Microsoft.Extensions.DependencyInjection;

var builder = WebApplication.CreateBuilder(args);

// ADR-005 L2/L5: loopback-only binding. The Launcher supplies a random loopback port via
// --urls; we bind 127.0.0.1:<port> exclusively and REFUSE any non-loopback address (we never
// fall back to 0.0.0.0). Module 1 scope is L1~L3; the Secure session cookie (L4) follows.
int port = ResolveLoopbackPort(args);
builder.WebHost.UseKestrel(options => options.ListenLocalhost(port));

// ADR-005 L3: one-time bootstrap token minted by the Launcher and passed via --bootstrap-token.
// The Host validates it once (BootstrapTokenGate seam); it is never logged or persisted (L9/L16).
string? bootstrapToken = GetOption(args, "--bootstrap-token");
string? stopToken = GetOption(args, "--stop-token");
string requestedUrls = GetOption(args, "--urls");
builder.Services.AddSingleton(new BootstrapTokenContext(bootstrapToken, TimeSpan.FromSeconds(30)));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Single-user loopback console: a singleton store + services avoid per-circuit state skew.
// Install the patched SourceGear native SQLite provider before any connection opens.
SqliteRuntimeBootstrap.Initialize();
string dataDirectory = ObserverDataDirectory.Resolve(builder.Configuration["Observer:DataDirectory"]);
Console.WriteLine($"[Observer] Resolved data directory: {dataDirectory}");
string dbPath = Path.Combine(dataDirectory, "observer_console.db");

var store = new ObserverStore(dbPath);
await store.EnsureSchemaAsync();

builder.Services.AddSingleton(store);
builder.Services.AddSingleton<JobStatusPresenter>();
builder.Services.AddSingleton<AuditContext>();
builder.Services.AddSingleton<SubjectCatalog>();
builder.Services.AddSingleton<KnowledgeCatalog>();
builder.Services.AddSingleton<AuditViewer>();
builder.Services.AddSingleton<AnalysisWorkspace>();
builder.Services.AddSingleton<SystemDiagnostics>();
builder.Services.AddSingleton<Orchestrator>();
builder.Services.AddSingleton<IntakeAdapter>();
builder.Services.AddSingleton<OutputAdapter>();
// M2-FIX-03: a single cancellation source that is signalled when the host begins stopping, so any
// in-flight analysis (and therefore the Engine worker process) is cancelled cleanly on shutdown.
builder.Services.AddSingleton<AnalysisShutdownToken>();
builder.Services.AddSingleton(sp =>
{
    // M2-FIX-03: resolve every runtime path via the shared resolver instead of reading
    // FSP_PRIVATE_PYTHON directly. The resolver derives PackageRoot from AppContext.BaseDirectory,
    // so the product works from any working directory with the env var UNSET (the formal package
    // ships runtime/python/python.exe). FSP_PRIVATE_PYTHON remains only a test/diagnostic override.
    var config = FullSpectrum.Observer.Contracts.RuntimeConfigurationResolver.Resolve();
    var options = new EngineFacadeOptions
    {
        PythonExecutablePath = config.PythonExecutablePath,
        WorkerScriptPath = config.WorkerScriptPath,
        EngineRootPath = config.EngineRootPath,
        WorkerLockPath = config.WorkerLockPath,
        SchemaDirectory = Path.GetFullPath(config.SchemaDirectory),
    };
    return EngineV15Composition.Create(options);
});

var app = builder.Build();

// M2-FIX-03 (T12): when the host begins stopping, signal the analysis cancellation source so any
// in-flight Engine worker is terminated via the existing EngineFacade cancel path (no forced kill).
app.Lifetime.ApplicationStopping.Register(() =>
    app.Services.GetRequiredService<AnalysisShutdownToken>().Signal());

// Surface the resolved (stable, absolute) data directory and the actual runtime endpoints
// on the System Information page.
var systemDiagnostics = app.Services.GetRequiredService<SystemDiagnostics>();
systemDiagnostics.DataDirectory = dataDirectory;
systemDiagnostics.RequestedEndpoint = requestedUrls;
// The authoritative binding is what Kestrel actually bound (IServerAddressesFeature),
// not a static constant. Capture it once the server has started listening.
app.Lifetime.ApplicationStarted.Register(() =>
{
    try
    {
        var server = app.Services.GetRequiredService<IServer>();
        var feature = server.Features.Get<IServerAddressesFeature>();
        if (feature?.Addresses is not null)
        {
            systemDiagnostics.ActualBoundEndpoints = feature.Addresses.ToList();
        }
    }
    catch
    {
        // keep the requested-endpoint fallback already set
    }
});

// Bootstrap-token handshake seam (L3/L4). Full HttpOnly session exchange is a subsequent module.
app.UseBootstrapTokenGate();
// M2-FIX-03 (T11): internal, loopback-only stop channel guarded by the Launcher-minted stop token.
// The handler calls IHostApplicationLifetime.StopApplication() for a clean graceful shutdown.
app.MapStopChannel(stopToken ?? string.Empty);
app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static int ResolveLoopbackPort(string[] args)
{
    string? urls = GetOption(args, "--urls");
    if (urls is null)
    {
        return 5180;
    }
    if (!Uri.TryCreate(urls, UriKind.Absolute, out Uri? uri) || !IsLoopbackHost(uri.Host))
    {
        throw new InvalidOperationException($"仅允许 loopback 绑定；拒绝非本地地址：{urls}");
    }
    return uri.Port;
}

static bool IsLoopbackHost(string host) =>
    host is "127.0.0.1" or "::1" or "localhost" or "[::1]";

static string? GetOption(string[] args, string name)
{
    for (int index = 0; index < args.Length; index++)
    {
        if (args[index] == name && index + 1 < args.Length)
        {
            return args[index + 1];
        }
        if (args[index].StartsWith(name + "="))
        {
            return args[index].Substring(name.Length + 1);
        }
    }
    return null;
}
