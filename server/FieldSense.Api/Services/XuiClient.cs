using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;

namespace FieldSense.Api.Services;

/// <summary>One 3x-ui panel. The API token is an administrator credential: it stays in server configuration only.</summary>
public class VpnServerOptions
{
    public string Code { get; set; } = "";
    public string Name { get; set; } = "";
    public string NameFa { get; set; } = "";
    /// <summary>Full panel URL including its private web base path, e.g. https://panel.example.com/secretPath</summary>
    public string PanelBaseUrl { get; set; } = "";
    /// <summary>Public subscription prefix the client SubId is appended to, e.g. https://direct.example.com/subPath/</summary>
    public string SubscriptionBaseUrl { get; set; } = "";
    public string ApiToken { get; set; } = "";
    public string InboundIds { get; set; } = "1,2,3";
    /// <summary>Vision flow; 3x-ui only applies it to compatible (TCP/REALITY) inbounds.</summary>
    public string Flow { get; set; } = "xtls-rprx-vision";
    public bool Enabled { get; set; } = true;

    public int[] Inbounds => InboundIds.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(v => int.TryParse(v, out var n) ? n : 0).Where(n => n > 0).Distinct().ToArray();
    public bool Configured => Enabled && System.Text.RegularExpressions.Regex.IsMatch(Code, "^[a-z0-9-]{2,20}$") &&
        Uri.TryCreate(PanelBaseUrl, UriKind.Absolute, out var p) && Secure(p) &&
        Uri.TryCreate(SubscriptionBaseUrl, UriKind.Absolute, out var s) && Secure(s) &&
        ApiToken.Length >= 16 && Inbounds.Length > 0;
    // Plain HTTP is accepted only for a loopback test fixture; real panels and subscriptions must be HTTPS.
    private static bool Secure(Uri u) => u.Scheme == "https" || (u.Scheme == "http" && u.IsLoopback);
    public string SubscriptionUrl(string subId) => SubscriptionBaseUrl.TrimEnd('/') + "/" + subId;
}

public class VpnSettings
{
    public List<VpnServerOptions> Servers { get; set; } = [];
    public VpnServerOptions? Find(string code) => Servers.FirstOrDefault(s => s.Code == code && s.Configured);
    public IEnumerable<VpnServerOptions> Available => Servers.Where(s => s.Configured);
}

public class XuiException(string message, bool notFound = false) : Exception(message)
{
    public bool NotFound { get; } = notFound;
}

/// <summary>Typed adapter for the 3x-ui v3.11 client API. Checks HTTP status and the JSON "success" field.</summary>
public class XuiClient(HttpClient http)
{
    private static string Esc(string v) => Uri.EscapeDataString(v);

    private async Task<JsonNode?> Call(VpnServerOptions server, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, server.PanelBaseUrl.TrimEnd('/') + "/panel/api/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", server.ApiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.UserAgent.ParseAdd("Darvazeh-VPN/1.0");
        if (body != null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        else if (method == HttpMethod.Post) request.Content = new StringContent("", Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, ct); }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested) { throw new XuiException("panel_timeout"); }
        catch (HttpRequestException) { throw new XuiException("panel_unreachable"); }
        using (response)
        {
            var status = (int)response.StatusCode;
            // An invalid token or wrong base path redirects to the login page instead of returning JSON.
            if (status is >= 300 and < 400) throw new XuiException("panel_redirect: check the panel base URL and API token");
            if (status is 401 or 403) throw new XuiException("panel_unauthorized: check the API token");
            if (status == 404) throw new XuiException("panel_not_found: check the panel base URL");
            if (!response.IsSuccessStatusCode) throw new XuiException("panel_http_" + status);
            var text = await response.Content.ReadAsStringAsync(ct);
            JsonNode? node;
            try { node = JsonNode.Parse(text); }
            catch (System.Text.Json.JsonException) { throw new XuiException("panel_invalid_response"); }
            var success = node?["success"] is JsonValue v && v.TryGetValue<bool>(out var ok) && ok;
            if (!success)
            {
                var msg = node?["msg"]?.ToString() ?? "failed";
                if (msg.Length > 300) msg = msg[..300];
                throw new XuiException("panel: " + msg, msg.Contains("not found", StringComparison.OrdinalIgnoreCase) ||
                    msg.Contains("not exist", StringComparison.OrdinalIgnoreCase));
            }
            return node!["obj"];
        }
    }

    /// <summary>Returns { client: ClientRecord, inboundIds: [...] } or null when the email is unknown.</summary>
    public async Task<JsonObject?> GetClient(VpnServerOptions s, string email, CancellationToken ct = default)
    {
        try { return (await Call(s, HttpMethod.Get, "clients/get/" + Esc(email), null, ct)) as JsonObject; }
        catch (XuiException e) when (e.NotFound) { return null; }
    }

    public Task Add(VpnServerOptions s, JsonObject client, int[] inboundIds, CancellationToken ct = default) =>
        Call(s, HttpMethod.Post, "clients/add", new JsonObject { ["client"] = client, ["inboundIds"] = new JsonArray(inboundIds.Select(i => (JsonNode)i).ToArray()) }, ct);

    public Task Attach(VpnServerOptions s, string email, int[] inboundIds, CancellationToken ct = default) =>
        Call(s, HttpMethod.Post, "clients/" + Esc(email) + "/attach", new JsonObject { ["inboundIds"] = new JsonArray(inboundIds.Select(i => (JsonNode)i).ToArray()) }, ct);

    public Task Update(VpnServerOptions s, string email, JsonObject client, CancellationToken ct = default) =>
        Call(s, HttpMethod.Post, "clients/update/" + Esc(email), client, ct);

    public async Task Delete(VpnServerOptions s, string email, CancellationToken ct = default)
    {
        try { await Call(s, HttpMethod.Post, "clients/del/" + Esc(email), null, ct); }
        catch (XuiException e) when (e.NotFound) { }
    }

    public Task ResetTraffic(VpnServerOptions s, string email, CancellationToken ct = default) =>
        Call(s, HttpMethod.Post, "clients/resetTraffic/" + Esc(email), null, ct);

    public record Traffic(long Up, long Down, bool Enable, long LastOnline);
    public async Task<Traffic?> GetTraffic(VpnServerOptions s, string email, CancellationToken ct = default)
    {
        JsonNode? obj;
        try { obj = await Call(s, HttpMethod.Get, "clients/traffic/" + Esc(email), null, ct); }
        catch (XuiException e) when (e.NotFound) { return null; }
        if (obj is not JsonObject o) return null;
        static long L(JsonNode? n) => n is JsonValue v && v.TryGetValue<long>(out var x) ? x : 0;
        return new(L(o["up"]), L(o["down"]), o["enable"] is JsonValue en && en.TryGetValue<bool>(out var b) && b, L(o["lastOnline"]));
    }

    public async Task<HashSet<string>> Onlines(VpnServerOptions s, CancellationToken ct = default)
    {
        var obj = await Call(s, HttpMethod.Post, "clients/onlines", null, ct);
        return obj is JsonArray a ? a.Select(x => x?.ToString() ?? "").Where(x => x != "").ToHashSet() : [];
    }

    /// <summary>Distinct source IPs recently seen for the client (3x-ui returns "ip (timestamp)" strings).</summary>
    public async Task<int> IpCount(VpnServerOptions s, string email, CancellationToken ct = default)
    {
        var obj = await Call(s, HttpMethod.Post, "clients/ips/" + Esc(email), null, ct);
        if (obj is not JsonArray a) return 0;
        return a.Select(x => (x?.ToString() ?? "").Split(' ')[0]).Where(ip => System.Net.IPAddress.TryParse(ip, out _)).Distinct().Count();
    }

    public async Task<JsonArray> Inbounds(VpnServerOptions s, CancellationToken ct = default) =>
        await Call(s, HttpMethod.Get, "inbounds/list", null, ct) as JsonArray ?? [];

    public async Task<int> SubLinkCount(VpnServerOptions s, string subId, CancellationToken ct = default) =>
        (await Call(s, HttpMethod.Get, "clients/subLinks/" + Esc(subId), null, ct)) is JsonArray a ? a.Count : 0;

    /// <summary>
    /// Converts the read model into the write model, as proven on v3.11: the numeric database id is replaced by the
    /// VLESS UUID and allowedIPs is decoded from its JSON string. The update endpoint replaces, so everything else is kept.
    /// </summary>
    public static JsonObject WriteModel(JsonObject record)
    {
        var w = (JsonObject)record.DeepClone();
        w["id"] = record["uuid"]?.DeepClone();
        var ips = record["allowedIPs"];
        if (ips is JsonValue v && v.TryGetValue<string>(out var text))
            w["allowedIPs"] = string.IsNullOrWhiteSpace(text) ? new JsonArray() : JsonNode.Parse(text);
        else if (ips == null) w["allowedIPs"] = new JsonArray();
        return w;
    }
}
