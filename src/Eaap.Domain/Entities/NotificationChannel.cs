namespace Eaap.Domain.Entities;

/// <summary>
/// A delivery target for events (phase 4 section 6). RepositoryId null means it applies to
/// every repository; Triggers lists the event names that fire it.
/// </summary>
public class NotificationChannel
{
    public Guid Id { get; set; }

    /// <summary>Null = global (all repositories).</summary>
    public Guid? RepositoryId { get; set; }
    public Repository? Repository { get; set; }

    public NotificationType Type { get; set; }

    /// <summary>Type-specific config (jsonb): url / webhookUrl / to+smtp profile, and a signing secret.</summary>
    public string ConfigJson { get; set; } = "{}";

    /// <summary>Event names that fire this channel, e.g. ["GateFailed","JobFailed","NewCriticalSecurity"].</summary>
    public List<string> Triggers { get; set; } = [];

    public bool Enabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Record of a delivery attempt outcome, kept for troubleshooting failed sends.</summary>
public class NotificationDeliveryLog
{
    public Guid Id { get; set; }
    public Guid ChannelId { get; set; }
    public NotificationChannel? Channel { get; set; }

    public string Event { get; set; } = string.Empty;
    public Guid? JobId { get; set; }
    public bool Success { get; set; }
    public int Attempts { get; set; }
    public string? Error { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
