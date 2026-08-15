using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using FullSpectrum.Observer.Host.Web.Services;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

/// <summary>
/// Gate 1 — UI assembly coverage for <see cref="ConfigurationPayloadBuilder.BuildSubjectDeclaration"/>.
///
/// These tests lock the ONLY behavior that the Gate 1 fix introduces on the subject path:
///   - deterministic, compact JSON with a FIXED key order `display_name → boundary → owner_operator`;
///   - standard JSON escaping (quotes / backslash / control chars such as newlines);
///   - byte-identical output across repeated calls (no ambient ordering / whitespace drift);
///   - forward-compatibility with <c>IntakeAdapter</c> (the produced string is deserialized into the
///     <c>JsonElement</c> the Engine receives as the <c>declaration</c>), whose key set MUST stay
///     <c>{display_name, boundary, owner_operator}</c> (Engine v1.5 contract unchanged).
///
/// The subject payload is NOT opaque to the Engine — it becomes the declaration verbatim — so the
/// three-key structure is a hard invariant, not a suggestion.
/// </summary>
public sealed class SubjectDeclarationUiAssemblyTests
{
    [Fact]
    public void BuildSubjectDeclaration_produces_compact_fixed_key_order_json()
    {
        string json = ConfigurationPayloadBuilder.BuildSubjectDeclaration("Acme Agent", "CN", "ops");

        // No indentation / whitespace: this is the exact canonical compact form.
        json.Should().Be("{\"display_name\":\"Acme Agent\",\"boundary\":\"CN\",\"owner_operator\":\"ops\"}");
    }

    [Fact]
    public void BuildSubjectDeclaration_escapes_quotes_backslash_and_newlines()
    {
        // Values that exercise the three classic escaping hazards.
        string displayName = "He said \"hi\"";
        string boundary = "C:\\zone";
        string owner = "line1\nline2";

        string json = ConfigurationPayloadBuilder.BuildSubjectDeclaration(displayName, boundary, owner);

        // The string must be parseable JSON (escape correctness).
        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);

        // And the parsed values must round-trip exactly back to the originals.
        doc.RootElement.GetProperty("display_name").GetString().Should().Be(displayName);
        doc.RootElement.GetProperty("boundary").GetString().Should().Be(boundary);
        doc.RootElement.GetProperty("owner_operator").GetString().Should().Be(owner);
    }

    [Fact]
    public void BuildSubjectDeclaration_is_deterministic_byte_for_byte_across_calls()
    {
        const string dn = "Acme Agent";
        const string b = "CN";
        const string oo = "ops";

        string first = ConfigurationPayloadBuilder.BuildSubjectDeclaration(dn, b, oo);
        string second = ConfigurationPayloadBuilder.BuildSubjectDeclaration(dn, b, oo);
        string third = ConfigurationPayloadBuilder.BuildSubjectDeclaration(dn, b, oo);

        first.Should().Be(second).And.Be(third);

        byte[] bytesA = Encoding.UTF8.GetBytes(first);
        byte[] bytesB = Encoding.UTF8.GetBytes(second);
        byte[] bytesC = Encoding.UTF8.GetBytes(third);
        bytesA.Should().Equal(bytesB);
        bytesA.Should().Equal(bytesC);
    }

    [Fact]
    public void BuildSubjectDeclaration_is_compatible_with_IntakeAdapter_declaration_deserialization()
    {
        // Mirrors IntakeAdapter.cs:32 — the payload is deserialized into a JsonElement that the
        // Engine receives as the `declaration`. The key set MUST be exactly the three expected keys.
        string json = ConfigurationPayloadBuilder.BuildSubjectDeclaration("Acme Agent", "CN", "ops");

        JsonElement element = JsonSerializer.Deserialize<JsonElement>(json);
        element.ValueKind.Should().Be(JsonValueKind.Object);

        HashSet<string> keys = element.EnumerateObject().Select(p => p.Name).ToHashSet();
        keys.Should().BeEquivalentTo(new HashSet<string> { "display_name", "boundary", "owner_operator" });

        // Values are preserved (Engine consumes them).
        element.GetProperty("display_name").GetString().Should().Be("Acme Agent");
        element.GetProperty("boundary").GetString().Should().Be("CN");
        element.GetProperty("owner_operator").GetString().Should().Be("ops");
    }

    [Fact]
    public void BuildSubjectDeclaration_handles_null_inputs_as_empty_strings()
    {
        string json = ConfigurationPayloadBuilder.BuildSubjectDeclaration(null, null, null);

        json.Should().Be("{\"display_name\":\"\",\"boundary\":\"\",\"owner_operator\":\"\"}");

        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("display_name").GetString().Should().Be(string.Empty);
        doc.RootElement.GetProperty("boundary").GetString().Should().Be(string.Empty);
        doc.RootElement.GetProperty("owner_operator").GetString().Should().Be(string.Empty);
    }
}
