namespace FullSpectrum.Observer.Host.Cli;

public sealed class CliOptions
{
    private readonly Dictionary<string, string?> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static CliOptions Parse(IEnumerable<string> args)
    {
        var options = new CliOptions();
        string[] values = args.ToArray();
        for (int index = 0; index < values.Length; index++)
        {
            string current = values[index];
            if (!current.StartsWith("--", StringComparison.Ordinal))
                continue;
            if (index + 1 < values.Length && !values[index + 1].StartsWith("--", StringComparison.Ordinal))
                options._values[current] = values[++index];
            else
                options._flags.Add(current);
        }
        return options;
    }

    public bool Has(string name) => _flags.Contains(name) || _values.ContainsKey(name);

    public string? Get(string name) => _values.TryGetValue(name, out string? value) ? value : null;

    public string Require(string name) => Get(name)
        ?? throw new ArgumentException($"Missing option: {name}.");

    public int GetInt(string name, int fallback)
    {
        string? value = Get(name);
        if (value is null)
            return fallback;
        return int.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out int parsed)
            ? parsed
            : throw new ArgumentException($"Option {name} must be an integer.");
    }
}
