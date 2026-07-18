using System;
using System.IO;
using System.Linq;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Host.Web;
using FullSpectrum.Observer.Host.Web.Services;
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
builder.Services.AddSingleton(_ => EngineV15Composition.Create(new EngineV15Options
{
    PythonExecutablePath = builder.Configuration["EngineV15:PythonExecutablePath"] ?? string.Empty,
    EngineRootPath = builder.Configuration["EngineV15:EngineRootPath"] ?? string.Empty,
}));

var app = builder.Build();

// Surface the resolved (stable, absolute) data directory on the System Information page.
app.Services.GetRequiredService<SystemDiagnostics>().DataDirectory = dataDirectory;

// Bootstrap-token handshake seam (L3/L4). Full HttpOnly session exchange is a subsequent module.
app.UseBootstrapTokenGate();
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
