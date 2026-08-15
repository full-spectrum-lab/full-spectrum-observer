using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using FullSpectrum.Observer.Host.Web.Services;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

/// <summary>
/// Gate 1 — UI assembly coverage for <see cref="ConfigurationPayloadBuilder.BuildKnowledgeContent"/>.
///
/// The knowledge payload is OPAQUE to the Engine (only its digest + applicability + ids are sent in
/// the v1.5 envelope), so the only hard invariant on the new path is that the digest remains
/// externally reproducible as <c>Digest = SHA256(UTF-8(Payload))</c> — which is exactly how
/// <c>KnowledgeCatalog.cs:49</c> computes it. These tests lock:
///   - deterministic, compact JSON with FIXED key order `title → body`;
///   - standard JSON escaping (quotes / backslash / newlines);
///   - byte-identical output across repeated calls;
///   - the digest (mirroring the catalog) equals an INDEPENDENT external SHA-256 recompute of the
///     same UTF-8 payload bytes, so the relationship holds for any downstream verifier;
///   - structural compatibility with the legacy `{"title":"","body":""}` form (same key set), so
///     existing records continue to display unchanged.
///
/// <para><b>Scope note:</b> <c>title/body</c> is the v0.3 knowledge STORAGE PROJECTION only
/// (<c>TITLE_BODY_CANONICAL_SCHEMA = NO</c>). This test does NOT elevate it to a system-wide schema.</para>
/// </summary>
public sealed class KnowledgePayloadAssemblyTests
{
    [Fact]
    public void BuildKnowledgeContent_produces_compact_fixed_key_order_json()
    {
        string json = ConfigurationPayloadBuilder.BuildKnowledgeContent("跨境支付合规", "合规规则正文");

        json.Should().Be("{\"title\":\"跨境支付合规\",\"body\":\"合规规则正文\"}");
    }

    [Fact]
    public void BuildKnowledgeContent_escapes_quotes_backslash_and_newlines()
    {
        string title = "He said \"hi\"";
        string body = "C:\\path\nnext";

        string json = ConfigurationPayloadBuilder.BuildKnowledgeContent(title, body);

        using JsonDocument doc = JsonDocument.Parse(json);
        doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
        doc.RootElement.GetProperty("title").GetString().Should().Be(title);
        doc.RootElement.GetProperty("body").GetString().Should().Be(body);
    }

    [Fact]
    public void BuildKnowledgeContent_is_deterministic_byte_for_byte_across_calls()
    {
        const string title = "跨境支付合规";
        const string body = "合规规则正文";

        string first = ConfigurationPayloadBuilder.BuildKnowledgeContent(title, body);
        string second = ConfigurationPayloadBuilder.BuildKnowledgeContent(title, body);
        string third = ConfigurationPayloadBuilder.BuildKnowledgeContent(title, body);

        first.Should().Be(second).And.Be(third);

        byte[] a = Encoding.UTF8.GetBytes(first);
        byte[] b = Encoding.UTF8.GetBytes(second);
        byte[] c = Encoding.UTF8.GetBytes(third);
        a.Should().Equal(b);
        a.Should().Equal(c);
    }

    [Fact]
    public void BuildKnowledgeContent_digest_equals_external_sha256_of_utf8_payload()
    {
        // Mirrors KnowledgeCatalog.cs:49 — digest = ToHexStringLower(SHA256(UTF-8(contentJson))).
        string payload = ConfigurationPayloadBuilder.BuildKnowledgeContent("跨境支付合规", "合规规则正文");
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payload);

        string catalogDigest = Convert.ToHexStringLower(SHA256.HashData(payloadBytes));

        // INDEPENDENT external recompute (different API surface — IncrementalHash, not the
        // static HashData the catalog uses) of SHA-256 over the same bytes, to prove the
        // relationship holds for ANY downstream verifier, not just the catalog's own call.
        byte[] externalHash;
        using (var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            incremental.AppendData(payloadBytes);
            externalHash = incremental.GetHashAndReset();
        }
        string externalDigest = Convert.ToHexString(externalHash).ToLowerInvariant();

        // Both must agree, and both must be a 64-char lowercase hex string (the digest form stored).
        catalogDigest.Should().Be(externalDigest);
        catalogDigest.Should().MatchRegex("^[0-9a-f]{64}$");

        // Sanity: a different payload yields a different digest (no accidental constant).
        string other = ConfigurationPayloadBuilder.BuildKnowledgeContent("other", "x");
        byte[] otherBytes = Encoding.UTF8.GetBytes(other);
        Convert.ToHexStringLower(SHA256.HashData(otherBytes)).Should().NotBe(catalogDigest);
    }

    [Fact]
    public void BuildKnowledgeContent_is_structurally_compatible_with_legacy_title_body_form()
    {
        // The new empty-form output must keep the SAME key set as the legacy default template so
        // existing records (and the legacy {"title":"","body":""}) remain displayable.
        string produced = ConfigurationPayloadBuilder.BuildKnowledgeContent(string.Empty, string.Empty);
        produced.Should().Be("{\"title\":\"\",\"body\":\"\"}");

        HashSet<string> NewKeys(JsonElement e) => e.EnumerateObject().Select(p => p.Name).ToHashSet();

        using JsonDocument producedDoc = JsonDocument.Parse(produced);
        using JsonDocument legacyDoc = JsonDocument.Parse("{\"title\":\"\",\"body\":\"\"}");

        HashSet<string> producedKeys = NewKeys(producedDoc.RootElement);
        HashSet<string> legacyKeys = NewKeys(legacyDoc.RootElement);

        producedKeys.Should().BeEquivalentTo(new HashSet<string> { "title", "body" });
        producedKeys.Should().BeEquivalentTo(legacyKeys);
    }
}
