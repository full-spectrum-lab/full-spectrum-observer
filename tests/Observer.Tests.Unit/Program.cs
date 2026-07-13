using System.Text;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Contracts.ReasonCodes;
using FullSpectrum.Observer.Contracts.Canonicalization;

var tests = new List<(string Id, Action Execute)>
{
    ("TR-FK-UNIT-001", CanonicalObjectOrderIsStable),
    ("TR-FK-UNIT-002", CanonicalNumberNormalizationIsStable),
    ("TR-FK-UNIT-002B", InvalidCanonicalNumberHasStableReasonCode),
    ("TR-FK-UNIT-003", ReasonCodeCatalogIsStable),
    ("TR-FK-UNIT-005", SelfDigestExcludesOnlyNamedField),
};

int failures = 0;
foreach ((string id, Action execute) in tests)
{
    try
    {
        execute();
        Console.WriteLine($"PASS {id}");
    }
    catch (Exception exception)
    {
        failures++;
        Console.Error.WriteLine($"FAIL {id}: {exception.Message}");
    }
}
return failures == 0 ? 0 : 1;

static void CanonicalObjectOrderIsStable()
{
    byte[] left = FsObsCanonicalizer.Canonicalize(Encoding.UTF8.GetBytes("{\"b\":2,\"a\":1}"));
    byte[] right = FsObsCanonicalizer.Canonicalize(Encoding.UTF8.GetBytes("{\"a\":1,\"b\":2}"));
    AssertEqual("{\"a\":1,\"b\":2}", Encoding.UTF8.GetString(left));
    AssertEqual(Encoding.UTF8.GetString(left), Encoding.UTF8.GetString(right));
}

static void CanonicalNumberNormalizationIsStable()
{
    byte[] result = FsObsCanonicalizer.Canonicalize(Encoding.UTF8.GetBytes("[1.2300,1e2,-0,123456789012345678901234567890]"));
    AssertEqual("[1.23,100,0,123456789012345678901234567890]", Encoding.UTF8.GetString(result));
}


static void InvalidCanonicalNumberHasStableReasonCode()
{
    try
    {
        _ = FsObsCanonicalizer.Canonicalize(Encoding.UTF8.GetBytes("{\"value\":1e100}"));
        throw new InvalidOperationException("Expected canonicalization failure.");
    }
    catch (CanonicalizationException exception)
    {
        AssertEqual("AUDIT_CANONICALIZATION_FAILED", exception.ReasonCode);
    }
}

static void ReasonCodeCatalogIsStable()
{
    string path = Path.Combine(RepositoryLayout.SchemaDirectory(), "reason-codes.v1.json");
    ReasonCodeCatalog catalog = ReasonCodeCatalog.Load(path);
    AssertEqual("50", catalog.Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
    if (!catalog.TryGetDomain(FoundationReasonCodes.AUDIT_CHAIN_BROKEN, out string domain))
    {
        throw new InvalidOperationException("AUDIT reason code is missing.");
    }
    AssertEqual("AUDIT", domain);
}

static void SelfDigestExcludesOnlyNamedField()
{
    byte[] json = Encoding.UTF8.GetBytes("{\"b\":2,\"manifest_sha256\":\"placeholder\",\"a\":1}");
    string actual = SelfDigestCalculator.Compute(json, "manifest_sha256");
    string expected = FsObsCanonicalizer.Sha256Hex(Encoding.UTF8.GetBytes("{\"a\":1,\"b\":2}"));
    AssertEqual(expected, actual);
}

static void AssertEqual(string expected, string actual)
{
    if (!string.Equals(expected, actual, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"Expected '{expected}', actual '{actual}'.");
    }
}
