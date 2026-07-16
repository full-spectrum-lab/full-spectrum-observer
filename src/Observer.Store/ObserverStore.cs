using System;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using FullSpectrum.Observer.Contracts.Models;
using Microsoft.Data.Sqlite;

namespace FullSpectrum.Observer.Store;

/// <summary>
/// Local SQLite store for the v0.3 Observer Console (10 tables).
/// Enforces the ADR-001 versioning discipline and the red-line invariants:
/// <list type="bullet">
///   <item><description>Active versioned rows are immutable; editing creates a new Draft, then activate().</description></item>
///   <item><description>audit_records is append-only: this class exposes INSERT only for it (no UPDATE/DELETE).</description></item>
///   <item><description>Digests (input/evidence/runtime) are stored verbatim; never fabricated or recomputed.</description></item>
/// </list>
/// </summary>
public sealed class ObserverStore : IAsyncDisposable
{
    private readonly string _dbPath;

    public ObserverStore(string dbPath)
    {
        _dbPath = dbPath ?? throw new ArgumentNullException(nameof(dbPath));
    }

    /// <summary>Applies <c>Init.sql</c> idempotently (CREATE TABLE IF NOT EXISTS), then
    /// backfills the <c>review_status</c> column for databases created before it existed.</summary>
    public async Task EnsureSchemaAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        string sql = LoadInitSql();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
        await EnsureReviewStatusColumnAsync(connection);
    }

    /// <summary>
    /// Idempotently adds <c>review_status</c> to <c>analysis_tasks</c>. SQLite has no
    /// ADD COLUMN IF NOT EXISTS, so we attempt the ALTER and ignore the duplicate-column error.
    /// </summary>
    private static async Task EnsureReviewStatusColumnAsync(SqliteConnection connection)
    {
        const string alter =
            "ALTER TABLE analysis_tasks ADD COLUMN review_status TEXT NOT NULL DEFAULT 'NOT_REQUIRED' " +
            "CHECK (review_status IN ('NOT_REQUIRED','PENDING','REVIEWED'))";
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = alter;
            await command.ExecuteNonQueryAsync();
        }
        catch (Microsoft.Data.Sqlite.SqliteException exception)
            when (exception.SqliteErrorCode == 1 && exception.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase))
        {
            // Column already present on this database; nothing to do.
        }
    }

    private static SqliteConnection Open(string dbPath) =>
        new($"Data Source={dbPath};Pooling=true;");

    private SqliteConnection Open() => Open(_dbPath);

    private static string LoadInitSql()
    {
        var assembly = typeof(ObserverStore).Assembly;
        const string resourceName = "FullSpectrum.Observer.Store.Data.Migrations.Init.sql";
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new StoreException("STORE_MIGRATION_MISSING", $"Embedded resource {resourceName} was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    // ---------------------------------------------------------------------
    // Subjects + subject_versions
    // ---------------------------------------------------------------------

    public async Task InsertSubjectAsync(ObservedSubject subject)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO subjects (local_subject_id, subject_type, mode, concentration_tier, created_at)
            VALUES (@id, @type, @mode, @tier, @created)";
        command.Parameters.AddWithValue("@id", subject.LocalSubjectId);
        command.Parameters.AddWithValue("@type", subject.SubjectType);
        command.Parameters.AddWithValue("@mode", subject.Mode);
        command.Parameters.AddWithValue("@tier", (object?)subject.ConcentrationTier ?? DBNull.Value);
        command.Parameters.AddWithValue("@created", subject.CreatedAt);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<ObservedSubject?> GetSubjectAsync(string localSubjectId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT local_subject_id, subject_type, mode, concentration_tier, created_at FROM subjects WHERE local_subject_id = @id";
        command.Parameters.AddWithValue("@id", localSubjectId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapSubject(reader) : null;
    }

    public async Task<List<ObservedSubject>> GetSubjectsAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT local_subject_id, subject_type, mode, concentration_tier, created_at FROM subjects ORDER BY created_at, local_subject_id";
        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<ObservedSubject>();
        while (await reader.ReadAsync())
        {
            list.Add(MapSubject(reader));
        }
        return list;
    }

    public async Task InsertSubjectVersionAsync(SubjectVersion version)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO subject_versions (version_id, subject_id, status, seq, payload, schema_version, created_at, active_from, retired_at)
            VALUES (@vid, @sid, @status, @seq, @payload, @schema, @created, @active, @retired)";
        BindSubjectVersion(command, version);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<SubjectVersion?> GetSubjectVersionAsync(string versionId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SubjectVersionSelect + " WHERE version_id = @vid";
        command.Parameters.AddWithValue("@vid", versionId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapSubjectVersion(reader) : null;
    }

    public async Task<List<SubjectVersion>> GetSubjectVersionsAsync(string subjectId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SubjectVersionSelect + " WHERE subject_id = @sid ORDER BY seq, created_at";
        command.Parameters.AddWithValue("@sid", subjectId);
        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<SubjectVersion>();
        while (await reader.ReadAsync())
        {
            list.Add(MapSubjectVersion(reader));
        }
        return list;
    }

    public async Task<SubjectVersion?> GetActiveSubjectVersionAsync(string subjectId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = SubjectVersionSelect + " WHERE subject_id = @sid AND status = 'Active'";
        command.Parameters.AddWithValue("@sid", subjectId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapSubjectVersion(reader) : null;
    }

    public async Task<int> GetNextSubjectSeqAsync(string subjectId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(seq), 0) + 1 FROM subject_versions WHERE subject_id = @sid";
        command.Parameters.AddWithValue("@sid", subjectId);
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value);
    }

    /// <summary>
    /// Activates a Draft subject version: retires any current Active version (lifecycle
    /// transition Active-&gt;Retired, which is allowed) and flips the target Draft to Active.
    /// The new immutable Active version is written together with the supplied audit event
    /// inside a single transaction. Re-activating an already-Active version throws.
    /// </summary>
    public async Task ActivateSubjectVersionAsync(string versionId, string activeFromUtc, AuditRecord audit)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            SubjectVersion? target = await GetSubjectVersionAsync(connection, transaction, versionId);
            if (target is null)
            {
                throw new StoreException("STORE_VERSION_MISSING", $"Subject version {versionId} does not exist.");
            }
            if (target.IsActive())
            {
                throw new ImmutableVersionException($"Subject version {versionId} is already Active and cannot be re-activated.");
            }

            await using (var retire = connection.CreateCommand())
            {
                retire.Transaction = transaction;
                retire.CommandText = "UPDATE subject_versions SET status='Retired', retired_at=@t WHERE subject_id=@sid AND status='Active'";
                retire.Parameters.AddWithValue("@t", activeFromUtc);
                retire.Parameters.AddWithValue("@sid", target.SubjectId);
                await retire.ExecuteNonQueryAsync();
            }

            await using (var activate = connection.CreateCommand())
            {
                activate.Transaction = transaction;
                activate.CommandText = "UPDATE subject_versions SET status='Active', active_from=@t WHERE version_id=@vid";
                activate.Parameters.AddWithValue("@t", activeFromUtc);
                activate.Parameters.AddWithValue("@vid", versionId);
                await activate.ExecuteNonQueryAsync();
            }

            await AppendAuditAsync(connection, transaction, audit);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    /// <summary>Retires an Active subject version (lifecycle transition). Only Active rows may be retired.</summary>
    public async Task RetireSubjectVersionAsync(string versionId, string retiredAtUtc, AuditRecord audit)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            SubjectVersion? target = await GetSubjectVersionAsync(connection, transaction, versionId);
            if (target is null)
            {
                throw new StoreException("STORE_VERSION_MISSING", $"Subject version {versionId} does not exist.");
            }
            if (!target.IsActive())
            {
                throw new ImmutableVersionException($"Only an Active subject version can be retired (version {versionId} is {target.Status}).");
            }

            await using var retire = connection.CreateCommand();
            retire.Transaction = transaction;
            retire.CommandText = "UPDATE subject_versions SET status='Retired', retired_at=@t WHERE version_id=@vid";
            retire.Parameters.AddWithValue("@t", retiredAtUtc);
            retire.Parameters.AddWithValue("@vid", versionId);
            await retire.ExecuteNonQueryAsync();

            await AppendAuditAsync(connection, transaction, audit);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    // ---------------------------------------------------------------------
    // Knowledge sources + knowledge_source_versions
    // ---------------------------------------------------------------------

    public async Task InsertKnowledgeSourceAsync(KnowledgeSource source)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO knowledge_sources (source_id, library_id, name, created_at) VALUES (@id, @lib, @name, @created)";
        command.Parameters.AddWithValue("@id", source.SourceId);
        command.Parameters.AddWithValue("@lib", source.LibraryId);
        command.Parameters.AddWithValue("@name", source.Name);
        command.Parameters.AddWithValue("@created", source.CreatedAt);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<KnowledgeSource>> GetKnowledgeSourcesAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_id, library_id, name, created_at FROM knowledge_sources ORDER BY created_at, source_id";
        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<KnowledgeSource>();
        while (await reader.ReadAsync())
        {
            list.Add(new KnowledgeSource
            {
                SourceId = reader.GetString(0),
                LibraryId = reader.GetString(1),
                Name = reader.GetString(2),
                CreatedAt = reader.GetString(3),
            });
        }
        return list;
    }

    public async Task<KnowledgeSource?> GetKnowledgeSourceAsync(string sourceId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT source_id, library_id, name, created_at FROM knowledge_sources WHERE source_id = @id";
        command.Parameters.AddWithValue("@id", sourceId);
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new KnowledgeSource
            {
                SourceId = reader.GetString(0),
                LibraryId = reader.GetString(1),
                Name = reader.GetString(2),
                CreatedAt = reader.GetString(3),
            };
        }
        return null;
    }

    public async Task InsertKnowledgeSourceVersionAsync(KnowledgeSourceVersion version)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO knowledge_source_versions (version_id, source_id, digest, applicability, status, seq, payload, created_at, effective_time)
            VALUES (@vid, @sid, @digest, @applic, @status, @seq, @payload, @created, @eff)";
        BindKnowledgeSourceVersion(command, version);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<KnowledgeSourceVersion>> GetKnowledgeSourceVersionsAsync(string sourceId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = KnowledgeSourceVersionSelect + " WHERE source_id = @sid ORDER BY seq, created_at";
        command.Parameters.AddWithValue("@sid", sourceId);
        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<KnowledgeSourceVersion>();
        while (await reader.ReadAsync())
        {
            list.Add(MapKnowledgeSourceVersion(reader));
        }
        return list;
    }

    public async Task<KnowledgeSourceVersion?> GetKnowledgeSourceVersionAsync(string versionId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = KnowledgeSourceVersionSelect + " WHERE version_id = @vid";
        command.Parameters.AddWithValue("@vid", versionId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapKnowledgeSourceVersion(reader) : null;
    }

    public async Task<KnowledgeSourceVersion?> GetActiveKnowledgeSourceVersionAsync(string sourceId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = KnowledgeSourceVersionSelect + " WHERE source_id = @sid AND status = 'Active'";
        command.Parameters.AddWithValue("@sid", sourceId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapKnowledgeSourceVersion(reader) : null;
    }

    public async Task<int> GetNextKnowledgeSeqAsync(string sourceId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COALESCE(MAX(seq), 0) + 1 FROM knowledge_source_versions WHERE source_id = @sid";
        command.Parameters.AddWithValue("@sid", sourceId);
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value);
    }

    public async Task ActivateKnowledgeSourceVersionAsync(string versionId, string effectiveTimeUtc, AuditRecord audit)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            KnowledgeSourceVersion? target = await GetKnowledgeSourceVersionAsync(connection, transaction, versionId);
            if (target is null)
            {
                throw new StoreException("STORE_VERSION_MISSING", $"Knowledge source version {versionId} does not exist.");
            }
            if (target.IsActive())
            {
                throw new ImmutableVersionException($"Knowledge source version {versionId} is already Active and cannot be re-activated.");
            }

            await using (var retire = connection.CreateCommand())
            {
                retire.Transaction = transaction;
                retire.CommandText = "UPDATE knowledge_source_versions SET status='Retired' WHERE source_id=@sid AND status='Active'";
                retire.Parameters.AddWithValue("@sid", target.SourceId);
                await retire.ExecuteNonQueryAsync();
            }

            await using (var activate = connection.CreateCommand())
            {
                activate.Transaction = transaction;
                activate.CommandText = "UPDATE knowledge_source_versions SET status='Active', effective_time=@t WHERE version_id=@vid";
                activate.Parameters.AddWithValue("@t", effectiveTimeUtc);
                activate.Parameters.AddWithValue("@vid", versionId);
                await activate.ExecuteNonQueryAsync();
            }

            await AppendAuditAsync(connection, transaction, audit);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    // ---------------------------------------------------------------------
    // Analysis tasks + results + conflicts + snapshots + evidence
    // ---------------------------------------------------------------------

    public async Task InsertAnalysisTaskAsync(AnalysisTask task)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO analysis_tasks
                (task_id, subject_version_id, knowledge_version_ids, input_mode, canonical_input, content_digest, transform_trace, retention_mode, status, review_status, created_at)
            VALUES (@id, @svid, @kvs, @mode, @ci, @cd, @tt, @rm, @status, @rstatus, @created)";
        command.Parameters.AddWithValue("@id", task.TaskId);
        command.Parameters.AddWithValue("@svid", task.SubjectVersionId);
        command.Parameters.AddWithValue("@kvs", SerializeArray(task.KnowledgeVersionIds));
        command.Parameters.AddWithValue("@mode", task.InputMode);
        command.Parameters.AddWithValue("@ci", task.CanonicalInput);
        command.Parameters.AddWithValue("@cd", task.ContentDigest);
        command.Parameters.AddWithValue("@tt", (object?)task.TransformTrace ?? DBNull.Value);
        command.Parameters.AddWithValue("@rm", task.RetentionMode);
        command.Parameters.AddWithValue("@status", task.Status);
        command.Parameters.AddWithValue("@rstatus", task.ReviewStatus);
        command.Parameters.AddWithValue("@created", task.CreatedAt);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Advances an analysis task's status. Only the <c>status</c> column is written; the
    /// locked subject/knowledge version bindings are never altered. The transition is validated
    /// by the caller against <see cref="JobLifecycle"/>.</summary>
    public async Task UpdateAnalysisTaskStatusAsync(string taskId, string status)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE analysis_tasks SET status=@status WHERE task_id=@id";
        command.Parameters.AddWithValue("@status", status);
        command.Parameters.AddWithValue("@id", taskId);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Updates the independent review status (CR-OBS-003-JOBSTATUS-001). This never
    /// alters the Engine execution fact recorded by <c>status</c>.</summary>
    public async Task UpdateReviewStatusAsync(string taskId, string reviewStatus)
    {
        if (!JobLifecycle.IsValidReviewStatus(reviewStatus))
        {
            throw new StoreException("STORE_REVIEW_STATUS_INVALID", $"Invalid review_status: {reviewStatus}.");
        }
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE analysis_tasks SET review_status=@rstatus WHERE task_id=@id";
        command.Parameters.AddWithValue("@rstatus", reviewStatus);
        command.Parameters.AddWithValue("@id", taskId);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<AnalysisTask?> GetAnalysisTaskAsync(string taskId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = AnalysisTaskSelect + " WHERE task_id = @id";
        command.Parameters.AddWithValue("@id", taskId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapAnalysisTask(reader) : null;
    }

    public async Task<List<AnalysisTask>> GetAnalysisTasksAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = AnalysisTaskSelect + " ORDER BY created_at DESC, task_id DESC";
        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<AnalysisTask>();
        while (await reader.ReadAsync())
        {
            list.Add(MapAnalysisTask(reader));
        }
        return list;
    }

    /// <summary>Returns every analysis task currently in the given Job status (P0-05).</summary>
    public async Task<List<AnalysisTask>> GetTasksByStatusAsync(string status)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = AnalysisTaskSelect + " WHERE status = @status ORDER BY created_at, task_id";
        command.Parameters.AddWithValue("@status", status);
        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<AnalysisTask>();
        while (await reader.ReadAsync())
        {
            list.Add(MapAnalysisTask(reader));
        }
        return list;
    }

    /// <summary>Returns every task that needs recovery after a Host exit / interruption (P0-05 / P0-B).</summary>
    public async Task<List<AnalysisTask>> GetRecoveryRequiredTasksAsync() =>
        await GetTasksByStatusAsync(AnalysisTaskStatus.RecoveryRequired);

    /// <summary>Loads the runtime snapshot for a task, joining through its result. Null when no
    /// result/snapshot has been persisted yet (Engine had not completed). Used by the recovery
    /// planner to decide whether the Engine must re-run or the task can resume post-Engine.</summary>
    public async Task<RuntimeSnapshot?> GetRuntimeSnapshotByTaskAsync(string taskId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT s.snapshot_id, s.result_id, s.analyzer_version, s.engine_version, s.profile_version,
                   s.schema_version, s.input_digest, s.config_digest, s.runtime_digest
            FROM runtime_snapshots s
            JOIN analysis_results r ON r.result_id = s.result_id
            WHERE r.task_id = @tid";
        command.Parameters.AddWithValue("@tid", taskId);
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new RuntimeSnapshot
            {
                SnapshotId = reader.GetString(0),
                ResultId = reader.GetString(1),
                AnalyzerVersion = reader.GetString(2),
                EngineVersion = reader.GetString(3),
                ProfileVersion = reader.GetString(4),
                SchemaVersion = reader.GetString(5),
                InputDigest = reader.GetString(6),
                ConfigDigest = reader.GetString(7),
                RuntimeDigest = reader.GetString(8),
            };
        }
        return null;
    }

    public async Task InsertAnalysisResultAsync(AnalysisResult result)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO analysis_results (result_id, task_id, conclusion_payload, unknown_state, hard_gate, created_at)
            VALUES (@id, @tid, @cp, @unk, @hg, @created)";
        command.Parameters.AddWithValue("@id", result.ResultId);
        command.Parameters.AddWithValue("@tid", result.TaskId);
        command.Parameters.AddWithValue("@cp", result.ConclusionPayload);
        command.Parameters.AddWithValue("@unk", result.UnknownState);
        command.Parameters.AddWithValue("@hg", result.HardGate ? 1 : 0);
        command.Parameters.AddWithValue("@created", result.CreatedAt);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<AnalysisResult?> GetAnalysisResultByTaskAsync(string taskId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT result_id, task_id, conclusion_payload, unknown_state, hard_gate, created_at FROM analysis_results WHERE task_id = @tid";
        command.Parameters.AddWithValue("@tid", taskId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapAnalysisResult(reader) : null;
    }

    public async Task InsertRuntimeSnapshotAsync(RuntimeSnapshot snapshot)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO runtime_snapshots
                (snapshot_id, result_id, analyzer_version, engine_version, profile_version, schema_version, input_digest, config_digest, runtime_digest)
            VALUES (@id, @rid, @av, @ev, @pv, @sv, @idig, @cdig, @rdig)";
        command.Parameters.AddWithValue("@id", snapshot.SnapshotId);
        command.Parameters.AddWithValue("@rid", snapshot.ResultId);
        command.Parameters.AddWithValue("@av", snapshot.AnalyzerVersion);
        command.Parameters.AddWithValue("@ev", snapshot.EngineVersion);
        command.Parameters.AddWithValue("@pv", snapshot.ProfileVersion);
        command.Parameters.AddWithValue("@sv", snapshot.SchemaVersion);
        command.Parameters.AddWithValue("@idig", snapshot.InputDigest);
        command.Parameters.AddWithValue("@cdig", snapshot.ConfigDigest);
        command.Parameters.AddWithValue("@rdig", snapshot.RuntimeDigest);
        await command.ExecuteNonQueryAsync();
    }

    public async Task InsertEvidenceBundleAsync(EvidenceBundle bundle)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO evidence_bundles (bundle_id, result_id, evidence_digest, references) VALUES (@id, @rid, @ed, @refs)";
        command.Parameters.AddWithValue("@id", bundle.BundleId);
        command.Parameters.AddWithValue("@rid", bundle.ResultId);
        command.Parameters.AddWithValue("@ed", bundle.EvidenceDigest);
        command.Parameters.AddWithValue("@refs", SerializeArray(bundle.References));
        await command.ExecuteNonQueryAsync();
    }

    public async Task InsertConflictObservationsAsync(IEnumerable<ConflictObservation> observations)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        foreach (var observation in observations)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = @"
                INSERT INTO conflict_observations
                    (observation_id, result_id, conflict_type, involved_subjects, severity, human_review_required, reason_code, missing_context, review_flag, review_note)
                VALUES (@id, @rid, @ct, @is, @sev, @hrr, @rc, @mc, @rf, @rn)";
            command.Parameters.AddWithValue("@id", observation.ObservationId);
            command.Parameters.AddWithValue("@rid", observation.ResultId);
            command.Parameters.AddWithValue("@ct", observation.ConflictType);
            command.Parameters.AddWithValue("@is", SerializeArray(observation.InvolvedSubjects));
            command.Parameters.AddWithValue("@sev", observation.Severity);
            command.Parameters.AddWithValue("@hrr", observation.HumanReviewRequired ? 1 : 0);
            command.Parameters.AddWithValue("@rc", (object?)observation.ReasonCode ?? DBNull.Value);
            command.Parameters.AddWithValue("@mc", observation.MissingContext is { } mc ? SerializeArray(mc) : (object)DBNull.Value);
            command.Parameters.AddWithValue("@rf", (object?)observation.ReviewFlag ?? DBNull.Value);
            command.Parameters.AddWithValue("@rn", (object?)observation.ReviewNote ?? DBNull.Value);
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task<EvidenceBundle?> GetEvidenceBundleByResultAsync(string resultId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT bundle_id, result_id, evidence_digest, references FROM evidence_bundles WHERE result_id = @rid";
        command.Parameters.AddWithValue("@rid", resultId);
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new EvidenceBundle
            {
                BundleId = reader.GetString(0),
                ResultId = reader.GetString(1),
                EvidenceDigest = reader.GetString(2),
                References = DeserializeArray(reader.GetString(3)),
            };
        }
        return null;
    }

    public async Task<RuntimeSnapshot?> GetRuntimeSnapshotByResultAsync(string resultId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT snapshot_id, result_id, analyzer_version, engine_version, profile_version, schema_version, input_digest, config_digest, runtime_digest
            FROM runtime_snapshots WHERE result_id = @rid";
        command.Parameters.AddWithValue("@rid", resultId);
        await using var reader = await command.ExecuteReaderAsync();
        if (await reader.ReadAsync())
        {
            return new RuntimeSnapshot
            {
                SnapshotId = reader.GetString(0),
                ResultId = reader.GetString(1),
                AnalyzerVersion = reader.GetString(2),
                EngineVersion = reader.GetString(3),
                ProfileVersion = reader.GetString(4),
                SchemaVersion = reader.GetString(5),
                InputDigest = reader.GetString(6),
                ConfigDigest = reader.GetString(7),
                RuntimeDigest = reader.GetString(8),
            };
        }
        return null;
    }

    public async Task<List<ConflictObservation>> GetConflictObservationsByResultAsync(string resultId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT observation_id, result_id, conflict_type, involved_subjects, severity, human_review_required, reason_code, missing_context, review_flag, review_note
            FROM conflict_observations WHERE result_id = @rid ORDER BY observation_id";
        command.Parameters.AddWithValue("@rid", resultId);
        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<ConflictObservation>();
        while (await reader.ReadAsync())
        {
            list.Add(MapConflictObservation(reader));
        }
        return list;
    }

    // ---------------------------------------------------------------------
    // Audit (append-only: INSERT only; no UPDATE/DELETE is ever issued here)
    // ---------------------------------------------------------------------

    /// <summary>Appends a single audit record (INSERT only). The append-only discipline is
    /// enforced by this method being the ONLY write path for <c>audit_records</c>.</summary>
    public async Task AppendAuditAsync(AuditRecord audit)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await AppendAuditAsync(connection, null, audit);
    }

    private static async Task AppendAuditAsync(SqliteConnection connection, SqliteTransaction? transaction, AuditRecord audit)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = @"
            INSERT INTO audit_records (audit_id, task_id, action, windows_user, machine, session, at, digest, prev_audit_id)
            VALUES (@id, @tid, @action, @user, @machine, @session, @at, @digest, @prev)";
        command.Parameters.AddWithValue("@id", audit.AuditId);
        command.Parameters.AddWithValue("@tid", (object?)audit.TaskId ?? DBNull.Value);
        command.Parameters.AddWithValue("@action", audit.Action);
        command.Parameters.AddWithValue("@user", audit.WindowsUser);
        command.Parameters.AddWithValue("@machine", audit.Machine);
        command.Parameters.AddWithValue("@session", audit.Session);
        command.Parameters.AddWithValue("@at", audit.At);
        command.Parameters.AddWithValue("@digest", audit.Digest);
        command.Parameters.AddWithValue("@prev", (object?)audit.PrevAuditId ?? DBNull.Value);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<AuditRecord>> GetAuditChainAsync(string? taskId = null)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        if (taskId is null)
        {
            command.CommandText = "SELECT audit_id, task_id, action, windows_user, machine, session, at, digest, prev_audit_id FROM audit_records ORDER BY at, audit_id";
        }
        else
        {
            command.CommandText = "SELECT audit_id, task_id, action, windows_user, machine, session, at, digest, prev_audit_id FROM audit_records WHERE task_id = @tid ORDER BY at, audit_id";
            command.Parameters.AddWithValue("@tid", taskId);
        }
        await using var reader = await command.ExecuteReaderAsync();
        var list = new List<AuditRecord>();
        while (await reader.ReadAsync())
        {
            list.Add(MapAudit(reader));
        }
        return list;
    }

    /// <summary>Returns the most recent audit record (by time then id), used to chain new records.</summary>
    public async Task<AuditRecord?> GetLatestAuditAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT audit_id, task_id, action, windows_user, machine, session, at, digest, prev_audit_id FROM audit_records ORDER BY at DESC, audit_id DESC LIMIT 1";
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapAudit(reader) : null;
    }

    /// <summary>Verifies the append-only audit chain continuity (red line #7 / ADR-002).</summary>
    public async Task<AuditChainVerification> VerifyAuditChainAsync()
    {
        List<AuditRecord> records = await GetAuditChainAsync();
        if (records.Count == 0)
        {
            return new AuditChainVerification(true, records.Count, null, "审计链为空（尚未产生审计）");
        }
        if (records[0].PrevAuditId is not null)
        {
            return new AuditChainVerification(false, records.Count, records[0].AuditId, "首条审计记录应无前驱指针");
        }
        for (int i = 1; i < records.Count; i++)
        {
            if (!string.Equals(records[i].PrevAuditId, records[i - 1].AuditId, StringComparison.Ordinal))
            {
                return new AuditChainVerification(false, records.Count, records[i].AuditId, "审计链在记录间断裂（prev_audit_id 不连续）");
            }
        }
        return new AuditChainVerification(true, records.Count, null, "审计链连续完整");
    }

    // ---------------------------------------------------------------------
    // Diagnostics
    // ---------------------------------------------------------------------

    public async Task<StoreDiagnostics> GetDiagnosticsAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        var diagnostics = new StoreDiagnostics();
        diagnostics.SubjectCount = await CountAsync(connection, "SELECT COUNT(*) FROM subjects");
        diagnostics.KnowledgeSourceCount = await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_sources");
        diagnostics.AnalysisTaskCount = await CountAsync(connection, "SELECT COUNT(*) FROM analysis_tasks");
        diagnostics.AuditCount = await CountAsync(connection, "SELECT COUNT(*) FROM audit_records");
        diagnostics.ActiveSubjectVersions = await CountAsync(connection, "SELECT COUNT(*) FROM subject_versions WHERE status='Active'");
        diagnostics.ActiveKnowledgeVersions = await CountAsync(connection, "SELECT COUNT(*) FROM knowledge_source_versions WHERE status='Active'");
        return diagnostics;
    }

    // ---------------------------------------------------------------------
    // Mapping helpers
    // ---------------------------------------------------------------------

    private const string SubjectVersionSelect =
        "SELECT version_id, subject_id, status, seq, payload, schema_version, created_at, active_from, retired_at FROM subject_versions";

    private const string KnowledgeSourceVersionSelect =
        "SELECT version_id, source_id, digest, applicability, status, seq, payload, created_at, effective_time FROM knowledge_source_versions";

    private const string AnalysisTaskSelect =
        "SELECT task_id, subject_version_id, knowledge_version_ids, input_mode, canonical_input, content_digest, transform_trace, retention_mode, status, review_status, created_at FROM analysis_tasks";

    private static ObservedSubject MapSubject(SqliteDataReader reader) => new()
    {
        LocalSubjectId = reader.GetString(0),
        SubjectType = reader.GetString(1),
        Mode = reader.GetString(2),
        ConcentrationTier = reader.IsDBNull(3) ? null : reader.GetString(3),
        CreatedAt = reader.GetString(4),
    };

    private static void BindSubjectVersion(SqliteCommand command, SubjectVersion version)
    {
        command.Parameters.AddWithValue("@vid", version.VersionId);
        command.Parameters.AddWithValue("@sid", version.SubjectId);
        command.Parameters.AddWithValue("@status", version.Status);
        command.Parameters.AddWithValue("@seq", version.Seq);
        command.Parameters.AddWithValue("@payload", version.Payload);
        command.Parameters.AddWithValue("@schema", version.SchemaVersion);
        command.Parameters.AddWithValue("@created", version.CreatedAt);
        command.Parameters.AddWithValue("@active", (object?)version.ActiveFrom ?? DBNull.Value);
        command.Parameters.AddWithValue("@retired", (object?)version.RetiredAt ?? DBNull.Value);
    }

    private static SubjectVersion MapSubjectVersion(SqliteDataReader reader) => new()
    {
        VersionId = reader.GetString(0),
        SubjectId = reader.GetString(1),
        Status = reader.GetString(2),
        Seq = reader.GetInt32(3),
        Payload = reader.GetString(4),
        SchemaVersion = reader.GetString(5),
        CreatedAt = reader.GetString(6),
        ActiveFrom = reader.IsDBNull(7) ? null : reader.GetString(7),
        RetiredAt = reader.IsDBNull(8) ? null : reader.GetString(8),
    };

    private static void BindKnowledgeSourceVersion(SqliteCommand command, KnowledgeSourceVersion version)
    {
        command.Parameters.AddWithValue("@vid", version.VersionId);
        command.Parameters.AddWithValue("@sid", version.SourceId);
        command.Parameters.AddWithValue("@digest", version.Digest);
        command.Parameters.AddWithValue("@applic", version.Applicability);
        command.Parameters.AddWithValue("@status", version.Status);
        command.Parameters.AddWithValue("@seq", version.Seq);
        command.Parameters.AddWithValue("@payload", version.Payload);
        command.Parameters.AddWithValue("@created", version.CreatedAt);
        command.Parameters.AddWithValue("@eff", (object?)version.EffectiveTime ?? DBNull.Value);
    }

    private static KnowledgeSourceVersion MapKnowledgeSourceVersion(SqliteDataReader reader) => new()
    {
        VersionId = reader.GetString(0),
        SourceId = reader.GetString(1),
        Digest = reader.GetString(2),
        Applicability = reader.GetString(3),
        Status = reader.GetString(4),
        Seq = reader.GetInt32(5),
        Payload = reader.GetString(6),
        CreatedAt = reader.GetString(7),
        EffectiveTime = reader.IsDBNull(8) ? null : reader.GetString(8),
    };

    private static AnalysisTask MapAnalysisTask(SqliteDataReader reader) => new()
    {
        TaskId = reader.GetString(0),
        SubjectVersionId = reader.GetString(1),
        KnowledgeVersionIds = DeserializeArray(reader.GetString(2)),
        InputMode = reader.GetString(3),
        CanonicalInput = reader.GetString(4),
        ContentDigest = reader.GetString(5),
        TransformTrace = reader.IsDBNull(6) ? null : reader.GetString(6),
        RetentionMode = reader.GetString(7),
        Status = reader.GetString(8),
        ReviewStatus = reader.IsDBNull(9) ? JobLifecycle.ReviewStatus.NotRequired : reader.GetString(9),
        CreatedAt = reader.GetString(10),
    };

    private static AnalysisResult MapAnalysisResult(SqliteDataReader reader) => new()
    {
        ResultId = reader.GetString(0),
        TaskId = reader.GetString(1),
        ConclusionPayload = reader.GetString(2),
        UnknownState = reader.GetString(3),
        HardGate = reader.GetInt32(4) != 0,
        CreatedAt = reader.GetString(5),
    };

    private static ConflictObservation MapConflictObservation(SqliteDataReader reader) => new()
    {
        ObservationId = reader.GetString(0),
        ResultId = reader.GetString(1),
        ConflictType = reader.GetString(2),
        InvolvedSubjects = DeserializeArray(reader.GetString(3)),
        Severity = reader.GetString(4),
        HumanReviewRequired = reader.GetInt32(5) != 0,
        ReasonCode = reader.IsDBNull(6) ? null : reader.GetString(6),
        MissingContext = reader.IsDBNull(7) ? null : DeserializeArray(reader.GetString(7)),
        ReviewFlag = reader.IsDBNull(8) ? null : reader.GetString(8),
        ReviewNote = reader.IsDBNull(9) ? null : reader.GetString(9),
    };

    private static AuditRecord MapAudit(SqliteDataReader reader) => new()
    {
        AuditId = reader.GetString(0),
        TaskId = reader.IsDBNull(1) ? null : reader.GetString(1),
        Action = reader.GetString(2),
        WindowsUser = reader.GetString(3),
        Machine = reader.GetString(4),
        Session = reader.GetString(5),
        At = reader.GetString(6),
        Digest = reader.GetString(7),
        PrevAuditId = reader.IsDBNull(8) ? null : reader.GetString(8),
    };

    private static async Task<SubjectVersion?> GetSubjectVersionAsync(SqliteConnection connection, SqliteTransaction? transaction, string versionId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = SubjectVersionSelect + " WHERE version_id = @vid";
        command.Parameters.AddWithValue("@vid", versionId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapSubjectVersion(reader) : null;
    }

    private static async Task<KnowledgeSourceVersion?> GetKnowledgeSourceVersionAsync(SqliteConnection connection, SqliteTransaction? transaction, string versionId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = KnowledgeSourceVersionSelect + " WHERE version_id = @vid";
        command.Parameters.AddWithValue("@vid", versionId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapKnowledgeSourceVersion(reader) : null;
    }

    private static async Task<int> CountAsync(SqliteConnection connection, string sql)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        return Convert.ToInt32(value);
    }

    private static string SerializeArray(ImmutableArray<string> values) =>
        JsonSerializer.Serialize(values.ToArray());

    private static ImmutableArray<string> DeserializeArray(string json) =>
        JsonSerializer.Deserialize<string[]>(json)?.ToImmutableArray() ?? ImmutableArray<string>.Empty;

    public async ValueTask DisposeAsync()
    {
        // Connection pooling is handled per-call; nothing persistent to release.
        await Task.CompletedTask;
    }
}

/// <summary>Result of an audit-chain verification.</summary>
/// <param name="IsValid">True when the chain is continuous.</param>
/// <param name="RecordCount">Number of audit records inspected.</param>
/// <param name="BrokenAtAuditId">The record at which the chain broke, if invalid.</param>
/// <param name="Message">Human-readable explanation.</param>
public sealed record AuditChainVerification(bool IsValid, int RecordCount, string? BrokenAtAuditId, string Message);

/// <summary>Store-level counters used by the System Information page.</summary>
public sealed class StoreDiagnostics
{
    public int SubjectCount { get; set; }
    public int KnowledgeSourceCount { get; set; }
    public int AnalysisTaskCount { get; set; }
    public int AuditCount { get; set; }
    public int ActiveSubjectVersions { get; set; }
    public int ActiveKnowledgeVersions { get; set; }
}
