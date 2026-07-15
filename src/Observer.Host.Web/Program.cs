using System.IO;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Host.Web;
using FullSpectrum.Observer.Host.Web.Services;
using FullSpectrum.Observer.Store;

var builder = WebApplication.CreateBuilder(args);

// Loopback-only binding. Kestrel ListenLocalhost binds 127.0.0.1 / ::1 exclusively and NEVER
// 0.0.0.0 (network boundary red line). Do not add any non-loopback endpoint here.
builder.WebHost.UseKestrel(options => options.ListenLocalhost(5180));

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Single-user loopback console: a singleton store + services avoid per-circuit state skew.
string dataDirectory = builder.Configuration["Observer:DataDirectory"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "data");
Directory.CreateDirectory(dataDirectory);
string dbPath = Path.Combine(dataDirectory, "observer_console.db");

var store = new ObserverStore(dbPath);
await store.EnsureSchemaAsync();

builder.Services.AddSingleton(store);
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

app.UseStaticFiles();
app.UseAntiforgery();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();
