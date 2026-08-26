using FullSpectrum.Observer.Contracts.Models;
using Microsoft.Data.Sqlite;

namespace FullSpectrum.Observer.Store;

public sealed partial class ObserverStore
{
    public async Task<ScenarioPackBatch> CreateScenarioPackBatchAsync(
        ScenarioPackBatch batch,
        IReadOnlyList<ScenarioPackBatchItem> items)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(items);
        if (items.Count != batch.TotalItems || items.Count == 0 ||
            batch.Status != ScenarioPackBatchStatus.Pending ||
            batch.CompletedItems != 0 || batch.FailedItems != 0 ||
            items.Any(item => !string.Equals(item.BatchId, batch.BatchId, StringComparison.Ordinal)) ||
            items.Any(item => item.Status != ScenarioPackBatchItemStatus.Pending || item.Attempts != 0 ||
                              item.ResultDigest is not null || item.ErrorCode is not null) ||
            items.Select(item => item.ItemId).Distinct(StringComparer.Ordinal).Count() != items.Count ||
            items.Select(item => item.SequenceNo).Distinct().Count() != items.Count)
        {
            throw new StoreException("SCENARIO_PACK_BATCH_INVALID", "Batch items do not match the declared batch identity/count.");
        }

        await using var connection = Open();
        await connection.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        try
        {
            ScenarioPackBatch? existing = await GetBatchByIdempotencyKeyAsync(connection, transaction, batch.IdempotencyKey);
            if (existing is not null)
            {
                bool sameRequest =
                    string.Equals(existing.PackId, batch.PackId, StringComparison.Ordinal) &&
                    string.Equals(existing.PackVersion, batch.PackVersion, StringComparison.Ordinal) &&
                    string.Equals(existing.PackDigest, batch.PackDigest, StringComparison.Ordinal) &&
                    string.Equals(existing.InputDigest, batch.InputDigest, StringComparison.Ordinal) &&
                    string.Equals(existing.AuthorizationRef, batch.AuthorizationRef, StringComparison.Ordinal) &&
                    string.Equals(existing.RedactionReportRef, batch.RedactionReportRef, StringComparison.Ordinal) &&
                    string.Equals(existing.DeletionPlanRef, batch.DeletionPlanRef, StringComparison.Ordinal) &&
                    existing.TotalItems == batch.TotalItems;
                if (!sameRequest)
                {
                    throw new StoreException("SCENARIO_PACK_BATCH_IDEMPOTENCY_CONFLICT", "The idempotency key is already bound to a different batch request.");
                }
                await transaction.CommitAsync();
                return existing;
            }

            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO scenario_pack_batches
                      (batch_id, idempotency_key, pack_id, pack_version, pack_digest, input_digest,
                       authorization_ref, redaction_report_ref, deletion_plan_ref, status,
                       total_items, completed_items, failed_items, created_at_utc, updated_at_utc)
                    VALUES
                      (@batch, @key, @pack, @version, @digest, @input,
                       @authorization, @redaction, @deletion, @status,
                       @total, @completed, @failed, @created, @updated)";
                BindBatch(command, batch);
                await command.ExecuteNonQueryAsync();
            }

            foreach (ScenarioPackBatchItem item in items.OrderBy(item => item.SequenceNo))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO scenario_pack_batch_items
                      (batch_id, item_id, sequence_no, input_json, input_digest, status, attempts, result_digest, error_code)
                    VALUES (@batch, @item, @sequence, @input, @digest, @status, @attempts, @result, @error)";
                BindBatchItem(command, item);
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
            return batch;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<ScenarioPackBatch?> GetScenarioPackBatchAsync(string batchId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        return await GetBatchAsync(connection, null, batchId);
    }

    public async Task<List<ScenarioPackBatchItem>> GetScenarioPackBatchItemsAsync(string batchId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = BatchItemSelect + " WHERE batch_id=@batch ORDER BY sequence_no";
        command.Parameters.AddWithValue("@batch", batchId);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<ScenarioPackBatchItem>();
        while (await reader.ReadAsync()) result.Add(MapBatchItem(reader));
        return result;
    }

    public async Task<ScenarioPackBatchItem?> ClaimNextScenarioPackBatchItemAsync(string batchId, string updatedAtUtc)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        try
        {
            ScenarioPackBatchItem? item;
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = BatchItemSelect + " WHERE batch_id=@batch AND status='PENDING' ORDER BY sequence_no LIMIT 1";
                select.Parameters.AddWithValue("@batch", batchId);
                await using var reader = await select.ExecuteReaderAsync();
                item = await reader.ReadAsync() ? MapBatchItem(reader) : null;
            }
            if (item is null)
            {
                await transaction.CommitAsync();
                return null;
            }
            await using (var update = connection.CreateCommand())
            {
                update.Transaction = transaction;
                update.CommandText = @"
                    UPDATE scenario_pack_batch_items
                    SET status='PROCESSING', attempts=attempts+1
                    WHERE batch_id=@batch AND item_id=@item AND status='PENDING';";
                update.Parameters.AddWithValue("@batch", batchId);
                update.Parameters.AddWithValue("@item", item.ItemId);
                if (await update.ExecuteNonQueryAsync() != 1)
                    throw new StoreException("SCENARIO_PACK_BATCH_STATE_CONFLICT", "Only a PENDING item may be claimed.");
            }
            await using (var batchUpdate = connection.CreateCommand())
            {
                batchUpdate.Transaction = transaction;
                batchUpdate.CommandText = @"
                    UPDATE scenario_pack_batches
                    SET status='PROCESSING', updated_at_utc=@updated
                    WHERE batch_id=@batch AND status IN ('PENDING','INTERRUPTED','PROCESSING');";
                batchUpdate.Parameters.AddWithValue("@batch", batchId);
                batchUpdate.Parameters.AddWithValue("@updated", updatedAtUtc);
                if (await batchUpdate.ExecuteNonQueryAsync() != 1)
                    throw new StoreException("SCENARIO_PACK_BATCH_STATE_CONFLICT", "The batch is unavailable for item claim.");
            }
            await transaction.CommitAsync();
            return item with { Status = ScenarioPackBatchItemStatus.Processing, Attempts = item.Attempts + 1 };
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public Task CompleteScenarioPackBatchItemAsync(string batchId, string itemId, string resultDigest, string updatedAtUtc) =>
        FinishScenarioPackBatchItemAsync(batchId, itemId, ScenarioPackBatchItemStatus.Completed, resultDigest, null, updatedAtUtc);

    public Task FailScenarioPackBatchItemAsync(string batchId, string itemId, string errorCode, string updatedAtUtc) =>
        FinishScenarioPackBatchItemAsync(batchId, itemId, ScenarioPackBatchItemStatus.Failed, null, errorCode, updatedAtUtc);

    public async Task<int> RecoverInterruptedScenarioPackBatchAsync(string batchId, string updatedAtUtc)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        try
        {
            int recovered;
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
                    UPDATE scenario_pack_batch_items SET status='PENDING'
                    WHERE batch_id=@batch AND status='PROCESSING'";
                command.Parameters.AddWithValue("@batch", batchId);
                recovered = await command.ExecuteNonQueryAsync();
            }
            if (recovered > 0)
            {
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = "UPDATE scenario_pack_batches SET status='INTERRUPTED', updated_at_utc=@updated WHERE batch_id=@batch";
                update.Parameters.AddWithValue("@batch", batchId);
                update.Parameters.AddWithValue("@updated", updatedAtUtc);
                await update.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
            return recovered;
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task InsertScenarioPackReviewAsync(ScenarioPackReviewRecord review)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO scenario_pack_review_records
              (review_id, task_id, reviewer_ref, disposition, evidence_refs_json, note,
               duration_milliseconds, started_at_utc, completed_at_utc, record_digest)
            VALUES (@review, @task, @reviewer, @disposition, @evidence, @note,
                    @duration, @started, @completed, @digest)";
        command.Parameters.AddWithValue("@review", review.ReviewId);
        command.Parameters.AddWithValue("@task", review.TaskId);
        command.Parameters.AddWithValue("@reviewer", review.ReviewerRef);
        command.Parameters.AddWithValue("@disposition", review.Disposition);
        command.Parameters.AddWithValue("@evidence", review.EvidenceRefsJson);
        command.Parameters.AddWithValue("@note", review.Note);
        command.Parameters.AddWithValue("@duration", review.DurationMilliseconds);
        command.Parameters.AddWithValue("@started", review.StartedAtUtc);
        command.Parameters.AddWithValue("@completed", review.CompletedAtUtc);
        command.Parameters.AddWithValue("@digest", review.RecordDigest);
        await command.ExecuteNonQueryAsync();
    }

    public async Task<List<ScenarioPackReviewRecord>> GetScenarioPackReviewsAsync(string taskId)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT review_id, task_id, reviewer_ref, disposition, evidence_refs_json, note,
                   duration_milliseconds, started_at_utc, completed_at_utc, record_digest
            FROM scenario_pack_review_records WHERE task_id=@task ORDER BY completed_at_utc, review_id";
        command.Parameters.AddWithValue("@task", taskId);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<ScenarioPackReviewRecord>();
        while (await reader.ReadAsync())
        {
            result.Add(new ScenarioPackReviewRecord
            {
                ReviewId = reader.GetString(0), TaskId = reader.GetString(1), ReviewerRef = reader.GetString(2),
                Disposition = reader.GetString(3), EvidenceRefsJson = reader.GetString(4), Note = reader.GetString(5),
                DurationMilliseconds = reader.GetInt64(6), StartedAtUtc = reader.GetString(7),
                CompletedAtUtc = reader.GetString(8), RecordDigest = reader.GetString(9),
            });
        }
        return result;
    }

    public async Task InsertScenarioPackMetricAsync(ScenarioPackMetricLedgerEntry entry)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO scenario_pack_metric_ledger
              (entry_id, pack_id, pack_version, pack_digest, sample_boundary_id, metric_id,
               numerator, denominator, unit, source_digest, calculated_at_utc)
            VALUES (@entry, @pack, @version, @digest, @boundary, @metric,
                    @numerator, @denominator, @unit, @source, @calculated)";
        command.Parameters.AddWithValue("@entry", entry.EntryId);
        command.Parameters.AddWithValue("@pack", entry.PackId);
        command.Parameters.AddWithValue("@version", entry.PackVersion);
        command.Parameters.AddWithValue("@digest", entry.PackDigest);
        command.Parameters.AddWithValue("@boundary", entry.SampleBoundaryId);
        command.Parameters.AddWithValue("@metric", entry.MetricId);
        command.Parameters.AddWithValue("@numerator", entry.Numerator);
        command.Parameters.AddWithValue("@denominator", entry.Denominator);
        command.Parameters.AddWithValue("@unit", entry.Unit);
        command.Parameters.AddWithValue("@source", entry.SourceDigest);
        command.Parameters.AddWithValue("@calculated", entry.CalculatedAtUtc);
        await command.ExecuteNonQueryAsync();
    }

    public async Task InsertScenarioPackExportAsync(ScenarioPackExportRecord export)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            INSERT INTO scenario_pack_export_records
              (export_id, task_id, export_json, export_digest, sensitive_scan_status, created_at_utc)
            VALUES (@export, @task, @json, @digest, @scan, @created)";
        command.Parameters.AddWithValue("@export", export.ExportId);
        command.Parameters.AddWithValue("@task", export.TaskId);
        command.Parameters.AddWithValue("@json", export.ExportJson);
        command.Parameters.AddWithValue("@digest", export.ExportDigest);
        command.Parameters.AddWithValue("@scan", export.SensitiveScanStatus);
        command.Parameters.AddWithValue("@created", export.CreatedAtUtc);
        await command.ExecuteNonQueryAsync();
    }

    public async Task AppendScenarioPackAuditEventAsync(ScenarioPackAuditEvent auditEvent, AuditRecord audit)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        ArgumentNullException.ThrowIfNull(audit);
        if (!string.Equals(auditEvent.AuditId, audit.AuditId, StringComparison.Ordinal) ||
            !string.Equals(auditEvent.TaskId, audit.TaskId, StringComparison.Ordinal) ||
            !string.Equals(auditEvent.OccurredAtUtc, audit.At, StringComparison.Ordinal) ||
            !string.Equals(audit.Action, "V04_" + auditEvent.EventType, StringComparison.Ordinal))
        {
            throw new StoreException("SCENARIO_PACK_AUDIT_MISMATCH", "Structured event and global audit record do not describe the same event.");
        }

        await using var connection = Open();
        await connection.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        try
        {
            string? expectedPrevious;
            string? previousAt;
            await using (var latest = connection.CreateCommand())
            {
                latest.Transaction = transaction;
                latest.CommandText = "SELECT audit_id, at FROM audit_records ORDER BY at DESC, audit_id DESC LIMIT 1";
                await using var reader = await latest.ExecuteReaderAsync();
                if (await reader.ReadAsync())
                {
                    expectedPrevious = reader.GetString(0);
                    previousAt = reader.GetString(1);
                }
                else
                {
                    expectedPrevious = null;
                    previousAt = null;
                }
            }
            if (!string.Equals(expectedPrevious, audit.PrevAuditId, StringComparison.Ordinal))
            {
                throw new StoreException("SCENARIO_PACK_AUDIT_CHAIN_CONFLICT", "Audit event does not extend the current global audit head.");
            }
            if (previousAt is not null &&
                DateTimeOffset.Parse(audit.At, System.Globalization.CultureInfo.InvariantCulture) <=
                DateTimeOffset.Parse(previousAt, System.Globalization.CultureInfo.InvariantCulture))
            {
                throw new StoreException("SCENARIO_PACK_AUDIT_TIME_CONFLICT", "Audit event time must advance beyond the current global audit head.");
            }
            await AppendAuditAsync(connection, transaction, audit);
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
                    INSERT INTO scenario_pack_audit_events
                      (event_id, audit_id, event_type, entity_ref, entity_digest, task_id, occurred_at_utc)
                    VALUES (@event, @audit, @type, @entity, @digest, @task, @occurred)";
                command.Parameters.AddWithValue("@event", auditEvent.EventId);
                command.Parameters.AddWithValue("@audit", auditEvent.AuditId);
                command.Parameters.AddWithValue("@type", auditEvent.EventType);
                command.Parameters.AddWithValue("@entity", auditEvent.EntityRef);
                command.Parameters.AddWithValue("@digest", auditEvent.EntityDigest);
                command.Parameters.AddWithValue("@task", (object?)auditEvent.TaskId ?? DBNull.Value);
                command.Parameters.AddWithValue("@occurred", auditEvent.OccurredAtUtc);
                await command.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    public async Task<List<ScenarioPackAuditEvent>> GetScenarioPackAuditEventsAsync()
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = @"
            SELECT event_id, audit_id, event_type, entity_ref, entity_digest, task_id, occurred_at_utc
            FROM scenario_pack_audit_events ORDER BY occurred_at_utc, event_id";
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<ScenarioPackAuditEvent>();
        while (await reader.ReadAsync())
        {
            result.Add(new ScenarioPackAuditEvent
            {
                EventId = reader.GetString(0), AuditId = reader.GetString(1), EventType = reader.GetString(2),
                EntityRef = reader.GetString(3), EntityDigest = reader.GetString(4),
                TaskId = reader.IsDBNull(5) ? null : reader.GetString(5), OccurredAtUtc = reader.GetString(6),
            });
        }
        return result;
    }

    private async Task FinishScenarioPackBatchItemAsync(
        string batchId, string itemId, string status, string? resultDigest, string? errorCode, string updatedAtUtc)
    {
        await using var connection = Open();
        await connection.OpenAsync();
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync();
        try
        {
            await using (var command = connection.CreateCommand())
            {
                command.Transaction = transaction;
                command.CommandText = @"
                    UPDATE scenario_pack_batch_items
                    SET status=@status, result_digest=@result, error_code=@error
                    WHERE batch_id=@batch AND item_id=@item AND status='PROCESSING'";
                command.Parameters.AddWithValue("@status", status);
                command.Parameters.AddWithValue("@result", (object?)resultDigest ?? DBNull.Value);
                command.Parameters.AddWithValue("@error", (object?)errorCode ?? DBNull.Value);
                command.Parameters.AddWithValue("@batch", batchId);
                command.Parameters.AddWithValue("@item", itemId);
                if (await command.ExecuteNonQueryAsync() != 1)
                    throw new StoreException("SCENARIO_PACK_BATCH_STATE_CONFLICT", "Only a PROCESSING item may reach a terminal state.");
            }
            await using (var counters = connection.CreateCommand())
            {
                counters.Transaction = transaction;
                counters.CommandText = @"
                    UPDATE scenario_pack_batches
                    SET completed_items=(SELECT count(*) FROM scenario_pack_batch_items WHERE batch_id=@batch AND status='COMPLETED'),
                        failed_items=(SELECT count(*) FROM scenario_pack_batch_items WHERE batch_id=@batch AND status='FAILED'),
                        status=CASE WHEN (SELECT count(*) FROM scenario_pack_batch_items WHERE batch_id=@batch AND status IN ('COMPLETED','FAILED'))=total_items THEN 'COMPLETED' ELSE 'PROCESSING' END,
                        updated_at_utc=@updated
                    WHERE batch_id=@batch";
                counters.Parameters.AddWithValue("@batch", batchId);
                counters.Parameters.AddWithValue("@updated", updatedAtUtc);
                await counters.ExecuteNonQueryAsync();
            }
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task<ScenarioPackBatch?> GetBatchByIdempotencyKeyAsync(
        SqliteConnection connection, SqliteTransaction transaction, string key)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BatchSelect + " WHERE idempotency_key=@key";
        command.Parameters.AddWithValue("@key", key);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapBatch(reader) : null;
    }

    private static async Task<ScenarioPackBatch?> GetBatchAsync(
        SqliteConnection connection, SqliteTransaction? transaction, string batchId)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = BatchSelect + " WHERE batch_id=@batch";
        command.Parameters.AddWithValue("@batch", batchId);
        await using var reader = await command.ExecuteReaderAsync();
        return await reader.ReadAsync() ? MapBatch(reader) : null;
    }

    private static void BindBatch(SqliteCommand command, ScenarioPackBatch batch)
    {
        command.Parameters.AddWithValue("@batch", batch.BatchId);
        command.Parameters.AddWithValue("@key", batch.IdempotencyKey);
        command.Parameters.AddWithValue("@pack", batch.PackId);
        command.Parameters.AddWithValue("@version", batch.PackVersion);
        command.Parameters.AddWithValue("@digest", batch.PackDigest);
        command.Parameters.AddWithValue("@input", batch.InputDigest);
        command.Parameters.AddWithValue("@authorization", batch.AuthorizationRef);
        command.Parameters.AddWithValue("@redaction", batch.RedactionReportRef);
        command.Parameters.AddWithValue("@deletion", batch.DeletionPlanRef);
        command.Parameters.AddWithValue("@status", batch.Status);
        command.Parameters.AddWithValue("@total", batch.TotalItems);
        command.Parameters.AddWithValue("@completed", batch.CompletedItems);
        command.Parameters.AddWithValue("@failed", batch.FailedItems);
        command.Parameters.AddWithValue("@created", batch.CreatedAtUtc);
        command.Parameters.AddWithValue("@updated", batch.UpdatedAtUtc);
    }

    private static void BindBatchItem(SqliteCommand command, ScenarioPackBatchItem item)
    {
        command.Parameters.AddWithValue("@batch", item.BatchId);
        command.Parameters.AddWithValue("@item", item.ItemId);
        command.Parameters.AddWithValue("@sequence", item.SequenceNo);
        command.Parameters.AddWithValue("@input", item.InputJson);
        command.Parameters.AddWithValue("@digest", item.InputDigest);
        command.Parameters.AddWithValue("@status", item.Status);
        command.Parameters.AddWithValue("@attempts", item.Attempts);
        command.Parameters.AddWithValue("@result", (object?)item.ResultDigest ?? DBNull.Value);
        command.Parameters.AddWithValue("@error", (object?)item.ErrorCode ?? DBNull.Value);
    }

    private static ScenarioPackBatch MapBatch(SqliteDataReader reader) => new()
    {
        BatchId = reader.GetString(0), IdempotencyKey = reader.GetString(1), PackId = reader.GetString(2),
        PackVersion = reader.GetString(3), PackDigest = reader.GetString(4), InputDigest = reader.GetString(5),
        AuthorizationRef = reader.GetString(6), RedactionReportRef = reader.GetString(7), DeletionPlanRef = reader.GetString(8),
        Status = reader.GetString(9), TotalItems = reader.GetInt32(10), CompletedItems = reader.GetInt32(11),
        FailedItems = reader.GetInt32(12), CreatedAtUtc = reader.GetString(13), UpdatedAtUtc = reader.GetString(14),
    };

    private static ScenarioPackBatchItem MapBatchItem(SqliteDataReader reader) => new()
    {
        BatchId = reader.GetString(0), ItemId = reader.GetString(1), SequenceNo = reader.GetInt32(2),
        InputJson = reader.GetString(3), InputDigest = reader.GetString(4), Status = reader.GetString(5),
        Attempts = reader.GetInt32(6), ResultDigest = reader.IsDBNull(7) ? null : reader.GetString(7),
        ErrorCode = reader.IsDBNull(8) ? null : reader.GetString(8),
    };

    private const string BatchSelect = @"
        SELECT batch_id, idempotency_key, pack_id, pack_version, pack_digest, input_digest,
               authorization_ref, redaction_report_ref, deletion_plan_ref, status,
               total_items, completed_items, failed_items, created_at_utc, updated_at_utc
        FROM scenario_pack_batches";

    private const string BatchItemSelect = @"
        SELECT batch_id, item_id, sequence_no, input_json, input_digest, status, attempts, result_digest, error_code
        FROM scenario_pack_batch_items";
}
