using System;
using System.Diagnostics;
using System.IO;
using FullSpectrum.Observer.Contracts;

namespace FullSpectrum.Observer.Contracts;

/// <summary>
/// Deterministic, single-source runtime configuration resolver (M2-FIX-03).
///
/// Every runtime path derives from <see cref="RepositoryLayout.FindRoot"/> applied to
/// <see cref="AppContext.BaseDirectory"/> — never from the current working directory, a
/// source checkout, or <c>bin</c>/<c>obj</c>. This single rule fixes the IG5 ("Case Pack
/// directory is missing") and IG6 (worker / Engine root) failures simultaneously, because
/// the published package carries <c>baselines.lock.json</c> + <c>schemas/foundation-kernel</c>
/// at its root, so <c>FindRoot</c> resolves to the package root from any working directory.
///
/// The <c>FSP_PRIVATE_PYTHON</c> environment variable is the ONLY remaining environment read.
/// It is an escape hatch used by tests / diagnostics: it overrides the Python executable path
/// ONLY when it is a fully-qualified, existing file. In a formal product package
/// (<c>FSP_PRIVATE_PYTHON</c> unset) the Python interpreter resolves to
/// <c>&lt;PackageRoot&gt;/runtime/python/python.exe</c> provisioned by <c>publish-observer.ps1</c>.
/// </summary>
public static class RuntimeConfigurationResolver
{
    /// <summary>Input to <see cref="Resolve"/>. All members are optional.</summary>
    /// <param name="StartPath">
    /// Directory to begin the repo-root walk from. Defaults to <see cref="AppContext.BaseDirectory"/>
    /// (the host's own assembly directory — correct for both dev and a movable published package).
    /// </param>
    /// <param name="PythonExecutableOverride">
    /// Explicit Python override. When <c>null</c>, the resolver falls back to the
    /// <c>FSP_PRIVATE_PYTHON</c> environment variable. The override is only honored when it is a
    /// fully-qualified, existing file.
    /// </param>
    public sealed record RuntimeResolutionInput(string? StartPath = null, string? PythonExecutableOverride = null);

    /// <summary>Resolved, package-relative runtime paths (all absolute).</summary>
    public sealed record RuntimeConfiguration(
        string PackageRoot,
        string PythonExecutablePath,
        string WorkerScriptPath,
        string EngineRootPath,
        string WorkerLockPath,
        string SchemaDirectory,
        string CasePackDirectory);

    /// <summary>
    /// Resolves the runtime configuration from the package root. Throws
    /// <see cref="DirectoryNotFoundException"/> if the package root cannot be located.
    /// </summary>
    public static RuntimeConfiguration Resolve(RuntimeResolutionInput? input = null)
    {
        input ??= new RuntimeResolutionInput();
        string start = input.StartPath ?? AppContext.BaseDirectory;
        string packageRoot = RepositoryLayout.FindRoot(start);

        return new RuntimeConfiguration(
            PackageRoot: packageRoot,
            PythonExecutablePath: ResolvePythonExecutable(packageRoot, input.PythonExecutableOverride),
            WorkerScriptPath: Path.Combine(packageRoot, "engine", "worker", "worker.py"),
            EngineRootPath: Path.Combine(packageRoot, "engine", "vendor", "full-spectrum-engine"),
            WorkerLockPath: Path.Combine(packageRoot, "engine", "worker.lock.json"),
            SchemaDirectory: Path.Combine(packageRoot, "schemas", "foundation-kernel"),
            CasePackDirectory: Path.Combine(packageRoot, "packs", "foundation-case005"));
    }

    /// <summary>
    /// Python executable = <c>&lt;PackageRoot&gt;/runtime/python/python.exe</c> by default.
    /// Overridden ONLY when <paramref name="overridePath"/> (or, when null, the
    /// <c>FSP_PRIVATE_PYTHON</c> env var) is a fully-qualified, existing file — the test/diagnostic
    /// escape hatch. The returned path is always absolute.
    /// </summary>
    private static string ResolvePythonExecutable(string packageRoot, string? overridePath)
    {
        if (string.IsNullOrWhiteSpace(overridePath))
        {
            overridePath = Environment.GetEnvironmentVariable("FSP_PRIVATE_PYTHON");
        }

        if (!string.IsNullOrWhiteSpace(overridePath)
            && Path.IsPathFullyQualified(overridePath)
            && File.Exists(overridePath))
        {
            return Path.GetFullPath(overridePath);
        }

        return Path.Combine(packageRoot, "runtime", "python", "python.exe");
    }
}
