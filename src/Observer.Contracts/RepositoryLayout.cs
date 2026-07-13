namespace FullSpectrum.Observer.Contracts;

public static class RepositoryLayout
{
    public static string FindRoot(string? start = null)
    {
        DirectoryInfo? directory = new(start ?? Directory.GetCurrentDirectory());
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "baselines.lock.json")) &&
                Directory.Exists(Path.Combine(directory.FullName, "schemas", "foundation-kernel")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Cannot locate the Full Spectrum Observer repository root.");
    }

    public static string SchemaDirectory(string? start = null) => Path.Combine(FindRoot(start), "schemas", "foundation-kernel");
}
