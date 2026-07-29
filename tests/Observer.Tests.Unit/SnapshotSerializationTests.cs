using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using FullSpectrum.Observer.Contracts.Models;
using FullSpectrum.Observer.Recovery;
using Xunit;

namespace FullSpectrum.Observer.Tests.Unit;

/// <summary>
/// Serialization / deserialization round-trips for the persisted recovery artifacts
/// (Runtime Snapshot + Analysis Task + Recovery Plan). These types are written to SQLite and
/// also survive process restart, so their JSON shape must round-trip losslessly.
/// </summary>
public sealed class SnapshotSerializationTests
{
    private static readonly JsonSerializerOptions Options = new();

    [Fact]
    public void Runtime_snapshot_round_trips_preserving_all_fields()
    {
        var snapshot = new RuntimeSnapshot
        {
            SnapshotId = "SNP-1",
            ResultId = "RES-1",
            AnalyzerVersion = "1.5.0",
            EngineVersion = "1.5.0",
            ProfileVersion = "1.0.0",
            SchemaVersion = "1.0.0",
            InputDigest = new string('a', 64),
            ConfigDigest = new string('b', 64),
            RuntimeDigest = new string('c', 64),
        };

        string json = JsonSerializer.Serialize(snapshot, Options);
        RuntimeSnapshot? restored = JsonSerializer.Deserialize<RuntimeSnapshot>(json, Options);

        restored.Should().NotBeNull();
        restored!.SnapshotId.Should().Be(snapshot.SnapshotId);
        restored.ResultId.Should().Be(snapshot.ResultId);
        restored.EngineVersion.Should().Be("1.5.0");
        restored.InputDigest.Should().Be(snapshot.InputDigest);
        restored.ConfigDigest.Should().Be(snapshot.ConfigDigest);
        restored.RuntimeDigest.Should().Be(snapshot.RuntimeDigest);
    }

    [Fact]
    public void Analysis_task_round_trips_including_independent_review_status()
    {
        var input = new RawAnalysisInput
        {
            Mode = "FORM",
            CanonicalInput = "{\"x\":1}",
            ContentDigest = new string('d', 64),
            TransformTrace = null,
        };
        AnalysisTask task = AnalysisTask.Create(
            "TASK-1", "SV-1", ImmutableArray.Create("KV-1", "KV-2"), input, "FULL_LOCAL", "2026-07-16T00:00:00Z")
            with { ReviewStatus = JobLifecycle.ReviewStatus.Pending };

        string json = JsonSerializer.Serialize(task, Options);
        AnalysisTask? restored = JsonSerializer.Deserialize<AnalysisTask>(json, Options);

        restored.Should().NotBeNull();
        restored!.TaskId.Should().Be("TASK-1");
        restored.SubjectVersionId.Should().Be("SV-1");
        restored.KnowledgeVersionIds.Should().BeEquivalentTo(ImmutableArray.Create("KV-1", "KV-2"));
        restored.ContentDigest.Should().Be(task.ContentDigest);
        // Independent review_status survives the round-trip and never alters status.
        restored.ReviewStatus.Should().Be(JobLifecycle.ReviewStatus.Pending);
        restored.Status.Should().Be(AnalysisTaskStatus.Draft);
    }

    [Fact]
    public void Recovery_plan_round_trips_preserving_strategy_and_locked_bindings()
    {
        var snapshot = new RuntimeSnapshot
        {
            SnapshotId = "SNP-R",
            ResultId = "RES-R",
            AnalyzerVersion = "1.5.0",
            EngineVersion = "1.5.0",
            ProfileVersion = "1.0.0",
            SchemaVersion = "1.0.0",
            InputDigest = new string('a', 64),
            ConfigDigest = new string('b', 64),
            RuntimeDigest = new string('c', 64),
        };
        var plan = new RecoveryPlan(
            TaskId: "TASK-R",
            Strategy: RecoveryStrategy.ResumePostEngine,
            EngineRerunRequired: false,
            ResumeFromPhase: RecoveryResumePhase.ArtifactCommitted,
            SubjectVersionId: "SV-R",
            KnowledgeVersionIds: ImmutableArray.Create("KV-R"),
            CanonicalInput: "{\"locked\":true}",
            ContentDigest: new string('e', 64),
            Snapshot: snapshot);

        string json = JsonSerializer.Serialize(plan, Options);
        RecoveryPlan? restored = JsonSerializer.Deserialize<RecoveryPlan>(json, Options);

        restored.Should().NotBeNull();
        restored!.TaskId.Should().Be("TASK-R");
        restored.Strategy.Should().Be(RecoveryStrategy.ResumePostEngine);
        restored.EngineRerunRequired.Should().BeFalse();
        restored.ResumeFromPhase.Should().Be(RecoveryResumePhase.ArtifactCommitted);
        restored.SubjectVersionId.Should().Be("SV-R");
        restored.KnowledgeVersionIds.Should().BeEquivalentTo(ImmutableArray.Create("KV-R"));
        restored.CanonicalInput.Should().Be("{\"locked\":true}");
        restored.ContentDigest.Should().Be(new string('e', 64));
        restored.HasSnapshot.Should().BeTrue();
        restored.Snapshot.Should().NotBeNull();
        restored.Snapshot!.EngineVersion.Should().Be("1.5.0");
    }
}
