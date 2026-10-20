using System;

namespace LocalFileAudit.Core.Models;

public sealed class ChatMessageRecord
{
    public long Id { get; set; }
    public Guid ScanRunId { get; set; }
    public string Role { get; set; } = "user"; // "user", "assistant", or "system"
    public string Content { get; set; } = string.Empty;
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string? ContextCandidateIds { get; set; }

    public string Sender
    {
        get => Role;
        set => Role = value;
    }

    public string Message
    {
        get => Content;
        set => Content = value;
    }

    public DateTime CreatedAtUtc
    {
        get => TimestampUtc;
        set => TimestampUtc = value;
    }
}
