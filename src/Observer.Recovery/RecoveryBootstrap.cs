using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using FullSpectrum.Observer.Application;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Store;

namespace FullSpectrum.Observer.Recovery;

/// <summary>
/// Host-exit / interruption recovery bootstrap (P0-B rule 2). When the Launcher terminates the
/// Host, no background task can keep computing. Every in-flight progress task is driven to
/// <c>RECOVERY_REQUIRED</c> and an audit event is chained, so the next start can rebuild attempts
/// from the stored runtime snapshot. This is an EXTERNAL forced transition and intentionally
/// bypasses <see cref="JobLifecycle"/> (a Host exit is an out-of-band event, not a normal edge).
/// </summary>
public static class RecoveryBootstrap
{
    public const string RecoveryAction = "HOST_EXIT_RECOVERY";

    /// <summary>
    /// Marks all in-flight tasks <c>RECOVERY_REQUIRED</c> and returns the number marked.
    /// Terminal / already-recovered tasks are left untouched.
    /// </summary>
    public static async Task<int> MarkInFlightTasksForRecoveryAsync(
        ObserverStore store, IClock clock, IIdGenerator ids, string session)
    {
        int marked = 0;
        AuditRecord? prev = await store.GetLatestAuditAsync();

        foreach (string state in InFlightStates())
        {
            foreach (AnalysisTask task in await store.GetTasksByStatusAsync(state))
            {
                if (task.Status == AnalysisTaskStatus.RecoveryRequired)
                {
                    continue;
                }

                await store.UpdateAnalysisTaskStatusAsync(task.TaskId, AnalysisTaskStatus.RecoveryRequired);

                string at = clock.UtcNow.ToString("O");
                string digest = ComputeDigest(RecoveryAction, task.TaskId, at, prev?.AuditId);
                AuditRecord record = AuditRecord.Append(
                    auditId: ids.NewId().ToString("D"),
                    taskId: task.TaskId,
                    action: RecoveryAction,
                    windowsUser: Environment.UserName,
                    machine: Environment.MachineName,
                    session: session,
                    atUtc: at,
                    digest: digest,
                    previousAuditId: prev?.AuditId);
                await store.AppendAuditAsync(record);
                prev = record;
                marked++;
            }
        }

        return marked;
    }

    private static IEnumerable<string> InFlightStates()
    {
        yield return AnalysisTaskStatus.Draft;
        yield return AnalysisTaskStatus.Running;
        yield return AnalysisTaskStatus.PrecheckPassed;
        yield return AnalysisTaskStatus.SnapshotCommitted;
        yield return AnalysisTaskStatus.EngineCompleted;
        yield return AnalysisTaskStatus.OutputValidated;
        yield return AnalysisTaskStatus.ArtifactCommitted;
        yield return AnalysisTaskStatus.ObservationCommitted;
        yield return AnalysisTaskStatus.AuditCommitted;
    }

    private static string ComputeDigest(string action, string taskId, string at, string? prevAuditId)
    {
        string material = $"{action}|{taskId}|{at}|{prevAuditId ?? string.Empty}";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
