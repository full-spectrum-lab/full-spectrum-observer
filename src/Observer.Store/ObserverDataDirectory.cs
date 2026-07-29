using System;
using System.IO;

namespace FullSpectrum.Observer.Store;

/// <summary>
/// Resolves the durable data directory used by the Host processes (CLI <c>serve</c> and the Web host).
///
/// <para>The default is a stable, per-user, always-writable local application-data directory
/// (e.g. <c>%LOCALAPPDATA%/full-spectrum-observer/data</c> on Windows). It is independent of the
/// current working directory — so it is never a fragile <c>/tmp</c> or cwd-relative path — and is
/// created automatically on first use. An explicit override (CLI <c>--data-dir</c> or the
/// <c>Observer:DataDirectory</c> configuration key) is always honored and is also created when
/// missing.</para>
/// </summary>
public static class ObserverDataDirectory
{
    /// <summary>
    /// Resolves and ensures the existence of the data directory.
    /// </summary>
    /// <param name="overridePath">Optional explicit path. When null/empty/whitespace, the per-user
    /// local application-data directory is used.</param>
    /// <returns>An absolute, existing directory path.</returns>
    public static string Resolve(string? overridePath)
    {
        // Priority: explicit --data-dir (or Observer:DataDirectory config) -> stable default.
        // Relative override paths are FORBIDDEN: they must never be silently resolved against the
        // current working directory (cwd-independent data-directory contract).
        if (string.IsNullOrWhiteSpace(overridePath))
        {
            string resolved = DefaultLocalDataDirectory();
            Directory.CreateDirectory(resolved);
            return resolved;
        }

        if (!Path.IsPathRooted(overridePath))
        {
            throw new ArgumentException(
                $"Data directory override must be an absolute path; relative paths are not allowed " +
                $"(received: '{overridePath}'). Use --data-dir with an absolute path, or omit it to use " +
                $"the default %LOCALAPPDATA%/full-spectrum-observer/data location.",
                nameof(overridePath));
        }

        string absolute = Path.GetFullPath(overridePath);
        Directory.CreateDirectory(absolute);
        return absolute;
    }

    private static string DefaultLocalDataDirectory()
    {
        // Prefer the per-user local application-data folder; fall back through the roaming
        // profile and user-profile folders before the current directory as a last resort.
        // We deliberately avoid %TEMP% so the default store is never placed in a volatile
        // /tmp location that could be cleared between runs.
        string baseDirectory = FirstNonEmpty(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        if (string.IsNullOrWhiteSpace(baseDirectory))
        {
            baseDirectory = ".";
        }

        return Path.Combine(baseDirectory, "full-spectrum-observer", "data");
    }

    private static string FirstNonEmpty(params string[] candidates)
    {
        foreach (string candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return string.Empty;
    }
}
