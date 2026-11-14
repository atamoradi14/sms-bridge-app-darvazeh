namespace FieldSense.Api.Models;

/// <summary>
/// One member's VPN access: exactly one 3x-ui client on one configured server.
/// Darvazeh owns the desired state (paid period, quota, suspension); the panel enforces it.
/// </summary>
public class VpnClient
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    /// <summary>Normalised email identity hash, so a deleted and re-created account can't claim a second trial.</summary>
    public string TrialKey { get; set; } = "";
    public bool TrialGranted { get; set; }
    public string ServerCode { get; set; } = "";
    /// <summary>The 3x-ui client email (its identifier, not a real address).</summary>
    public string ClientEmail { get; set; } = "";
    public string ClientUuid { get; set; } = Guid.NewGuid().ToString();
    public string SubId { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? DeletedAt { get; set; }

    public DateTime? PaidUntil { get; set; }
    public DateTime? PeriodStart { get; set; }
    public DateTime? PeriodEnd { get; set; }
    /// <summary>Traffic quota for the current trial or paid period, in bytes. Never 0 while enabled (0 means unlimited in 3x-ui).</summary>
    public long LimitBytes { get; set; }
    /// <summary>Optional per-member monthly quota set by an administrator; null uses the policy default.</summary>
    public long? LimitOverrideBytes { get; set; }

    public DateTime? SuspendedAt { get; set; }
    /// <summary>"admin" or "user".</summary>
    public string? SuspendedBy { get; set; }
    public string? SuspendReason { get; set; }

    public long UsedUp { get; set; }
    public long UsedDown { get; set; }
    public long LifetimeUp { get; set; }
    public long LifetimeDown { get; set; }
    public bool Online { get; set; }
    public int ActiveIps { get; set; }
    public DateTime? LastOnlineAt { get; set; }
    public bool PanelEnabled { get; set; }
    public DateTime? SyncedAt { get; set; }

    /// <summary>Desired state changed and has not yet been written to the panel.</summary>
    public bool NeedsPush { get; set; } = true;
    /// <summary>Panel traffic counters must be zeroed on the next push (new period).</summary>
    public bool ResetPending { get; set; }
    public string? LastError { get; set; }
}

/// <summary>Administrator-editable VPN product rules (single row, Id = 1). Prices live on the hidden "vpn-iran" plan used for checkout.</summary>
public class VpnPolicy
{
    public int Id { get; set; } = 1;
    /// <summary>When false, new members can't request a configuration and checkout is closed. Existing access continues.</summary>
    public bool Enabled { get; set; } = true;
    public long TrialBytes { get; set; } = 10L * 1024 * 1024;
    public int TrialDays { get; set; } = 1;
    public long MonthlyBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public int PeriodDays { get; set; } = 30;
    public int IpLimit { get; set; } = 1;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
