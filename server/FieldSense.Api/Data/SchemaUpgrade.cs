using FieldSense.Api.Models;
using FieldSense.Api.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace FieldSense.Api.Data;

public static class SchemaUpgrade
{
    public static async Task Apply(AppDbContext db, string adminEmail)
    {
        await db.Database.EnsureCreatedAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        // Inspect existing columns explicitly: SQL errors must never be swallowed.
        async Task<bool> AddColumn(string table, string name, string declaration)
        {
            var connection = db.Database.GetDbConnection();
            using var cmd = connection.CreateCommand();
            cmd.Transaction = transaction.GetDbTransaction();
            cmd.CommandText = $"PRAGMA table_info([{table}])";
            await using var reader = await cmd.ExecuteReaderAsync();
            var found = false;
            while (await reader.ReadAsync()) if (reader.GetString(1) == name) found = true;
            await reader.DisposeAsync();
            if (!found) { cmd.CommandText = $"ALTER TABLE [{table}] ADD COLUMN [{name}] {declaration}"; await cmd.ExecuteNonQueryAsync(); }
            return !found;
        }
        await AddColumn("Users", "GoogleSubject", "TEXT NULL");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_GoogleSubject ON Users(GoogleSubject);
            CREATE TABLE IF NOT EXISTS GoogleLoginFlows (Id TEXT NOT NULL PRIMARY KEY, StateHash TEXT NOT NULL,
              BrowserSecretHash TEXT NOT NULL, Nonce TEXT NOT NULL, PkceVerifier TEXT NOT NULL, Consumed INTEGER NOT NULL,
              ExpiresAt TEXT NOT NULL, UserId TEXT NULL, AuthVersion INTEGER NOT NULL, ResultCodeHash TEXT NULL, ResultExpiresAt TEXT NULL);
            """);
        await AddColumn("Users", "Username", "TEXT NULL");
        await AddColumn("Users", "PasswordHash", "TEXT NULL");
        await AddColumn("Users", "FailedPasswordAttempts", "INTEGER NOT NULL DEFAULT 0");
        await AddColumn("Users", "PasswordLockedUntil", "TEXT NULL");
        await AddColumn("Users", "AuthVersion", "INTEGER NOT NULL DEFAULT 0");
        await AddColumn("Users", "SuspendedAt", "TEXT NULL");
        await AddColumn("Users", "DeletedAt", "TEXT NULL");
        // Deleted records remain closed for financial/audit history. Identities
        // are unique among live accounts so verified owners can register anew.
        await db.Database.ExecuteSqlRawAsync("""
            DROP INDEX IF EXISTS IX_Users_Email;
            CREATE UNIQUE INDEX IX_Users_Email ON Users(Email) WHERE DeletedAt IS NULL;
            DROP INDEX IF EXISTS IX_Users_Username;
            CREATE UNIQUE INDEX IX_Users_Username ON Users(Username) WHERE DeletedAt IS NULL;
            """);
        await AddColumn("Devices", "AdminPausedAt", "TEXT NULL");
        await AddColumn("Messages", "DeletedAt", "TEXT NULL");
        await db.Database.ExecuteSqlRawAsync("CREATE TABLE IF NOT EXISTS SecuritySettings (Id INTEGER NOT NULL PRIMARY KEY, AdminRequireOtp INTEGER NOT NULL DEFAULT 0); INSERT OR IGNORE INTO SecuritySettings(Id,AdminRequireOtp) VALUES(1,0);");
        var addedVerification = await AddColumn("Users", "EmailVerifiedAt", "TEXT NULL");
        var addedTrialStart = await AddColumn("Users", "TrialStartedAt", "TEXT NULL");
        // Before this release, successful registration/sign-in required proof of email.
        if (addedVerification) await db.Database.ExecuteSqlRawAsync("UPDATE Users SET EmailVerifiedAt=COALESCE(LastLoginAt,CreatedAt) WHERE LastLoginAt IS NOT NULL OR GoogleSubject IS NOT NULL");
        if (addedTrialStart) await db.Database.ExecuteSqlRawAsync("UPDATE Users SET TrialStartedAt=CreatedAt");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Users_Username ON Users(Username);
            CREATE TABLE IF NOT EXISTS EmailDispatches (Id TEXT NOT NULL PRIMARY KEY, Kind TEXT NOT NULL, UserId TEXT NOT NULL,
              MessageId TEXT NULL, SubscriptionId TEXT NULL, ChallengeId TEXT NULL, ProtectedPayload TEXT NOT NULL,
              Status TEXT NOT NULL, ErrorCode TEXT NULL, Attempts INTEGER NOT NULL, ProviderStarted INTEGER NOT NULL,
              CreatedAt TEXT NOT NULL, NextAttemptAt TEXT NOT NULL, ExpiresAt TEXT NOT NULL, AcceptedAt TEXT NULL, CompletedAt TEXT NULL);
            CREATE INDEX IF NOT EXISTS IX_EmailDispatches_Kind_NextAttemptAt ON EmailDispatches(Kind,NextAttemptAt);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_EmailDispatches_MessageId ON EmailDispatches(MessageId);
            """);
        await AddColumn("Devices", "InstallationId", "TEXT NULL");
        await AddColumn("Devices", "LastIpAddress", "TEXT NULL");
        await AddColumn("Devices", "Platform", "TEXT NULL");
        await AddColumn("Devices", "DeviceModel", "TEXT NULL");
        await AddColumn("Devices", "AppVersion", "TEXT NULL");
        await AddColumn("Messages", "ClientMessageId", "TEXT NULL");
        await AddColumn("OtpChallenges", "Attempts", "INTEGER NOT NULL DEFAULT 0");
        var addedFeatureOverrides = await AddColumn("Users", "SendOverride", "INTEGER NULL");
        await AddColumn("Users", "ReceiveOverride", "INTEGER NULL");
        // Existing databases might predate the membership tables entirely.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS Plans (Id TEXT NOT NULL PRIMARY KEY, Code TEXT NOT NULL, Name TEXT NOT NULL,
              DurationDays INTEGER NOT NULL, MessageLimit INTEGER NOT NULL, SendEnabled INTEGER NOT NULL,
              ReceiveEnabled INTEGER NOT NULL, IsActive INTEGER NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Plans_Code ON Plans(Code);
            CREATE TABLE IF NOT EXISTS Subscriptions (Id TEXT NOT NULL PRIMARY KEY, UserId TEXT NOT NULL,
              PlanId TEXT NOT NULL, StartsAt TEXT NOT NULL, ExpiresAt TEXT NOT NULL, MessageLimit INTEGER NOT NULL,
              UsedMessages INTEGER NOT NULL, SendEnabled INTEGER NOT NULL, ReceiveEnabled INTEGER NOT NULL, IsCurrent INTEGER NOT NULL,
              FOREIGN KEY(UserId) REFERENCES Users(Id) ON DELETE CASCADE, FOREIGN KEY(PlanId) REFERENCES Plans(Id) ON DELETE CASCADE);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Subscriptions_UserId ON Subscriptions(UserId) WHERE IsCurrent = 1;
            CREATE TABLE IF NOT EXISTS UsageEntries (Id TEXT NOT NULL PRIMARY KEY, UserId TEXT NOT NULL,
              SubscriptionId TEXT NOT NULL, MessageId TEXT NOT NULL, Direction TEXT NOT NULL, CreatedAt TEXT NOT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_UsageEntries_MessageId ON UsageEntries(MessageId);
            CREATE TABLE IF NOT EXISTS Activities (Id TEXT NOT NULL PRIMARY KEY, UserId TEXT NOT NULL,
              ActorId TEXT NULL, Action TEXT NOT NULL, Detail TEXT NOT NULL, CreatedAt TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Activities_UserId_CreatedAt ON Activities(UserId,CreatedAt);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Messages_DeviceId_ClientMessageId ON Messages(DeviceId,ClientMessageId);
            """);
        await AddColumn("Plans", "PriceUsdCents", "INTEGER NULL");
        await AddColumn("Plans", "PriceToman", "INTEGER NULL");
        await AddColumn("Plans", "IsPurchasable", "INTEGER NOT NULL DEFAULT 0");
        await AddColumn("Subscriptions", "PaymentOrderId", "TEXT NULL");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE UNIQUE INDEX IF NOT EXISTS IX_Subscriptions_PaymentOrderId ON Subscriptions(PaymentOrderId);
            CREATE TABLE IF NOT EXISTS PaymentOrders (
              Id TEXT NOT NULL PRIMARY KEY, UserId TEXT NOT NULL, PlanId TEXT NOT NULL,
              PlanName TEXT NOT NULL, DurationDays INTEGER NOT NULL, MessageLimit INTEGER NOT NULL,
              SendEnabled INTEGER NOT NULL, ReceiveEnabled INTEGER NOT NULL, Provider TEXT NOT NULL,
              Currency TEXT NOT NULL, AmountMinor INTEGER NOT NULL, RefundedMinor INTEGER NOT NULL,
              RequestKey TEXT NOT NULL, Status TEXT NOT NULL, ProviderSessionId TEXT NULL,
              ProviderPaymentId TEXT NULL, CheckoutUrl TEXT NULL, ReturnOrigin TEXT NOT NULL,
              CreatedAt TEXT NOT NULL, ExpiresAt TEXT NULL, PaidAt TEXT NULL, FulfilledAt TEXT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_PaymentOrders_UserId_RequestKey ON PaymentOrders(UserId,RequestKey);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_PaymentOrders_ProviderSessionId ON PaymentOrders(ProviderSessionId);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_PaymentOrders_ProviderPaymentId ON PaymentOrders(ProviderPaymentId);
            CREATE TABLE IF NOT EXISTS PaymentEvents (Id TEXT NOT NULL PRIMARY KEY, Type TEXT NOT NULL, CreatedAt TEXT NOT NULL);
            """);

        foreach (var table in new[] { "Plans", "Subscriptions", "PaymentOrders" })
            await AddColumn(table, "EmailForwardLimit", "INTEGER NOT NULL DEFAULT 0");
        await AddColumn("Subscriptions", "UsedEmailForwards", "INTEGER NOT NULL DEFAULT 0");
        await AddColumn("Messages", "EmailForwardStatus", "TEXT NOT NULL DEFAULT 'not_requested'");
        foreach (var table in new[] { "Devices", "Messages" }) {
            await AddColumn(table, "BatteryLevel", "INTEGER NULL");
            await AddColumn(table, "IsCharging", "INTEGER NULL");
            await AddColumn(table, "NetworkType", "TEXT NULL");
        }
        foreach (var col in new[] { "NetworkValidated", "SmsPermissionsGranted", "BatteryOptimizationExempt" })
            await AddColumn("Devices", col, "INTEGER NULL");
        await AddColumn("Devices", "HealthReportedAt", "TEXT NULL");
        await AddColumn("Devices", "RevokedAt", "TEXT NULL");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS DeviceLinks (Id TEXT NOT NULL PRIMARY KEY, Email TEXT NOT NULL,
              SecretHash TEXT NOT NULL, ComparisonCode TEXT NOT NULL, InstallationId TEXT NOT NULL,
              DeviceName TEXT NOT NULL, AppVersion TEXT NULL, SourceIp TEXT NOT NULL, Status TEXT NOT NULL,
              DeviceId TEXT NULL, CreatedAt TEXT NOT NULL, ExpiresAt TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_DeviceLinks_Email_CreatedAt ON DeviceLinks(Email,CreatedAt);
            CREATE TABLE IF NOT EXISTS DeviceAlerts (Id TEXT NOT NULL PRIMARY KEY, UserId TEXT NOT NULL,
              DeviceId TEXT NOT NULL, Kind TEXT NOT NULL, DeviceName TEXT NOT NULL,
              CreatedAt TEXT NOT NULL, ResolvedAt TEXT NULL, ReadAt TEXT NULL);
            CREATE UNIQUE INDEX IF NOT EXISTS IX_DeviceAlerts_DeviceId_Kind ON DeviceAlerts(DeviceId,Kind) WHERE ResolvedAt IS NULL;
            CREATE TABLE IF NOT EXISTS AppMigrations (Id TEXT NOT NULL PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS TrialClaims (Key TEXT NOT NULL PRIMARY KEY, UserId TEXT NOT NULL);
            """);
        // Toman payments through the Ezpay relay into Darvazeh's own default wallet.
        await AddColumn("PaymentOrders", "ReceivingPhone", "TEXT NULL");
        await AddColumn("PaymentOrders", "ProviderUniqueId", "TEXT NULL");
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS PaymentWallets (Id INTEGER NOT NULL PRIMARY KEY, PhoneNumber TEXT NULL, ConnectedAt TEXT NULL,
              ConnectedByUserId TEXT NULL, MerchantEnabled INTEGER NOT NULL DEFAULT 0, PendingPhoneNumber TEXT NULL, RequestUid TEXT NULL,
              ChallengeId TEXT NULL, ChallengeUserId TEXT NULL, ChallengeExpiresAt TEXT NULL, LastOtpAt TEXT NULL, FailedAttempts INTEGER NOT NULL DEFAULT 0);
            INSERT OR IGNORE INTO PaymentWallets(Id,MerchantEnabled,FailedAttempts) VALUES(1,0,0);
            CREATE TABLE IF NOT EXISTS WalletOwners (PhoneNumber TEXT NOT NULL PRIMARY KEY, OwnerUserId TEXT NOT NULL, VerifiedAt TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS WalletPhoneProofs (Id TEXT NOT NULL PRIMARY KEY, UserId TEXT NOT NULL, PhoneNumber TEXT NOT NULL,
              CodeHash TEXT NOT NULL, Attempts INTEGER NOT NULL, Consumed INTEGER NOT NULL, RequestedFromIp TEXT NULL,
              CreatedAt TEXT NOT NULL, ExpiresAt TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_WalletPhoneProofs_PhoneNumber_CreatedAt ON WalletPhoneProofs(PhoneNumber,CreatedAt);
            """);
        if (addedFeatureOverrides)
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE Users SET SendOverride=(SELECT CASE WHEN s.SendEnabled<>p.SendEnabled THEN s.SendEnabled ELSE NULL END
                  FROM Subscriptions s JOIN Plans p ON p.Id=s.PlanId WHERE s.UserId=Users.Id AND s.IsCurrent=1),
                ReceiveOverride=(SELECT CASE WHEN s.ReceiveEnabled<>p.ReceiveEnabled THEN s.ReceiveEnabled ELSE NULL END
                  FROM Subscriptions s JOIN Plans p ON p.Id=s.PlanId WHERE s.UserId=Users.Id AND s.IsCurrent=1);
                """);
        if (!await db.Plans.AnyAsync(p => p.Code == "trial"))
        {
            db.Plans.Add(new MembershipPlan { Code = "trial", Name = "Trial", DurationDays = 3, MessageLimit = 20 });
            await db.SaveChangesAsync();
        }
        await db.Plans.Where(p => p.Code == "trial").ExecuteUpdateAsync(update => update
            .SetProperty(p => p.PriceUsdCents, 0L).SetProperty(p => p.PriceToman, 0L).SetProperty(p => p.IsPurchasable, false));

        // Apply this product change once; later administrator edits remain authoritative.
        var connectionForVersion = db.Database.GetDbConnection();
        using (var version = connectionForVersion.CreateCommand()) {
            version.Transaction = transaction.GetDbTransaction();
            version.CommandText = "SELECT COUNT(*) FROM AppMigrations WHERE Id='device-link-health-plans-v1'";
            if (Convert.ToInt32(await version.ExecuteScalarAsync()) == 0) {
                await db.Database.ExecuteSqlRawAsync("""
                    UPDATE Plans SET MessageLimit=20, DurationDays=3, EmailForwardLimit=20 WHERE Code='trial';
                    UPDATE Subscriptions SET MessageLimit=20, EmailForwardLimit=20 WHERE PlanId IN (SELECT Id FROM Plans WHERE Code='trial');
                    UPDATE Plans SET PriceUsdCents=200, EmailForwardLimit=10, IsPurchasable=1 WHERE Code='silver';
                    UPDATE Plans SET PriceUsdCents=300, EmailForwardLimit=200, MessageLimit=200, DurationDays=30, IsPurchasable=1 WHERE Code='gold';
                    UPDATE Subscriptions SET EmailForwardLimit=10 WHERE PlanId IN (SELECT Id FROM Plans WHERE Code='silver');
                    UPDATE PaymentOrders SET EmailForwardLimit=10 WHERE PlanId IN (SELECT Id FROM Plans WHERE Code='silver');
                    UPDATE Messages SET EmailForwardStatus='sent' WHERE EmailForwarded=1;
                    UPDATE Subscriptions SET UsedEmailForwards=(SELECT COUNT(*) FROM Messages m JOIN UsageEntries u ON u.MessageId=m.Id
                      WHERE u.SubscriptionId=Subscriptions.Id AND m.EmailForwarded=1);
                    INSERT INTO AppMigrations(Id) VALUES('device-link-health-plans-v1');
                    """);
                if (!await db.Plans.AnyAsync(p => p.Code == "silver"))
                    db.Plans.Add(new MembershipPlan { Code="silver", Name="Silver", DurationDays=30, MessageLimit=200,
                        EmailForwardLimit=10, PriceUsdCents=200, IsPurchasable=true });
                if (!await db.Plans.AnyAsync(p => p.Code == "gold"))
                    db.Plans.Add(new MembershipPlan { Code="gold", Name="Gold", DurationDays=30, MessageLimit=200,
                        EmailForwardLimit=200, PriceUsdCents=300, IsPurchasable=true });
                await db.SaveChangesAsync();
            }
        }
        var service = new MembershipService(db);
        foreach(var previous in await db.Users.AsNoTracking().Where(u=>u.TrialStartedAt!=null).ToListAsync()) {
            var key=MembershipService.TrialKey(previous.Email);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT OR IGNORE INTO TrialClaims(Key,UserId) VALUES ({key},{previous.Id.ToString().ToUpperInvariant()})");
        }
        if (!await db.Users.AnyAsync(u => u.Email == adminEmail))
        {
            db.Users.Add(new AppUser { Email = adminEmail.ToLowerInvariant(), Role = UserRole.Admin,
                ForwardEmailAddress = adminEmail.ToLowerInvariant() });
            await db.SaveChangesAsync();
        }
        foreach (var user in await db.Users.ToListAsync())
        {
            user.Role = string.Equals(user.Email, adminEmail, StringComparison.OrdinalIgnoreCase) ? UserRole.Admin : UserRole.User;
            if (await db.Subscriptions.AnyAsync(s => s.UserId == user.Id && s.IsCurrent)) continue;
            await service.StartTrial(user);
            var subscription = db.Subscriptions.Local.Single(s => s.UserId == user.Id);
            // Preserve and account for historical messages during this additive upgrade.
            var messages = await db.Messages.Where(m => m.UserId == user.Id).ToListAsync();
            subscription.UsedMessages = messages.Count;
            foreach (var message in messages) db.UsageEntries.Add(new UsageEntry {
                UserId = user.Id, SubscriptionId = subscription.Id, MessageId = message.Id,
                Direction = message.Direction, CreatedAt = message.CreatedAt });
        }
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
    }
}
