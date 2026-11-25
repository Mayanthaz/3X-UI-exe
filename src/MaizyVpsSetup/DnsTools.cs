using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace MaizyVpsSetup;

public sealed record DnsReport(List<string> A, List<string> AAAA)
{
    /// <summary>True when the A records are Cloudflare's own proxy addresses (orange cloud).</summary>
    public bool Proxied => A.Count > 0 && A.All(DnsTools.IsCloudflareIp);
}

public static class DnsTools
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    // https://www.cloudflare.com/ips-v4
    private static readonly (uint net, int bits)[] CloudflareRanges = new[]
    {
        "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18",
        "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17",
        "162.158.0.0/15", "104.16.0.0/13", "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
    }.Select(c => { var p = c.Split('/'); return (ToUInt(IPAddress.Parse(p[0])), int.Parse(p[1])); }).ToArray();

    private static uint ToUInt(IPAddress ip)
    {
        var b = ip.GetAddressBytes();
        return (uint)(b[0] << 24 | b[1] << 16 | b[2] << 8 | b[3]);
    }

    public static bool IsCloudflareIp(string ip)
    {
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var v = ToUInt(addr);
        return CloudflareRanges.Any(r => (v >> (32 - r.bits)) == (r.net >> (32 - r.bits)));
    }

    /// <summary>Looks up A and AAAA records through Cloudflare DNS-over-HTTPS (bypasses the Windows DNS cache).</summary>
    public static async Task<DnsReport> ResolveAsync(string host, CancellationToken ct = default)
    {
        var a = ResolveTypeAsync(host, "A", 1, ct);
        var aaaa = ResolveTypeAsync(host, "AAAA", 28, ct);
        return new DnsReport(await a, await aaaa);
    }

    public static Task<List<string>> ResolveARecordsAsync(string host, CancellationToken ct = default) =>
        ResolveTypeAsync(host, "A", 1, ct);

    private static async Task<List<string>> ResolveTypeAsync(string host, string type, int typeCode, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get,
            $"https://cloudflare-dns.com/dns-query?name={Uri.EscapeDataString(host)}&type={type}");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-json"));
        using var res = await Http.SendAsync(req, ct);
        res.EnsureSuccessStatusCode();
        var doc = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
        var list = new List<string>();
        if (doc?["Answer"] is JsonArray answers)
            foreach (var x in answers)
                if ((int?)x?["type"] == typeCode && x?["data"]?.GetValue<string>() is { } ip)
                    list.Add(ip);
        return list;
    }

    /// <summary>
    /// Points the A record for <paramref name="host"/> at <paramref name="ip"/> (DNS only, not proxied) and removes
    /// AAAA records for the same name, which would otherwise keep sending IPv6 users to the old server.
    /// </summary>
    public static async Task<List<string>> UpdateCloudflareAsync(string apiToken, string host, string ip, CancellationToken ct = default)
    {
        var messages = new List<string>();
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
            messages.Add($"Created A record {host} -> {ip} (DNS only)");
        }
        else
        {
            var oldIp = (string?)a["content"];
            var wasProxied = (bool?)a["proxied"] == true;
            if (oldIp == ip && !wasProxied)
                messages.Add($"A record already points to {ip}");
            else
            {
                await CfAsync(apiToken, HttpMethod.Patch, $"zones/{zoneId}/dns_records/{(string?)a["id"]}", body, ct);
                messages.Add($"Updated A record {host}: {oldIp} -> {ip}" + (wasProxied ? " (proxy turned off: DNS only)" : " (DNS only)"));
            }
        }

        foreach (var aaaa in all.Where(r => (string?)r?["type"] == "AAAA"))
        {
            await CfAsync(apiToken, HttpMethod.Delete, $"zones/{zoneId}/dns_records/{(string?)aaaa!["id"]}", null, ct);
            messages.Add($"Removed old IPv6 record {host} -> {(string?)aaaa["content"]}");
        }
        return messages;
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
