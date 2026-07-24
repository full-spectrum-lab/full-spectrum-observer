using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.Serialization;
using FullSpectrum.Observer.EngineFacade;
using Xunit;

namespace FullSpectrum.Observer.Tests.Integration;

/// <summary>
/// M3-FIX-06 / SD-002 — §六 / §八 dual-path regression proof. The REAL <see cref="EngineFacade"/>
/// builds the <c>EngineFacadeRequest</c> envelope and derives the Worker <c>seed</c> from the content
/// digest via <see cref="EngineSeedContract"/>. Two real-envelope cases are covered:
/// <list type="bullet">
///   <item><description>HIGH_BIT_ONE: prefix "d783d8df" -> 3615742175 (the incident input; previously
///   sign-truncated to the illegal negative -679225121 that NumPy rejected).</description></item>
///   <item><description>HIGH_BIT_ZERO: prefix "7fffffff" -> 2147483647 (max positive int, high bit 0;
///   proves we did not regress the previously-working lower half of UInt32).</description></item>
/// </list>
/// Both drive the REAL EngineFacade with a TEMP fake Python worker that captures the verbatim
/// serialized envelope; the captured <c>seed</c> is asserted. The pinned Engine / worker is NOT
/// touched — a throwaway worker + lock live in a temp dir. No path is hardcoded: the Python
/// interpreter is resolved via <see cref="RuntimeConfigurationResolver.Resolve"/> exactly as in
/// production.
/// </summary>
public sealed class EngineSeedFacadeEnvelopeTests
{
    /// <summary>
    /// Spawns the REAL EngineFacade against a temp fake worker and returns the <c>seed</c> that was
    /// written into the real request envelope, for the given content digest. The only input that
    /// differs between the two regression facts is <paramref name="contentDigest"/>.
    /// </summary>
    private async Task<long> CaptureSeedAsync(string contentDigest)
    {
        // Resolve the Python interpreter the SAME way the production facade does (no hack / override).
        string python = RuntimeConfigurationResolver.Resolve().PythonExecutablePath;
        if (!Path.IsPathFullyQualified(python) || !File.Exists(python))
            throw new FileNotFoundException("Private Python executable is missing or is not an absolute path.", python);

        string repository = RepositoryLayout.FindRoot();
        string root = Path.Combine(Path.GetTempPath(), $"observer-m3fix06-seed-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        string workerPath = Path.Combine(root, "worker.py");
        string capturePath = Path.Combine(root, "envelope.json");
        // The worker embeds this path with forward slashes (C# raw-string interpolation, N=2).
        string captureForward = capturePath.Replace('\\', '/');

        // Fake worker: read ALL stdin (the serialized EngineFacadeRequest envelope), capture it
        // verbatim to disk, then emit a single-line, contract-valid SUCCESS EngineFacadeResponse whose
        // output is the JSON string "ok" and whose output_sha256 is SHA256(utf8(b'"ok"')).
        string workerSource = $$"""
import sys, json, hashlib
data = sys.stdin.buffer.read()
with open(r"{{captureForward}}", "wb") as f:
    f.write(data)
output = "ok"
output_bytes = json.dumps(output).encode("utf-8")
digest = hashlib.sha256(output_bytes).hexdigest()
response = {
    "protocol": "fs-observer-engine-facade/1",
    "request_id": "seed-envelope-test",
    "status": "SUCCESS",
    "engine_version": "v1.5.0",
    "engine_commit": "88493007d4e00344c70a70ed0e5a5d652dec86f5",
    "output_sha256": digest,
    "output": output,
}
sys.stdout.write(json.dumps(response))
""";
        File.WriteAllText(workerPath, workerSource);
        byte[] workerBytes = File.ReadAllBytes(workerPath);

        // Matching worker integrity lock (mirrors Program.cs CreateAdversarialFacade).
        string lockPath = Path.Combine(root, "worker.lock.json");
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new
        {
            protocol = "fs-observer-worker-lock/1",
            engine_version = EngineV15Contract.EngineTag,
            engine_commit = EngineV15Contract.EngineCommit,
            files = new[] { new { path = "worker.py", size_bytes = workerBytes.LongLength, sha256 = Convert.ToHexStringLower(SHA256.HashData(workerBytes)) } },
        }, FoundationJson.CreateOptions()));

        var options = new EngineFacadeOptions
        {
            PythonExecutablePath = python,
            WorkerScriptPath = workerPath,
            EngineRootPath = Path.Combine(repository, "engine", "vendor", "full-spectrum-engine"),
            WorkerLockPath = lockPath,
            SchemaDirectory = RepositoryLayout.SchemaDirectory(repository),
            DefaultTimeout = TimeSpan.FromSeconds(30),
            KillGracePeriod = TimeSpan.FromMilliseconds(250),
            MaximumResponseBytes = 1024 * 1024,
            MaximumStandardErrorBytes = 1024 * 1024,
        };

        var request = new EngineRequest
        {
            EnvelopeVersion = EngineV15Contract.EnvelopeVersion,
            AnalyzerVersion = EngineV15Contract.AnalyzerVersion,
            EngineVersion = EngineV15Contract.EngineTag,
            EngineCommit = EngineV15Contract.EngineCommit,
            ProfileVersion = EngineV15Contract.ProfileVersion,
            SchemaVersion = EngineV15Contract.SchemaVersion,
            SchemaDigest = EngineV15Contract.SchemaDigest,
            CaseId = "CASE005_KNOWLEDGE_CONFLICT",
            Subject = new EngineSubject
            {
                LocalSubjectId = "S-SEED",
                SubjectType = "PERSON",
                Mode = "OBSERVE",
                Declaration = JsonSerializer.SerializeToElement(new { }),
            },
            Knowledge = new List<EngineKnowledge>(),
            Input = new EngineInput
            {
                Mode = "FORM",
                CanonicalInput = JsonSerializer.SerializeToElement(new { user_question = "q", ai_output = "a", context = "c" }),
                // Only this field changes between the two regression facts.
                ContentDigest = contentDigest,
                TransformTrace = JsonSerializer.SerializeToElement(new { }),
            },
            RetentionMode = "SanitizedPersistent",
        };

        string? capturedJson = null;
        try
        {
            EngineResponse response = await new FullSpectrum.Observer.EngineFacade.EngineFacade(options).AnalyzeAsync(request, CancellationToken.None);

            // The facade must report the frozen Engine identity and reach a SUCCESS envelope.
            response.EngineVersion.Should().Be("v1.5.0");

            // The fake worker must have captured the verbatim request envelope.
            File.Exists(capturePath).Should().BeTrue();
            capturedJson = File.ReadAllText(capturePath);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }

        // Return the seed that was written into the REAL envelope.
        long seed;
        using (JsonDocument doc = JsonDocument.Parse(capturedJson!))
        {
            seed = doc.RootElement.GetProperty("seed").GetInt64();
        }
        return seed;
    }

    [Fact]
    public async Task Real_engine_facade_envelope_seed_is_unsigned_uint32_for_high_bit_digest_prefix()
    {
        // HIGH_BIT_ONE: prefix "d783d8df" -> seed 3615742175 (the exact incident input).
        long seed = await CaptureSeedAsync("d783d8df1c15f0ceba4d3b732e0cda91a12edb4b0eea89f309722f8081b21d78");
        seed.Should().Be(3615742175L);            // UInt32 domain value, never sign-truncated
        seed.Should().NotBe(-679225121L);          // the legacy bug's negative value
        EngineSeedContract.IsValid(seed).Should().BeTrue();
    }

    [Fact]
    public async Task Real_engine_facade_envelope_seed_is_unsigned_uint32_for_low_bit_digest_prefix()
    {
        // HIGH_BIT_ZERO: prefix "7fffffff" -> seed 2147483647 (max positive int, high bit 0).
        long seed = await CaptureSeedAsync("7fffffff00000000000000000000000000000000000000000000000000000000");
        seed.Should().Be(2147483647L);
        // High bit must be ZERO (proves we did not regress the previously-working low half of UInt32).
        (seed & 0x80000000L).Should().Be(0L);
        EngineSeedContract.IsValid(seed).Should().BeTrue();
        // And it must equal the contract's own derivation (determinism check).
        EngineSeedContract.FromContentDigest("7fffffff00000000000000000000000000000000000000000000000000000000").Should().Be(seed);
    }
}
