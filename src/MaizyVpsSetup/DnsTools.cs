using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaizyVpsSetup;

public static class DnsTools
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Looks up A records through Cloudflare DNS-over-HTTPS (bypasses the local Windows DNS cache).</summary>
    public static async Task<List<string>> ResolveARecordsAsync(string host, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type=A");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
        using var res = await Http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var doc = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        var list = new List<string>();
        if (doc?["Answer"] is JsonArray answers)
            foreach (var a in answers)
                if ((int?)a?["type"] == 1 && a?["data"]?.GetValue<string>() is { } ip)
                    list.Add(ip);
        return list;
    }

    /// <summary>Points the A record for <paramref name="host"/> at <paramref name="ip"/> (DNS only, not proxied).</summary>
    public static async Task<string> UpdateCloudflareAsync(string apiToken, string host, string ip, CancellationToken ct = default)
    {
        var zoneId = await FindZoneAsync(apiToken, host, ct)
            ?? throw new InvalidOperationException($"No Cloudflare zone found for {host}. Check the API token has Zone:Read and DNS:Edit for this domain.");

        var records = await CfAsync(apiToken, HttpMethod.Get,
            $"zones/{zoneId}/dns_records?name={Uri.EscapeDataString(host)}", null, ct);
        var all = records["result"]?.AsArray() ?? new JsonArray();
        var a = all.FirstOrDefault(r => (string?)r?["type"] == "A");
        var cname = all.FirstOrDefault(r => (string?)r?["type"] == "CNAME");
        if (a is null && cname is not null)
            throw new InvalidOperationException($"{host} is a CNAME record in Cloudflare. Change it to an A record first.");

        var body = new JsonObject
        {
            ["type"] = "A",
            ["name"] = host,
            ["content"] = ip,
            ["ttl"] = 60,
            ["proxied"] = false, // Xray TLS on custom ports cannot go through the orange-cloud proxy.
        };

        if (a is null)
        {
            await CfAsync(apiToken, HttpMethod.Post, $"zones/{zoneId}/dns_records", body, ct);
            return $"Created A record {host} -> {ip} (DNS only)";
        }

        var oldIp = (string?)a["content"];
        var wasProxied = (bool?)a["proxied"] == true;
        if (oldIp == ip && !wasProxied)
            return $"A record already points to {ip}";

        await CfAsync(apiToken, HttpMethod.Patch, $"zones/{zoneId}/dns_records/{(string?)a["id"]}", body, ct);
        return $"Updated A record {host}: {oldIp} -> {ip} (DNS only)";
    }

    private static async Task<string?> FindZoneAsync(string token, string host, CancellationToken ct)
    {
        var labels = host.Trim('.').Split('.');
        for (var i = 0; i <= labels.Length - 2; i++)
        {
            var candidate = string.Join('.', labels[i..]);
            var res = await CfAsync(token, HttpMethod.Get, $"zones?name={Uri.EscapeDataString(candidate)}", null, ct);
            var first = res["result"]?.AsArray().FirstOrDefault();
            if (first?["id"] is { } id) return (string?)id;
        }
        return null;
    }

    private static async Task<JsonNode> CfAsync(string token, HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, "https://api.cloudflare.com/client/v4/" + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        if (body is not null) req.Content = JsonContent.Create(body);
        using var res = await Http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        JsonNode? json;
        try { json = JsonNode.Parse(text); }
        catch (JsonException) { throw new InvalidOperationException($"Cloudflare returned HTTP {(int)res.StatusCode}."); }

        if (json is null || (bool?)json["success"] != true)
        {
            var errors = json?["errors"]?.AsArray().Select(e => (string?)e?["message"]).Where(m => m != null);
            var msg = errors is null ? "" : string.Join("; ", errors);
            throw new InvalidOperationException($"Cloudflare API error (HTTP {(int)res.StatusCode}): {(msg.Length > 0 ? msg : text)}");
        }
        return json;
    }
}
