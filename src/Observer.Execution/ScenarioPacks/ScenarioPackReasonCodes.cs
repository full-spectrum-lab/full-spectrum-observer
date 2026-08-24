namespace FullSpectrum.Observer.Execution.ScenarioPacks;

public static class ScenarioPackReasonCodes
{
    public const string ManifestInvalid = "SCENARIO_PACK_MANIFEST_INVALID";
    public const string DigestMismatch = "SCENARIO_PACK_DIGEST_MISMATCH";
    public const string SignatureMissing = "SCENARIO_PACK_SIGNATURE_MISSING";
    public const string SignatureInvalid = "SCENARIO_PACK_SIGNATURE_INVALID";
    public const string TrustRootMissing = "SCENARIO_PACK_TRUST_ROOT_MISSING";
    public const string ObserverIncompatible = "SCENARIO_PACK_OBSERVER_INCOMPATIBLE";
    public const string EngineIncompatible = "SCENARIO_PACK_ENGINE_INCOMPATIBLE";
    public const string ReferenceUnsafe = "SCENARIO_PACK_REFERENCE_UNSAFE";
    public const string ReferenceMissing = "SCENARIO_PACK_REFERENCE_MISSING";
    public const string IdentityConflict = "SCENARIO_PACK_IDENTITY_CONFLICT";
    public const string IdentityNotFound = "SCENARIO_PACK_IDENTITY_NOT_FOUND";
}
