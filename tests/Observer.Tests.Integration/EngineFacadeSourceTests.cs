using System.Text.Json;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.EngineFacade;

namespace FullSpectrum.Observer.Tests.Integration;

public static class EngineFacadeSourceTests
{
    public static async Task RunAsync(string repositoryRoot, string pythonExecutable)
    {
        var options = new EngineFacadeOptions
        {
            PythonExecutablePath = Path.GetFullPath(pythonExecutable),
            WorkerScriptPath = Path.Combine(repositoryRoot, "engine", "worker", "worker.py"),
            EngineRootPath = Path.Combine(repositoryRoot, "engine", "vendor", "full-spectrum-engine"),
            WorkerLockPath = Path.Combine(repositoryRoot, "engine", "worker.lock.json"),
            SchemaDirectory = Path.Combine(repositoryRoot, "schemas", "foundation-kernel"),
        };
        var facade = new PythonWorkerEngineFacade(options);
        JsonElement scenario = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repositoryRoot, "packs", "foundation-case005", "case005.input.json"))).RootElement.Clone();
        var request = new EngineFacadeRequest
        {
            Protocol = "fs-observer-engine-facade/1",
            RequestId = "12345678-1234-4234-8234-123456789abc",
            Operation = "evaluate",
            Engine = JsonSerializer.SerializeToElement(new { version="v1.5.0", commit="88493007d4e00344c70a70ed0e5a5d652dec86f5" }),
            Seed = 42,
            FixedTimeUtc = "2026-07-04T00:00:00Z",
            Scenario = scenario,
            OutputSerialization = "FSE-PYJSON-1",
        };
        var result = await facade.EvaluateAsync(request, TimeSpan.FromSeconds(30), CancellationToken.None);
        if (result.Response.Status != "SUCCESS" || result.Response.Output is null)
            throw new InvalidOperationException("TR-FK-CON-004/OUT-001 Engine Facade smoke failed.");
    }
}
