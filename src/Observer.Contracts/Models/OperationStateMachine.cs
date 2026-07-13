namespace FullSpectrum.Observer.Contracts.Models;

public static class OperationStates
{
    public const string Received = "RECEIVED";
    public const string Adapting = "ADAPTING";
    public const string ValidatingSchema = "VALIDATING_SCHEMA";
    public const string ValidatingGovernance = "VALIDATING_GOVERNANCE";
    public const string SnapshotFixed = "SNAPSHOT_FIXED";
    public const string EngineRunning = "ENGINE_RUNNING";
    public const string AssemblingOutput = "ASSEMBLING_OUTPUT";
    public const string Persisting = "PERSISTING";
    public const string Completed = "COMPLETED";
    public const string Cancelling = "CANCELLING";
    public const string RejectedSchema = "REJECTED_SCHEMA";
    public const string NeedsEvidence = "NEEDS_EVIDENCE";
    public const string AdapterFailed = "ADAPTER_FAILED";
    public const string EngineFailed = "ENGINE_FAILED";
    public const string StoreFailed = "STORE_FAILED";
    public const string Cancelled = "CANCELLED";
    public const string TimedOut = "TIMED_OUT";
    public const string InternalFailed = "INTERNAL_FAILED";
}

public static class OperationStateMachine
{
    private static readonly IReadOnlyDictionary<string, HashSet<string>> Allowed =
        new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            [OperationStates.Received] = Set(OperationStates.Adapting, OperationStates.RejectedSchema, OperationStates.InternalFailed),
            [OperationStates.Adapting] = Set(OperationStates.ValidatingSchema, OperationStates.AdapterFailed),
            [OperationStates.ValidatingSchema] = Set(OperationStates.ValidatingGovernance, OperationStates.RejectedSchema),
            [OperationStates.ValidatingGovernance] = Set(OperationStates.SnapshotFixed, OperationStates.NeedsEvidence),
            [OperationStates.SnapshotFixed] = Set(OperationStates.EngineRunning, OperationStates.InternalFailed),
            [OperationStates.EngineRunning] = Set(OperationStates.AssemblingOutput, OperationStates.EngineFailed, OperationStates.Cancelling, OperationStates.TimedOut),
            [OperationStates.AssemblingOutput] = Set(OperationStates.Persisting, OperationStates.InternalFailed),
            [OperationStates.Persisting] = Set(OperationStates.Completed, OperationStates.StoreFailed),
            [OperationStates.Cancelling] = Set(OperationStates.Cancelled, OperationStates.InternalFailed),
        };

    public static bool IsTerminal(string state) => state is
        OperationStates.Completed or OperationStates.RejectedSchema or OperationStates.NeedsEvidence or
        OperationStates.AdapterFailed or OperationStates.EngineFailed or OperationStates.StoreFailed or
        OperationStates.Cancelled or OperationStates.TimedOut or OperationStates.InternalFailed;

    public static bool CanTransition(string current, string next) =>
        Allowed.TryGetValue(current, out HashSet<string>? states) && states.Contains(next);

    public static void EnsureTransition(string current, string next)
    {
        if (!CanTransition(current, next))
        {
            throw new InvalidOperationException($"Illegal operation transition: {current} -> {next}.");
        }
    }

    private static HashSet<string> Set(params string[] states) => new(states, StringComparer.Ordinal);
}
