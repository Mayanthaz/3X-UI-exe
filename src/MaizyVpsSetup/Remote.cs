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

    public static string BuildScript(IReadOnlyDictionary<string, string> vars)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("MaizyVpsSetup.remote-setup.sh")
            ?? throw new InvalidOperationException("Embedded script missing.");
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
    /// Uploads the backup (optional) and the setup script, runs it as root and streams its output.
    /// Returns the script exit code.
    /// </summary>
    public static int RunSetup(SshTarget t, string? localDb, Dictionary<string, string> vars,
        Action<string> log, CancellationToken ct)
    {
        log("==> Connecting to " + t.Host + ":" + t.Port);
        var ci = t.ToConnectionInfo();
        using var ssh = Connect(new SshClient(ci), log);
        using var sftp = Connect(new SftpClient(ci), null);
        log("[OK] Connected as " + t.User);

        var home = sftp.WorkingDirectory.TrimEnd('/');
        var remoteDb = home + "/maizy-restore.db";
        var remoteScript = home + "/maizy-setup.sh";
        vars["DB_SRC"] = remoteDb;

        try
        {
            if (localDb is not null)
            {
                ct.ThrowIfCancellationRequested();
                var size = new FileInfo(localDb).Length;
                log($"==> Uploading database backup ({size / 1024.0:0} KB)");
                using var fs = File.OpenRead(localDb);
                var lastPct = -1;
                sftp.UploadFile(fs, remoteDb, true, sent =>
                {
                    var pct = size == 0 ? 100 : (int)(sent * 100 / (ulong)size);
                    if (pct / 25 != lastPct / 25) { lastPct = pct; log($"     {pct}%"); }
                });
                log("[OK] Backup uploaded to " + remoteDb);
            }

            var script = BuildScript(vars);
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
            using var cmd = ssh.CreateCommand(command);
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
            return cmd.ExitStatus ?? -1;
        }
        finally
        {
            // The script holds the Cloudflare token: never leave it behind.
            try
            {
                if (ssh.IsConnected)
                {
                    using var c = ssh.RunCommand($"rm -f {Quote(remoteScript)} {Quote(remoteDb)}");
                }
            }
            catch { /* best effort */ }
        }
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
