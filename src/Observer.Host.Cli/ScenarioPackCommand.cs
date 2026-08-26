using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FullSpectrum.Observer.Contracts;
using FullSpectrum.Observer.Contracts.Canonicalization;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Contracts.Serialization;
using FullSpectrum.Observer.EngineFacade;
using FullSpectrum.Observer.Execution.ScenarioPacks;
using FullSpectrum.Observer.Store;

namespace FullSpectrum.Observer.Host.Cli;

/// <summary>
/// Minimal local v0.4 entry point. It runs either a signed Golden Case or one explicitly authorized,
/// structured external sample through the real Engine v1.5 path and persists the candidate-only
/// evidence chain. It intentionally has no remote listener, identity/RBAC layer, production action,
/// natural-language inference claim, or target-user acceptance claim.
/// </summary>
public static class ScenarioPackCommand
{
    private static readonly JsonSerializerOptions IndentedJsonOptions = CreateIndentedOptions();
    public static async Task<int> RunAsync(
        CliOptions options,
        string dataDirectory,
        CancellationToken cancellationToken)
    {
        string? requestedCase = options.Get("--case");
        string? requestedInput = options.Get("--input");
        if (requestedCase is not null && requestedInput is not null)
            throw new ArgumentException("Specify --case or --input, not both.");
        bool externalInput = requestedInput is not null;
        string? inputRoot = externalInput
            ? Path.GetFullPath(options.Require("--input-root"))
            : null;
        string packPath = externalInput
            ? ResolveContainedDirectory(inputRoot!, options.Require("--pack"))
            : Path.GetFullPath(options.Require("--pack"));
        string authorizationRef = options.Require("--authorization-ref");
        string redactionRef = options.Require("--redaction-ref");
        string deletionRef = options.Require("--deletion-ref");
        RuntimeConfigurationResolver.RuntimeConfiguration config = RuntimeConfigurationResolver.Resolve();
        string schemaPath = Path.Combine(config.PackageRoot, "schemas", "scenario-pack", "v1", "scenario-pack-manifest.schema.json");
        string trustRootPath = externalInput
            ? ResolveContainedFile(inputRoot!, options.Require("--trust-store"))
            : Path.Combine(config.PackageRoot, "config", "scenario-pack-trust-roots.json");
        string trustStoreDigest = ComputeFileDigest(trustRootPath);
        if (externalInput)
        {
            RequireDigestReference(authorizationRef, "--authorization-ref");
            RequireDigestReference(redactionRef, "--redaction-ref");
            RequireDigestReference(deletionRef, "--deletion-ref");
            string declaredTrustStoreDigest = options.Require("--trust-store-sha256").ToLowerInvariant();
            if (!string.Equals(declaredTrustStoreDigest, trustStoreDigest, StringComparison.Ordinal))
                throw new InvalidDataException("SCENARIO_PACK_TRUST_STORE_DIGEST_MISMATCH: external trust store digest mismatch.");
        }
        var loader = new ScenarioPackLoader(schemaPath, trustRootPath, "v0.4.0-beta", "v1.5.0");
        LoadedScenarioPack pack = loader.Load(packPath);
        if (externalInput &&
            (!pack.TrustPurpose.StartsWith("OWNER_LOCAL_PILOT:", StringComparison.Ordinal) ||
             !pack.Identity.PackId.StartsWith("observer.case.knowledge-conflict", StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "SCENARIO_PACK_EXTERNAL_TRUST_PURPOSE_INVALID: external knowledge input requires an OWNER_LOCAL_PILOT trust root.");
        }

        byte[] inputBytes;
        string caseId;
        if (externalInput)
        {
            string inputPath = ResolveContainedFile(inputRoot!, requestedInput!);
            inputBytes = File.ReadAllBytes(inputPath);
            if (inputBytes.Length is 0 or > 1_048_576)
                throw new InvalidDataException("SCENARIO_PACK_STRUCTURED_INPUT_INVALID: external input size is invalid.");
            using JsonDocument inputDocument = JsonDocument.Parse(inputBytes);
            if (!inputDocument.RootElement.TryGetProperty("sample_id", out JsonElement sampleIdElement) ||
                sampleIdElement.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("SCENARIO_PACK_STRUCTURED_INPUT_INVALID: sample_id is required.");
            caseId = sampleIdElement.GetString() ?? string.Empty;
        }
        else
        {
            ScenarioPackGoldenCase golden = pack.Manifest.GoldenCases.SingleOrDefault(item =>
                requestedCase is null || string.Equals(item.CaseId, requestedCase, StringComparison.Ordinal))
                ?? throw new ArgumentException("The requested --case is not declared by the Scenario Pack.");
            caseId = golden.CaseId;
            inputBytes = File.ReadAllBytes(ResolveContained(pack.RootDirectory, golden.InputRef));
        }

        string now = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
        string inputIdentity = externalInput
            ? FsObsCanonicalizer.Sha256Hex(FsObsCanonicalizer.Canonicalize(inputBytes))
            : caseId;
        string caseToken = Convert.ToHexStringLower(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{pack.Identity.PackId}|{pack.Identity.Version}|{pack.Identity.Digest}|{inputIdentity}")))[..16];
        string batchId = options.Get("--batch-id") ?? $"BATCH-V04-{caseToken}";
        string idempotencyKey = options.Get("--idempotency-key") ?? batchId;
        string itemId = $"ITEM-V04-{caseToken}";
        string taskId = $"TASK-V04-{caseToken}";
        string resultId = $"RESULT-{caseToken}";
        ScenarioPackEngineRequest request = new ScenarioPackEngineRequestFactory().BuildRequest(
            pack, itemId, inputBytes, externalInput ? "SCENARIO_PACK_AUTHORIZED_EXTERNAL" : "SCENARIO_PACK_GOLDEN");
        StructuredKnowledgeConflictResult? structured = externalInput
            ? StructuredKnowledgeConflictAdapter.Analyze(
                inputBytes, resultId, now, pack.SignatureKeyId, trustStoreDigest)
            : null;

        SqliteRuntimeBootstrap.Initialize();
        string dbPath = Path.Combine(Path.GetFullPath(dataDirectory), "observer_console.db");
        await using var store = new ObserverStore(dbPath);
        await store.EnsureSchemaAsync();
        await store.InstallScenarioPackAsync(new ScenarioPackRegistration
        {
            PackId = pack.Identity.PackId,
            Version = pack.Identity.Version,
            Digest = pack.Identity.Digest,
            ManifestJson = File.ReadAllText(Path.Combine(pack.RootDirectory, "scenario-pack.manifest.json"), Encoding.UTF8),
            InstalledAtUtc = now,
        });
        await AppendAuditAsync(store, ScenarioPackAuditEventType.PackInstalled,
            pack.Identity.PackId + "@" + pack.Identity.Version, pack.Identity.Digest, null);

        string inputJson = Encoding.UTF8.GetString(inputBytes);
        ScenarioPackBatchDraft draft = ScenarioPackBatchFactory.Create(
            pack,
            batchId,
            idempotencyKey,
            [(itemId, inputJson)],
            authorizationRef,
            redactionRef,
            deletionRef,
            now);
        ScenarioPackBatch batch = await store.CreateScenarioPackBatchAsync(draft.Batch, draft.Items);
        await AppendAuditAsync(store, ScenarioPackAuditEventType.BatchImported,
            batch.BatchId, batch.InputDigest, null);
        ScenarioPackBatchItem? claimed = await store.ClaimNextScenarioPackBatchItemAsync(batch.BatchId, now);
        if (claimed is null)
        {
            Write(new
            {
                status = batch.Status,
                idempotent = true,
                batch_id = batch.BatchId,
                pack_id = pack.Identity.PackId,
                pack_version = pack.Identity.Version,
                pack_digest = pack.Identity.Digest,
                input_mode = externalInput ? "AUTHORIZED_EXTERNAL_STRUCTURED" : "SIGNED_GOLDEN",
            }, options.Has("--json"));
            return 0;
        }

        await SeedTaskAsync(store, request, taskId, now);
        await store.BindScenarioPackToTaskAsync(new ScenarioPackTaskBinding
        {
            TaskId = taskId,
            PackId = pack.Identity.PackId,
            PackVersion = pack.Identity.Version,
            PackDigest = pack.Identity.Digest,
            ProfileRefsJson = ScenarioPackEngineRequestFactory.CreateProfileBindingJson(request),
            BoundAtUtc = now,
        });
        await AppendAuditAsync(store, ScenarioPackAuditEventType.TaskBound,
            taskId, request.InputDigest, taskId);

        try
        {
            EngineFacadeOptions engineOptions = new()
            {
                PythonExecutablePath = config.PythonExecutablePath,
                WorkerScriptPath = config.WorkerScriptPath,
                EngineRootPath = config.EngineRootPath,
                WorkerLockPath = config.WorkerLockPath,
                SchemaDirectory = config.SchemaDirectory,
                DefaultTimeout = TimeSpan.FromSeconds(options.GetInt("--timeout", 60)),
            };
            IEngineFacade engine = new FullSpectrum.Observer.EngineFacade.EngineFacade(engineOptions);
            EngineResponse response = await engine.AnalyzeAsync(request.Request, cancellationToken);
            ScenarioPackExecutionProjection projection = ScenarioPackEngineResultProjector.Project(
                taskId,
                resultId,
                request,
                response,
                DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
            await store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.PrecheckPassed);
            await store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.EngineCompleted);
            await store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.OutputValidated);
            await store.InsertAnalysisResultAsync(projection.Result);
            await store.InsertRuntimeSnapshotAsync(projection.Snapshot);
            if (projection.Evidence is not null)
                await store.InsertEvidenceBundleAsync(projection.Evidence);
            await store.InsertConflictObservationsAsync(projection.Observations);
            if (structured is not null)
                await store.InsertScenarioPackCandidateObservationsAsync(structured.Candidates);
            await store.UpdateAnalysisTaskStatusAsync(taskId, AnalysisTaskStatus.Completed);
            string executionDigest = ComputeExecutionDigest(projection.Snapshot.RuntimeDigest, structured?.Candidates ?? []);
            await store.CompleteScenarioPackBatchItemAsync(batch.BatchId, claimed.ItemId, executionDigest, now);
            await AppendAuditAsync(store, ScenarioPackAuditEventType.ResultPersisted,
                projection.Result.ResultId, projection.Snapshot.RuntimeDigest, taskId);
            if (structured is not null)
            {
                foreach (ScenarioPackCandidateObservation candidate in structured.Candidates)
                    await AppendAuditAsync(store, ScenarioPackAuditEventType.ResultPersisted,
                        candidate.CandidateId, candidate.CandidateDigest, taskId);
            }
            Write(new
            {
                status = "COMPLETED",
                candidate_only = true,
                human_review_required = true,
                authorized_action = false,
                batch_id = batch.BatchId,
                task_id = taskId,
                result_id = projection.Result.ResultId,
                pack_id = pack.Identity.PackId,
                pack_version = pack.Identity.Version,
                pack_digest = pack.Identity.Digest,
                input_mode = externalInput ? "AUTHORIZED_EXTERNAL_STRUCTURED" : "SIGNED_GOLDEN",
                signature_key_id = pack.SignatureKeyId,
                trust_purpose = pack.TrustPurpose,
                trust_store_digest = trustStoreDigest,
                unknown_state = projection.Result.UnknownState,
                runtime_digest = projection.Snapshot.RuntimeDigest,
                execution_digest = executionDigest,
                structured_adapter = structured is null ? null : StructuredKnowledgeConflictAdapter.AdapterVersion,
                candidate_count = structured?.Candidates.Count ?? 0,
                candidate_ids = structured?.Candidates.Select(candidate => candidate.CandidateId).ToArray() ?? [],
                candidate_reason_codes = structured?.Candidates.Select(candidate => candidate.ReasonCode).Distinct(StringComparer.Ordinal).ToArray() ?? [],
                minority_evidence_survived = structured?.MinorityEvidenceSurvived ?? false,
                unknown_context = structured?.UnknownContext ?? [],
            }, options.Has("--json"));
            return 0;
        }
        catch (Exception exception)
        {
            string errorCode = exception is InvalidDataException dataException &&
                                dataException.Message.StartsWith("SCENARIO_PACK_", StringComparison.Ordinal)
                ? dataException.Message.Split(':', 2)[0]
                : "SCENARIO_PACK_RUN_FAILED";
            await store.FailScenarioPackBatchItemAsync(batch.BatchId, claimed.ItemId, errorCode, now);
            Write(new
            {
                status = "FAILED",
                candidate_only = true,
                batch_id = batch.BatchId,
                task_id = taskId,
                error_code = errorCode,
            }, options.Has("--json"));
            return 70;
        }
    }

    private static async Task SeedTaskAsync(
        ObserverStore store,
        ScenarioPackEngineRequest request,
        string taskId,
        string now)
    {
        string subjectId = $"SUBJ-{taskId[9..]}";
        string subjectVersionId = $"SUBV-{taskId[9..]}";
        await store.InsertSubjectAsync(new ObservedSubject
        {
            LocalSubjectId = subjectId,
            SubjectType = request.Request.Subject.SubjectType,
            Mode = "V04_LOCAL_RUNNER",
            CreatedAt = now,
        });
        await store.InsertSubjectVersionAsync(new SubjectVersion
        {
            VersionId = subjectVersionId,
            SubjectId = subjectId,
            Status = "Active",
            Seq = 1,
            Payload = $"{{\"subject_type\":\"{request.Request.Subject.SubjectType}\",\"mode\":\"V04_LOCAL_RUNNER\"}}",
            SchemaVersion = request.Request.SchemaVersion,
            CreatedAt = now,
            ActiveFrom = now,
        });
        RawAnalysisInput input = new()
        {
            Mode = request.Request.Input.Mode,
            CanonicalInput = request.Request.Input.CanonicalInput.GetRawText(),
            ContentDigest = request.InputDigest,
            TransformTrace = request.Request.Input.TransformTrace.GetRawText(),
        };
        await store.InsertAnalysisTaskAsync(AnalysisTask.Create(
            taskId,
            subjectVersionId,
            [],
            input,
            RetentionMode.SanitizedPersistent.ToWire(),
            now));
    }

    private static string ResolveContained(string root, string reference)
    {
        string candidate = Path.GetFullPath(Path.Combine(root, reference.Replace('/', Path.DirectorySeparatorChar)));
        string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !File.Exists(candidate))
            throw new FileNotFoundException($"Scenario Pack input is unavailable: {reference}");
        return candidate;
    }

    private static string ResolveContainedFile(string root, string value) =>
        ResolveContainedExternal(root, value, requireDirectory: false);

    private static string ResolveContainedDirectory(string root, string value) =>
        ResolveContainedExternal(root, value, requireDirectory: true);

    private static string ResolveContainedExternal(string root, string value, bool requireDirectory)
    {
        string absoluteRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        string candidate = Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(absoluteRoot, value));
        string prefix = absoluteRoot + Path.DirectorySeparatorChar;
        if (!string.Equals(candidate, absoluteRoot, StringComparison.OrdinalIgnoreCase) &&
            !candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("SCENARIO_PACK_EXTERNAL_PATH_UNSAFE: external pilot path escapes input-root.");
        bool exists = requireDirectory ? Directory.Exists(candidate) : File.Exists(candidate);
        if (!exists || (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("SCENARIO_PACK_EXTERNAL_PATH_UNSAFE: external pilot path is missing or unsafe.");
        return candidate;
    }

    private static string ComputeFileDigest(string path) =>
        Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    private static void RequireDigestReference(string value, string option)
    {
        const string prefix = "sha256:";
        if (!value.StartsWith(prefix, StringComparison.Ordinal) || value.Length != prefix.Length + 64 ||
            value[prefix.Length..].Any(character => !Uri.IsHexDigit(character)) ||
            !string.Equals(value, value.ToLowerInvariant(), StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"SCENARIO_PACK_GOVERNANCE_REFERENCE_INVALID: {option} must be a lowercase sha256:<digest> reference.");
        }
    }

    private static string ComputeExecutionDigest(
        string runtimeDigest,
        IReadOnlyList<ScenarioPackCandidateObservation> candidates)
    {
        byte[] material = FsObsCanonicalizer.Canonicalize(JsonSerializer.SerializeToElement(new
        {
            runtime_digest = runtimeDigest,
            candidate_digests = candidates.Select(candidate => candidate.CandidateDigest)
                .Order(StringComparer.Ordinal)
                .ToArray(),
        }));
        return FsObsCanonicalizer.Sha256Hex(material);
    }

    private static async Task AppendAuditAsync(
        ObserverStore store,
        string eventType,
        string entityRef,
        string entityDigest,
        string? taskId)
    {
        AuditRecord? previous = await store.GetLatestAuditAsync();
        DateTimeOffset at = DateTimeOffset.UtcNow;
        if (previous is not null &&
            DateTimeOffset.TryParse(previous.At, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTimeOffset previousAt) &&
            at <= previousAt)
        {
            at = previousAt.AddTicks(1);
        }
        string atUtc = at.ToString("O", CultureInfo.InvariantCulture);
        ScenarioPackAuditAppend append = ScenarioPackAuditFactory.Create(
            "EVT-" + Guid.NewGuid().ToString("N"),
            "AUD-" + Guid.NewGuid().ToString("N"),
            eventType,
            entityRef,
            entityDigest,
            taskId,
            atUtc,
            Environment.UserName,
            Environment.MachineName,
            "V04-RUNNER",
            previous?.AuditId);
        await store.AppendScenarioPackAuditEventAsync(append.Event, append.Audit);
    }

    private static void Write<T>(T value, bool json)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, json ? FoundationJson.CreateOptions() : IndentedJsonOptions));
    }

    private static JsonSerializerOptions CreateIndentedOptions()
    {
        JsonSerializerOptions options = FoundationJson.CreateOptions();
        options.WriteIndented = true;
        return options;
    }
}
