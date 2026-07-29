namespace FullSpectrum.Observer.Store;

/// <summary>
/// Marker type confirming the Observer.Store assembly is loaded.
/// The v0.3 Observer Console local SQLite store (10 tables) lives here, distinct from the
/// v0.2 Foundation Evidence module (Observer.Evidence), which owns a different schema and
/// repository architecture.
/// </summary>
public static class ModuleMarker
{
    public const string ModuleName = "Observer.Store";
}
