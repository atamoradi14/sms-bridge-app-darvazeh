using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
namespace FieldSense.Api.Services;
public class GoogleSettings {
    public bool Enabled {get;set;}
    public string ClientId {get;set;}="";
    public string ClientSecret {get;set;}="";
    public string RedirectUri {get;set;}="https://api1.daryajan.com/api/auth/google/callback";
    public string PortalOrigin {get;set;}="https://darvazeh.daryajan.com";
    public bool Configured=>Enabled && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(ClientSecret);
}
public record GoogleIdentity(string Subject,string Email,bool Authoritative);
public interface IGoogleIdentityProvider {
    string AuthorizationUrl(string state,string nonce,string challenge);
    Task<GoogleIdentity> Exchange(string code,string verifier,string nonce,CancellationToken ct);
}
public class GoogleIdentityProvider(HttpClient http,IOptions<GoogleSettings> options) : IGoogleIdentityProvider {
    private readonly GoogleSettings settings=options.Value;
    private static readonly ConfigurationManager<OpenIdConnectConfiguration> Discovery=new(
        "https://accounts.google.com/.well-known/openid-configuration",new OpenIdConnectConfigurationRetriever());
    public string AuthorizationUrl(string state,string nonce,string challenge) {
        var values=new Dictionary<string,string> { ["client_id"]=settings.ClientId,["redirect_uri"]=settings.RedirectUri,["response_type"]="code",
            ["scope"]="openid email",["state"]=state,["nonce"]=nonce,["code_challenge"]=challenge,["code_challenge_method"]="S256",["prompt"]="select_account" };
        return "https://accounts.google.com/o/oauth2/v2/auth?"+string.Join("&",values.Select(p=>Uri.EscapeDataString(p.Key)+"="+Uri.EscapeDataString(p.Value)));
    }
    public static GoogleIdentity Validate(string token,OpenIdConnectConfiguration config,string audience,string nonce) {
        if(nonce.Length<16)throw new SecurityTokenValidationException("Invalid nonce");
        var handler=new JwtSecurityTokenHandler {MapInboundClaims=false};
        var principal=handler.ValidateToken(token,new TokenValidationParameters {
            ValidateIssuer=true,ValidIssuers=new[]{"https://accounts.google.com","accounts.google.com"},ValidateAudience=true,ValidAudience=audience,
            ValidateLifetime=true,RequireExpirationTime=true,RequireSignedTokens=true,ValidateIssuerSigningKey=true,IssuerSigningKeys=config.SigningKeys,
            ValidAlgorithms=new[]{SecurityAlgorithms.RsaSha256},ClockSkew=TimeSpan.FromSeconds(30)
        },out var parsed);
        var jwt=(JwtSecurityToken)parsed;
        var actualNonce=principal.FindFirstValue("nonce")??"";
        if(!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(actualNonce),Encoding.UTF8.GetBytes(nonce)))throw new SecurityTokenValidationException("Invalid nonce");
        var azp=principal.FindFirstValue("azp");
        if((azp!=null && azp!=audience) || (jwt.Audiences.Count()>1 && azp!=audience))throw new SecurityTokenValidationException("Invalid authorized party");
        var subject=principal.FindFirstValue("sub")??"";var email=(principal.FindFirstValue("email")??"").Trim().ToLowerInvariant();
        if(subject.Length is <1 or >255 || email.Length>254 || !System.Net.Mail.MailAddress.TryCreate(email,out var address) || address.Address!=email ||
            !string.Equals(principal.FindFirstValue("email_verified"),"true",StringComparison.OrdinalIgnoreCase))throw new SecurityTokenValidationException("Unverified identity");
        return new(subject,email,email.EndsWith("@gmail.com",StringComparison.OrdinalIgnoreCase) || !string.IsNullOrWhiteSpace(principal.FindFirstValue("hd")));
    }
    public async Task<GoogleIdentity> Exchange(string code,string verifier,string nonce,CancellationToken ct) {
        using var response=await http.PostAsync("https://oauth2.googleapis.com/token",new FormUrlEncodedContent(new Dictionary<string,string> {
            ["code"]=code,["client_id"]=settings.ClientId,["client_secret"]=settings.ClientSecret,["redirect_uri"]=settings.RedirectUri,["grant_type"]="authorization_code",["code_verifier"]=verifier
        }),ct);
        if(!response.IsSuccessStatusCode)throw new SecurityTokenValidationException("Token exchange failed");
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var idToken=json.RootElement.GetProperty("id_token").GetString()??"";
        var config=await Discovery.GetConfigurationAsync(ct);
        try{return Validate(idToken,config,settings.ClientId,nonce);}
        catch(SecurityTokenSignatureKeyNotFoundException){Discovery.RequestRefresh();throw;}
    }
}
