using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

#nullable enable

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>
/// Deterministic, IO-free payload assembler for the v0.3 Web console forms.
///
/// This is the SINGLE chokepoint that turns the human-facing structured form fields into the
/// JSON payload strings that the catalogs persist verbatim. By centralizing assembly here:
///   - key order is fixed and stable across runs (no dictionary / POCO ordering surprises);
///   - string values use standard JSON escaping (quotes, backslash, control chars such as newlines);
///   - output is compact (no indentation / whitespace) so a given input always yields identical bytes.
///
/// <para><b>Scope note (cross-project decision):</b> the shapes produced here are an
/// <i>Observer v0.3 storage projection</i> only — they are NOT declared as a whole-system
/// canonical schema.</para>
/// <list type="bullet">
///   <item><description><c>SUBJECT_THREE_FIELD_CANONICAL = NO</c>: <c>{display_name, boundary, owner_operator}</c>
///   is the v0.3 form projection of a subject declaration; the formal Engine contract
///   (<c>SubjectDeclaration v1.1</c>) is richer and is left unchanged.</description></item>
///   <item><description><c>TITLE_BODY_CANONICAL_SCHEMA = NO</c>: <c>{title, body}</c> is the v0.3
///   knowledge storage projection; knowledge governance semantics (identity/version/source/scope/
///   responsibility/revocation/conflict/audit/replay) are intentionally out of scope for v0.3.</description></item>
/// </list>
/// <para>The produced subject payload is consumed verbatim by <c>IntakeAdapter</c> as the Engine
/// <c>declaration</c> JsonElement, so the three-key structure MUST be preserved (Engine contract
/// unchanged). The knowledge payload is opaque to the Engine (only its digest is transmitted), so
/// only the <c>Digest = SHA256(UTF-8(Payload))</c> relationship must remain externally reproducible.</para>
/// </summary>
public static class ConfigurationPayloadBuilder
{
    // Pre-encoded, fixed key constants — their declaration order here is irrelevant; the writer
    // emits them in the exact sequence used in each Build* method below, guaranteeing stable order.
    private static readonly JsonEncodedText DisplayNameKey = JsonEncodedText.Encode("display_name");
    private static readonly JsonEncodedText BoundaryKey = JsonEncodedText.Encode("boundary");
    private static readonly JsonEncodedText OwnerOperatorKey = JsonEncodedText.Encode("owner_operator");
    private static readonly JsonEncodedText TitleKey = JsonEncodedText.Encode("title");
    private static readonly JsonEncodedText BodyKey = JsonEncodedText.Encode("body");

    /// <summary>
    /// Builds the subject declaration payload as compact JSON with the FIXED key order
    /// <c>display_name → boundary → owner_operator</c>.
    /// </summary>
    /// <param name="displayName">The human-facing display name. Null is treated as empty.</param>
    /// <param name="boundary">The operational boundary (e.g. jurisdiction). Null is treated as empty.</param>
    /// <param name="ownerOperator">The owning operator. Null is treated as empty.</param>
    /// <returns>
    /// A deterministic, compact JSON object string, e.g.
    /// <c>{"display_name":"Acme Agent","boundary":"CN","owner_operator":"ops"}</c>.
    /// </returns>
    public static string BuildSubjectDeclaration(string? displayName, string? boundary, string? ownerOperator)
    {
        displayName ??= string.Empty;
        boundary ??= string.Empty;
        ownerOperator ??= string.Empty;

        var buffer = new ArrayBufferWriter<byte>();
        // UnsafeRelaxedJsonEscaping keeps non-ASCII (e.g. CJK) literal in the output so the
        // produced payload matches what a user would have typed into the legacy JSON textarea
        // (and the documented example form), while STILL escaping the JSON hazard characters
        // ('"' '\' and control chars such as newlines) — so the result is always valid JSON.
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        writer.WriteStartObject();
        writer.WriteString(DisplayNameKey, displayName);
        writer.WriteString(BoundaryKey, boundary);
        writer.WriteString(OwnerOperatorKey, ownerOperator);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Builds the knowledge content payload as compact JSON with the FIXED key order
    /// <c>title → body</c>.
    /// </summary>
    /// <param name="title">The knowledge title. Null is treated as empty.</param>
    /// <param name="body">The knowledge body text. Null is treated as empty.</param>
    /// <returns>
    /// A deterministic, compact JSON object string, e.g.
    /// <c>{"title":"跨境支付合规","body":"..."}</c>.
    /// </returns>
    public static string BuildKnowledgeContent(string? title, string? body)
    {
        title ??= string.Empty;
        body ??= string.Empty;

        var buffer = new ArrayBufferWriter<byte>();
        // UnsafeRelaxedJsonEscaping keeps non-ASCII (e.g. CJK) literal in the output so the
        // produced payload matches what a user would have typed into the legacy JSON textarea
        // (and the documented example form), while STILL escaping the JSON hazard characters
        // ('"' '\' and control chars such as newlines) — so the result is always valid JSON.
        using var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
        {
            Indented = false,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        writer.WriteStartObject();
        writer.WriteString(TitleKey, title);
        writer.WriteString(BodyKey, body);
        writer.WriteEndObject();
        writer.Flush();
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }
}
