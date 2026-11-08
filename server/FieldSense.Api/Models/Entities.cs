namespace FieldSense.Api.Models;

public enum UserRole
{
    User = 0,
    Admin = 1
}

public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.User;
    public bool CanRelaySms { get; set; }
    public bool ForwardEmailEnabled { get; set; }
    public string? ForwardEmailAddress { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public List<Device> Devices { get; set; } = new();
    public List<SmsMessage> Messages { get; set; } = new();
}

public class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string Name { get; set; } = "Iran phone";
    public string DeviceToken { get; set; } = "";
    public string? PhoneNumber { get; set; }
    public bool IsOnline { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SmsMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public Guid? DeviceId { get; set; }
    public string Direction { get; set; } = "inbound"; // inbound | outbound
    public string PeerNumber { get; set; } = "";
    public string Body { get; set; } = "";
    public string Status { get; set; } = "received"; // received | queued | sent | failed
    public bool EmailForwarded { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class OutboundJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid SmsMessageId { get; set; }
    public string ToNumber { get; set; } = "";
    public string Body { get; set; } = "";
    public string Status { get; set; } = "queued"; // queued | sent | failed
    public string? Error { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAt { get; set; }
}

public class OtpChallenge
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public DateTime ExpiresAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool Consumed { get; set; }
}
