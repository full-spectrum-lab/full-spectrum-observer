using System.Text.Json;

namespace FullSpectrum.Observer.Host.Cli;

internal static class LaunchSettings
{
    internal const string FileName = "observer-launch-settings.json";

    internal static string? ResolveDataDirectoryOverride(
        string packageRoot,
        string? commandLineOverride,
        string? environmentOverride)
    {
        if (!string.IsNullOrWhiteSpace(commandLineOverride))
        {
            return commandLineOverride;
        }
        if (!string.IsNullOrWhiteSpace(environmentOverride))
        {
            return environmentOverride;
        }

        string absoluteRoot = Path.GetFullPath(packageRoot);
        string? parent = Directory.GetParent(absoluteRoot.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar))?.FullName;
        string[] candidates = parent is null
            ? [Path.Combine(absoluteRoot, FileName)]
            :
            [
                Path.Combine(absoluteRoot, FileName),
                Path.Combine(parent, FileName),
            ];

        string? path = candidates.FirstOrDefault(File.Exists);
        return path is null ? null : ReadDataDirectory(path);
    }

    private static string ReadDataDirectory(string path)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"启动设置必须是 JSON 对象：{path}");
        }

        string[] names = root.EnumerateObject().Select(property => property.Name).ToArray();
        if (names.Length != 1 || !names.Contains("data_directory", StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"启动设置只允许 data_directory 字段：{path}");
        }
        JsonElement value = root.GetProperty("data_directory");
        string? dataDirectory = value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;
        if (string.IsNullOrWhiteSpace(dataDirectory) || !Path.IsPathFullyQualified(dataDirectory))
        {
            throw new InvalidDataException(
                $"启动设置 data_directory 必须是非空绝对路径：{path}");
        }

        return dataDirectory;
    }
}
