using System.Security.Cryptography;
using System.Text;
using FullSpectrum.Observer.Contracts.Models;

namespace FullSpectrum.Observer.Host.Web.Services;

/// <summary>
/// Audit context for a single Observer session. The Windows user / machine / session fields are
/// audit context ONLY and are never used as a login identity (red line #1). Digest chaining is
/// computed from the prior audit id, action, timestamp, task, and actor — never random.
/// </summary>
public sealed class AuditContext
{
    public string WindowsUser { get; }

    public string Machine { get; }

    public string Session { get; }

    public AuditContext()
    {
        WindowsUser = Environment.UserName;
        Machine = Environment.MachineName;
        Session = Guid.NewGuid().ToString("D");
    }

    /// <summary>Computes the chained event digest (sha256 of the canonical audit line).</summary>
    public string ComputeDigest(string? prevAuditId, string action, string at, string? taskId)
    {
        string canonical = $"{prevAuditId ?? string.Empty}|{action}|{at}|{taskId ?? string.Empty}|{WindowsUser}|{Machine}|{Session}";
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    /// <summary>Builds an append-only audit record chained to <paramref name="prevAuditId"/>.</summary>
    public AuditRecord Build(string auditId, string? taskId, string action, string atUtc, string digest, string? prevAuditId) =>
        AuditRecord.Append(auditId, taskId, action, WindowsUser, Machine, Session, atUtc, digest, prevAuditId);
}
