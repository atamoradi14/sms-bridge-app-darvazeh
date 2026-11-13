namespace FieldSense.Api.Models;

public enum UserRole
{
    User = 0,
    Admin = 1
}

public class AppUser
{
    public string? Username { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? GoogleSubject { get; set; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string? PasswordHash { get; set; }
    public int FailedPasswordAttempts { get; set; }
    public DateTime? PasswordLockedUntil { get; set; }
    public int AuthVersion { get; set; }
    public DateTime? EmailVerifiedAt { get; set; }
    public DateTime? TrialStartedAt { get; set; }
    public DateTime? SuspendedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public UserRole Role { get; set; } = UserRole.User;
    public bool CanRelaySms { get; set; }
    public bool? SendOverride { get; set; }
    public bool? ReceiveOverride { get; set; }
    public bool ForwardEmailEnabled { get; set; }
    public string? ForwardEmailAddress { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastLoginAt { get; set; }
    public List<Device> Devices { get; set; } = new();
    public List<SmsMessage> Messages { get; set; } = new();
}

public class Device
{
    public DateTime? AdminPausedAt { get; set; }
    public int? BatteryLevel { get; set; }
    public bool? IsCharging { get; set; }
    public string? NetworkType { get; set; }
    public bool? NetworkValidated { get; set; }
    public bool? SmsPermissionsGranted { get; set; }
    public bool? BatteryOptimizationExempt { get; set; }
    public DateTime? HealthReportedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public string Name { get; set; } = "Iran phone";
    /// <summary>Stable phone app install id so re-login reuses the same device row.</summary>
    public string? InstallationId { get; set; }
    public string DeviceToken { get; set; } = "";
    public string? PhoneNumber { get; set; }
    public string? LastIpAddress { get; set; }
    public string? Platform { get; set; }
    public string? DeviceModel { get; set; }
    public string? AppVersion { get; set; }
    public bool IsOnline { get; set; }
    public DateTime? LastSeenAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class SmsMessage
{
    public DateTime? DeletedAt { get; set; }
    public int? BatteryLevel { get; set; }
    public bool? IsCharging { get; set; }
    public string? NetworkType { get; set; }
    public string EmailForwardStatus { get; set; } = "not_requested";
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public Guid? DeviceId { get; set; }
    public string Direction { get; set; } = "inbound"; // inbound | outbound
    public string PeerNumber { get; set; } = "";
    public string Body { get; set; } = "";
    public string Status { get; set; } = "received"; // received | queued | sent | failed
    public string? ClientMessageId { get; set; }
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
    public int Attempts { get; set; }
}

public class SecuritySettings {
    public int Id { get; set; } = 1;
    public bool AdminRequireOtp { get; set; }
}

public class MembershipPlan
{
    public int EmailForwardLimit { get; set; } = 0;
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public int DurationDays { get; set; } = 3;
    public int MessageLimit { get; set; } = 20;
    public bool SendEnabled { get; set; } = true;
    public bool ReceiveEnabled { get; set; } = true;
    public bool IsActive { get; set; } = true;
    public long? PriceUsdCents { get; set; }
    public long? PriceToman { get; set; }
    public bool IsPurchasable { get; set; }
}

public class Subscription
{
    public int EmailForwardLimit { get; set; } = 0;
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser User { get; set; } = null!;
    public Guid PlanId { get; set; }
    public MembershipPlan Plan { get; set; } = null!;
    public DateTime StartsAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddDays(3);
    public int MessageLimit { get; set; } = 20;
    public int UsedMessages { get; set; }
    public int UsedEmailForwards { get; set; }
    public bool SendEnabled { get; set; } = true;
    public bool ReceiveEnabled { get; set; } = true;
    public bool IsCurrent { get; set; } = true;
    public Guid? PaymentOrderId { get; set; }
}

public class PaymentOrder
{
    public int EmailForwardLimit { get; set; } = 0;
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid PlanId { get; set; }
    public string PlanName { get; set; } = "";
    public int DurationDays { get; set; }
    public int MessageLimit { get; set; }
    public bool SendEnabled { get; set; }
    public bool ReceiveEnabled { get; set; }
    public string Provider { get; set; } = "stripe";
    public string Currency { get; set; } = "USD";
    public long AmountMinor { get; set; }
    public long RefundedMinor { get; set; }
    public string RequestKey { get; set; } = "";
    public string Status { get; set; } = "created";
    public string? ProviderSessionId { get; set; }
    public string? ProviderPaymentId { get; set; }
    public string? CheckoutUrl { get; set; }
    /// <summary>Ezpay orders: the wallet the link pays into and the broker's link id (uniqueId), kept for status checks.</summary>
    public string? ReceivingPhone { get; set; }
    public string? ProviderUniqueId { get; set; }
    public string ReturnOrigin { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ExpiresAt { get; set; }
    public DateTime? PaidAt { get; set; }
    public DateTime? FulfilledAt { get; set; }
}

public class PaymentEventReceipt
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class UsageEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid SubscriptionId { get; set; }
    public Guid MessageId { get; set; }
    public string Direction { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class AccountActivity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid? ActorId { get; set; }
    public string Action { get; set; } = "";
    public string Detail { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

public class DeviceLinkRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Email { get; set; } = "";
    public string SecretHash { get; set; } = "";
    public string ComparisonCode { get; set; } = "";
    public string InstallationId { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string? AppVersion { get; set; }
    public string SourceIp { get; set; } = "";
    public string Status { get; set; } = "pending";
    public Guid? DeviceId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddMinutes(10);
}
public class DeviceAlert
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public string Kind { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}

public class EmailDispatch
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Kind { get; set; } = "otp";
    public Guid UserId { get; set; }
    public Guid? MessageId { get; set; }
    public Guid? SubscriptionId { get; set; }
    public Guid? ChallengeId { get; set; }
    public string ProtectedPayload { get; set; } = "";
    public string Status { get; set; } = "queued";
    public string? ErrorCode { get; set; }
    public int Attempts { get; set; }
    public bool ProviderStarted { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime NextAttemptAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; } = DateTime.UtcNow.AddHours(1);
    public DateTime? AcceptedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class GoogleLoginFlow {
    public Guid Id {get;set;}=Guid.NewGuid();
    public string StateHash {get;set;}="";
    public string BrowserSecretHash {get;set;}="";
    public string Nonce {get;set;}="";
    public string PkceVerifier {get;set;}="";
    public bool Consumed {get;set;}
    public DateTime ExpiresAt {get;set;}=DateTime.UtcNow.AddMinutes(10);
    public Guid? UserId {get;set;}
    public int AuthVersion {get;set;}
    public string? ResultCodeHash {get;set;}
    public DateTime? ResultExpiresAt {get;set;}
}


/// <summary>
/// Darvazeh's default receiving wallet (single row, Id = 1), connected by phone number on the admin wallet page, like Ravand's
/// superadmin wallet: Ezpay consent code, or phone-ownership proof when Ezpay already has broker access. Toman payments go here.
/// </summary>
public class PaymentWallet
{
    public int Id { get; set; } = 1;
    public string? PhoneNumber { get; set; }
    public DateTime? ConnectedAt { get; set; }
    public Guid? ConnectedByUserId { get; set; }
    public bool MerchantEnabled { get; set; }
    public string? PendingPhoneNumber { get; set; }
    public string? RequestUid { get; set; }
    public Guid? ChallengeId { get; set; }
    public Guid? ChallengeUserId { get; set; }
    public DateTime? ChallengeExpiresAt { get; set; }
    public DateTime? LastOtpAt { get; set; }
    public int FailedAttempts { get; set; }
}

/// <summary>A wallet phone whose ownership an admin proved (Ezpay consent or SMS code), so it can be selected again without a code.</summary>
public class WalletOwner
{
    public string PhoneNumber { get; set; } = "";
    public Guid OwnerUserId { get; set; }
    public DateTime VerifiedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One-time SMS code proving the admin holds a wallet phone (sent through the admin's linked Darvazeh phone).</summary>
public class WalletPhoneProof
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public string PhoneNumber { get; set; } = "";
    public string CodeHash { get; set; } = "";
    public int Attempts { get; set; }
    public bool Consumed { get; set; }
    public string? RequestedFromIp { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime ExpiresAt { get; set; }
}
