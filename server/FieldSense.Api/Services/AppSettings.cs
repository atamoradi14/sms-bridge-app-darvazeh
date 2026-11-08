namespace FieldSense.Api.Services;

public class AppSettings
{
    public string JwtKey { get; set; } = "dev-fieldsense-change-me-to-a-long-secret-key-32+";
    public string AdminEmail { get; set; } = "admin@ndorbit.co.nz";
    public string AdminPassword { get; set; } = "ChangeMeAdmin!";
    public string AcsConnectionString { get; set; } = "";
    public string AcsSenderAddress { get; set; } = "DoNotReply@ndorbit.co.nz";
    public bool AcsMock { get; set; } = true;
    public string CorsOrigin { get; set; } = "http://127.0.0.1:43123,http://localhost:43123";
}
