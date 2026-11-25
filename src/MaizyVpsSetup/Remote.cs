using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using Renci.SshNet;

namespace MaizyVpsSetup;

public sealed class SshTarget
{
    public required string Host { get; init; }
    public int Port { get; init; } = 22;
    public required string User { get; init; }
    public bool UseKey { get; init; }
    public string Password { get; init; } = "";
    public string KeyPath { get; init; } = "";
    public string KeyPassphrase { get; init; } = "";

    public bool IsRoot => User == "root";

    public ConnectionInfo ToConnectionInfo()
    {
        var methods = new List<AuthenticationMethod>();
        if (UseKey)
        {
            var key = string.IsNullOrEmpty(KeyPassphrase) ? new PrivateKeyFile(KeyPath) : new PrivateKeyFile(KeyPath, KeyPassphrase);
            methods.Add(new PrivateKeyAuthenticationMethod(User, key));
        }
        else
        {
            methods.Add(new PasswordAuthenticationMethod(User, Password));
            var kbd = new KeyboardInteractiveAuthenticationMethod(User);
            kbd.AuthenticationPrompt += (_, e) =>
            {
                foreach (var p in e.Prompts) p.Response = Password;
            };
            methods.Add(kbd);
        }
        return new ConnectionInfo(Host, Port, User, methods.ToArray()) { Timeout = TimeSpan.FromSeconds(20) };
    }
}

public static class Remote
{
    private static T Connect<T>(T client, Action<string>? log) where T : BaseClient
    {
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = true;
            log?.Invoke($"     Server key ({e.HostKeyName}): SHA256:{e.FingerPrintSHA256}");
        };
        client.KeepAliveInterval = TimeSpan.FromSeconds(20);
        client.Connect();
        return client;
    }

    /// <summary>Connects and returns a short description of the server.</summary>
    public static string Test(SshTarget t)
    {
        using var ssh = Connect(new SshClient(t.ToConnectionInfo()), null);
        using var cmd = ssh.RunCommand(
            ". /etc/os-release 2>/dev/null; echo \"${PRETTY_NAME:-Linux} ($(uname -m))\"; " +
            "if [ -x /usr/local/x-ui/x-ui ]; then echo '3x-ui: installed'; else echo '3x-ui: not installed'; fi; " +
            "id -un");
        var lines = cmd.Result.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return string.Join("  |  ", lines.Take(2)) + (lines.Length > 2 ? $"  |  logged in as {lines[2]}" : "");
    }

    public static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    public static string BuildScript(string resource, IReadOnlyDictionary<string, string> vars)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MaizyVpsSetup." + resource)
            ?? throw new InvalidOperationException("Embedded script missing: " + resource);
        using var reader = new StreamReader(stream);
        var body = reader.ReadToEnd().Replace("\r\n", "\n");
        var firstNewLine = body.IndexOf('\n');
        if (body.StartsWith("#!")) body = body[(firstNewLine + 1)..];

        var sb = new StringBuilder("#!/usr/bin/env bash\n");
        foreach (var (k, v) in vars) sb.Append(k).Append('=').Append(Quote(v)).Append('\n');
        sb.Append(body);
        return sb.ToString();
    }

    /// <summary>
    /// Connects, lets <paramref name="before"/> upload files (it gets the SFTP client and the home folder and
    /// may set variables), uploads and runs the embedded script as root while streaming its output, then calls
    /// <paramref name="after"/> when the script succeeded. Returns the script exit code.
    /// </summary>
    public static int RunScript(SshTarget t, string resource, Dictionary<string, string> vars,
        Action<string> log, CancellationToken ct,
        Action<SftpClient, string>? before = null, Action<SftpClient, string>? after = null)
    {
        log("==> Connecting to " + t.Host + ":" + t.Port);
        var ci = t.ToConnectionInfo();
        using var ssh = Connect(new SshClient(ci), log);
        using var sftp = Connect(new SftpClient(ci), null);
        log("[OK] Connected as " + t.User);

        var home = sftp.WorkingDirectory.TrimEnd('/');
        var remoteScript = home + "/maizy-" + Guid.NewGuid().ToString("N")[..8] + ".sh";
        var cleanup = new List<string> { remoteScript };

        try
        {
            ct.ThrowIfCancellationRequested();
            before?.Invoke(sftp, home);
            foreach (var key in new[] { "DB_SRC", "BK_DIR" })
                if (vars.TryGetValue(key, out var path) && path.Length > 0) cleanup.Add(path);

            var script = BuildScript(resource, vars);
            using (var ms = new MemoryStream(new UTF8Encoding(false).GetBytes(script)))
                sftp.UploadFile(ms, remoteScript, true);
            sftp.ChangePermissions(remoteScript, 0x1C0 /* 0700 */);

            string? sudoPassword = null;
            string command;
            if (t.IsRoot) command = $"bash {Quote(remoteScript)} 2>&1";
            else if (!t.UseKey && t.Password.Length > 0)
            {
                command = $"sudo -S -p '' bash {Quote(remoteScript)} 2>&1";
                sudoPassword = t.Password;
            }
            else command = $"sudo -n bash {Quote(remoteScript)} 2>&1";

            ct.ThrowIfCancellationRequested();
            int exit;
            using (var cmd = ssh.CreateCommand(command))
            {
                var exec = cmd.ExecuteAsync(ct);
                using (var input = cmd.CreateInputStream())
                {
                    if (sudoPassword is not null)
                        input.Write(Encoding.UTF8.GetBytes(sudoPassword + "\n"));
                }

                var pump = Task.Run(() =>
                {
                    using var sr = new StreamReader(cmd.OutputStream, Encoding.UTF8);
                    string? line;
                    while ((line = sr.ReadLine()) != null) log(line);
                });

                try { exec.GetAwaiter().GetResult(); }
                finally { pump.Wait(TimeSpan.FromSeconds(5)); }
                exit = cmd.ExitStatus ?? -1;
            }

            if (exit == 0) after?.Invoke(sftp, home);
            return exit;
        }
        finally
        {
            // Scripts can hold the Cloudflare token and uploads hold the database: never leave them behind.
            // Everything listed lives in the SSH user's home folder and is owned by that user.
            try
            {
                if (ssh.IsConnected)
                {
                    using var c = ssh.RunCommand("rm -rf " + string.Join(' ', cleanup.Select(Quote)));
                }
            }
            catch { /* best effort */ }
        }
    }

    /// <summary>Uploads the backup (optional) and runs the full setup.</summary>
    public static int RunSetup(SshTarget t, string? localDb, Dictionary<string, string> vars,
        Action<string> log, CancellationToken ct)
    {
        vars["DB_SRC"] = "";
        return RunScript(t, "remote-setup.sh", vars, log, ct, before: (sftp, home) =>
        {
            if (localDb is null) return;
            var remoteDb = home + "/maizy-restore.db";
            vars["DB_SRC"] = remoteDb;
            var size = new FileInfo(localDb).Length;
            log($"==> Uploading database backup ({size / 1024.0:0} KB)");
            using var fs = File.OpenRead(localDb);
            var lastPct = -1;
            sftp.UploadFile(fs, remoteDb, true, sent =>
            {
                var pct = size == 0 ? 100 : (int)(sent * 100 / (ulong)size);
                if (pct / 25 != lastPct / 25) { lastPct = pct; log($"     {pct}%"); }
            });
            log("[OK] Backup uploaded");
        });
    }

    /// <summary>Runs one management action: health, restart, logs or renew.</summary>
    public static int RunTool(SshTarget t, string action, string domain, string cfToken, IEnumerable<int> ports,
        Action<string> log, CancellationToken ct)
    {
        var vars = new Dictionary<string, string>
        {
            ["ACTION"] = action, ["DOMAIN"] = domain, ["CF_TOKEN"] = cfToken,
            ["BK_DIR"] = "", ["BK_OWNER"] = t.User, ["OPEN_PORTS"] = string.Join(' ', ports),
        };
        return RunScript(t, "remote-tools.sh", vars, log, ct);
    }

    /// <summary>
    /// Downloads the live database into <paramref name="saveFolder"/> as &lt;ip&gt;_&lt;yyyy-MM-dd&gt;_&lt;HHmmss&gt;.db,
    /// the same naming as 3x-ui's own backups. Returns the saved path.
    /// </summary>
    public static string DownloadBackup(SshTarget t, string saveFolder, Action<string> log, CancellationToken ct)
    {
        var work = Path.Combine(Path.GetTempPath(), "maizy-bk-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var vars = new Dictionary<string, string>
            {
                ["ACTION"] = "backup", ["DOMAIN"] = "", ["CF_TOKEN"] = "", ["BK_DIR"] = "",
                ["BK_OWNER"] = t.User, ["OPEN_PORTS"] = "",
            };
            var exit = RunScript(t, "remote-tools.sh", vars, log, ct,
                before: (_, home) => vars["BK_DIR"] = home + "/maizy-backup",
                after: (sftp, _) =>
                {
                    var dir = vars["BK_DIR"];
                    foreach (var name in new[] { "x-ui.db", "x-ui.db-wal" })
                    {
                        if (!sftp.Exists(dir + "/" + name)) continue;
                        using var fs = File.Create(Path.Combine(work, name));
                        sftp.DownloadFile(dir + "/" + name, fs);
                    }
                    log("[OK] Downloaded");
                });
            if (exit != 0) throw new InvalidOperationException($"Backup failed on the server (exit code {exit}).");

            // Merge the WAL into one self-contained file, then verify it.
            var local = Path.Combine(work, "x-ui.db");
            BackupInfo.Consolidate(local);
            var info = BackupInfo.Load(local);
            Directory.CreateDirectory(saveFolder);
            var saved = Path.Combine(saveFolder, $"{t.Host}_{DateTime.Now:yyyy-MM-dd_HHmmss}.db");
            File.Copy(local, saved, overwrite: false);
            log($"[OK] Saved {saved}  ({info.Inbounds.Count} inbounds, integrity ok)");
            return saved;
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            try { Directory.Delete(work, true); } catch { /* temp */ }
        }
    }

    /// <summary>
    /// Tests each port from this PC with a real TLS handshake (SNI = <paramref name="sni"/>). A bare TCP connect is
    /// not enough: VPN/proxy apps in TUN mode accept every connection locally, so only an answer from the real
    /// server counts. The panel, subscription and TLS inbounds all answer; non-TLS inbounds report false.
    /// </summary>
    public static async Task<SortedDictionary<int, bool>> CheckPortsAsync(string ip, string sni, IEnumerable<int> ports, CancellationToken ct)
    {
        var addr = IPAddress.Parse(ip);
        var tasks = ports.Distinct().Select(async p =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(addr, p, timeout.Token);
                using var tls = new System.Net.Security.SslStream(client.GetStream(), false, (_, _, _, _) => true);
                await tls.AuthenticateAsClientAsync(new System.Net.Security.SslClientAuthenticationOptions
                {
                    TargetHost = sni,
                    ApplicationProtocols = new() { System.Net.Security.SslApplicationProtocol.Http11 },
                }, timeout.Token);
                return (Port: p, Open: true);
            }
            catch when (!ct.IsCancellationRequested) { return (Port: p, Open: false); }
        });
        return new SortedDictionary<int, bool>((await Task.WhenAll(tasks)).ToDictionary(x => x.Port, x => x.Open));
    }

    /// <summary>True when a VPN/proxy on this PC answers TCP connections itself (TUN mode).</summary>
    public static async Task<bool> LocalProxyInterceptsAsync(CancellationToken ct)
    {
        // 192.0.2.1 is a reserved documentation address: nothing real can ever accept this connection.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(2));
        try
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Parse("192.0.2.1"), 443, timeout.Token);
            return true;
        }
        catch when (!ct.IsCancellationRequested) { return false; }
    }

    /// <summary>
    /// Opens the panel URL while forcing the TCP connection to <paramref name="ip"/>, so the test works
    /// even before DNS changes reach this PC. The certificate is validated normally.
    /// </summary>
    public static async Task<int> CheckPanelAsync(Uri url, string ip, CancellationToken ct)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(15),
            ConnectCallback = async (ctx, token) =>
            {
                var s = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await s.ConnectAsync(IPAddress.Parse(ip), ctx.DnsEndPoint.Port, token);
                    return new NetworkStream(s, ownsSocket: true);
                }
                catch { s.Dispose(); throw; }
            },
        };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        using var res = await http.GetAsync(url, ct);
        return (int)res.StatusCode;
    }
}
