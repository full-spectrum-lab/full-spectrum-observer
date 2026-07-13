namespace FullSpectrum.Observer.Contracts.Schema;

public sealed record SchemaValidationIssue(string ReasonCode, string JsonPath, string Message);

public sealed record SchemaValidationReport(string SchemaId, IReadOnlyList<SchemaValidationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;
}
