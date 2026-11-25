using Microsoft.Data.Sqlite;

namespace MaizyVpsSetup;

public sealed record InboundInfo(string Remark, int Port, string Protocol, bool Enabled);

/// <summary>Reads a 3x-ui (x-ui.db) backup and prepares a patched copy for upload.</summary>
public sealed class BackupInfo
{
    public required string SourcePath { get; init; }
    public Dictionary<string, string> Settings { get; } = new(StringComparer.Ordinal);
    public List<InboundInfo> Inbounds { get; } = new();
    public List<string> AdminUsers { get; } = new();

    public string Get(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var v) ? v : fallback;

    public int WebPort => int.TryParse(Get("webPort"), out var p) ? p : 2053;
    public string WebBasePath => NormalizeBasePath(Get("webBasePath", "/"));
    public string WebCertFile => Get("webCertFile");
    public string WebKeyFile => Get("webKeyFile");
    public bool SubEnable => Get("subEnable").Equals("true", StringComparison.OrdinalIgnoreCase);
    public int SubPort => int.TryParse(Get("subPort"), out var p) ? p : 2096;

    public static string NormalizeBasePath(string? path)
    {
        var p = (path ?? "").Trim().Trim('/');
        return p.Length == 0 ? "/" : "/" + p + "/";
    }

    public static BackupInfo Load(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("Backup file not found.", path);

        var header = new byte[16];
        using (var fs = File.OpenRead(path))
        {
            if (fs.Read(header, 0, 16) != 16 || System.Text.Encoding.ASCII.GetString(header, 0, 15) != "SQLite format 3")
                throw new InvalidDataException("This file is not a SQLite database (x-ui.db backup).");
        }

        // Work on a temp copy so the original backup is never touched (it may be in WAL mode).
        var work = CopyToTemp(path);
        try
        {
            var info = new BackupInfo { SourcePath = path };
            using var con = Open(work);

            var check = Scalar(con, "PRAGMA quick_check;");
            if (!string.Equals(check, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The database failed the integrity check: " + check);

            if (!TableExists(con, "settings") || !TableExists(con, "inbounds"))
                throw new InvalidDataException("This database does not look like a 3x-ui backup (no settings/inbounds tables).");

            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT key, value FROM settings";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    info.Settings[r.GetString(0)] = r.IsDBNull(1) ? "" : r.GetString(1);
            }
            using (var cmd = con.CreateCommand())
            {
                cmd.CommandText = "SELECT remark, port, protocol, enable FROM inbounds ORDER BY id";
                using var r = cmd.ExecuteReader();
                while (r.Read())
                    info.Inbounds.Add(new InboundInfo(
                        r.IsDBNull(0) ? "" : r.GetString(0),
                        r.IsDBNull(1) ? 0 : r.GetInt32(1),
                        r.IsDBNull(2) ? "" : r.GetString(2),
                        !r.IsDBNull(3) && r.GetValue(3) switch { long l => l != 0, string s => s is "1" or "true", bool b => b, _ => true }));
            }
            if (TableExists(con, "users"))
            {
                using var cmd = con.CreateCommand();
                cmd.CommandText = "SELECT username FROM users ORDER BY id";
                using var r = cmd.ExecuteReader();
                while (r.Read()) if (!r.IsDBNull(0)) info.AdminUsers.Add(r.GetString(0));
            }
            return info;
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            TryDelete(work);
        }
    }

    /// <summary>Ports that must be reachable from the internet.</summary>
    public SortedSet<int> RequiredPorts(int panelPort)
    {
        var ports = new SortedSet<int> { 80, panelPort };
        if (SubEnable) ports.Add(SubPort);
        foreach (var ib in Inbounds)
            if (ib.Enabled && ib.Port > 0) ports.Add(ib.Port);
        return ports;
    }

    public List<string> Warnings(int panelPort)
    {
        var list = new List<string>();
        var enabled = Inbounds.Where(i => i.Enabled && i.Port > 0).ToList();
        if (SubEnable)
            foreach (var ib in enabled.Where(i => i.Port == SubPort))
                list.Add($"Port {SubPort} is used by both the subscription server and inbound \"{ib.Remark}\" - one of them will fail to start.");
        foreach (var ib in enabled.Where(i => i.Port == panelPort))
            list.Add($"Port {panelPort} is used by both the panel and inbound \"{ib.Remark}\".");
        foreach (var g in enabled.GroupBy(i => i.Port).Where(g => g.Count() > 1))
            list.Add($"Port {g.Key} is used by more than one inbound: {string.Join(", ", g.Select(i => i.Remark))}.");
        return list;
    }

    /// <summary>
    /// Where the SSL certificate must be installed so the restored panel and inbounds find it.
    /// Keeps the backup's own paths when they already belong to <paramref name="domain"/>.
    /// </summary>
    public (string cert, string key) CertPathsFor(string domain)
    {
        var cert = WebCertFile;
        var key = WebKeyFile;
        if (cert.Length > 0 && key.Length > 0 && DirName(cert).EndsWith("/" + domain, StringComparison.OrdinalIgnoreCase))
            return (cert, key);
        return ($"/root/cert/{domain}/fullchain.pem", $"/root/cert/{domain}/privkey.pem");
    }

    /// <summary>
    /// Makes a temp copy of the backup with the panel port, base path and certificate paths set
    /// for the new server. Returns the temp file path; the caller deletes it.
    /// </summary>
    public string PrepareUploadCopy(string domain, int panelPort, string basePath, bool useSsl, List<string> changes)
    {
        var work = CopyToTemp(SourcePath);
        try
        {
            using var con = Open(work);
            basePath = NormalizeBasePath(basePath);

            if (panelPort != WebPort)
            {
                SetSetting(con, "webPort", panelPort.ToString());
                changes.Add($"Panel port {WebPort} -> {panelPort}");
            }
            if (basePath != WebBasePath)
            {
                SetSetting(con, "webBasePath", basePath);
                changes.Add($"Panel path {WebBasePath} -> {basePath}");
            }

            if (useSsl)
            {
                var (cert, key) = CertPathsFor(domain);
                var oldDir = WebCertFile.Length > 0 ? DirName(WebCertFile) : "";
                var newDir = DirName(cert);
                if (oldDir.Length > 0 && oldDir != newDir)
                {
                    // Move every reference (panel, subscription and inbound TLS settings) to the new folder.
                    Exec(con, "UPDATE settings SET value = replace(value, $o, $n) WHERE instr(value, $o) > 0",
                        ("$o", oldDir + "/"), ("$n", newDir + "/"));
                    Exec(con, "UPDATE inbounds SET stream_settings = replace(stream_settings, $o, $n) WHERE instr(stream_settings, $o) > 0",
                        ("$o", oldDir + "/"), ("$n", newDir + "/"));
                    changes.Add($"Certificate folder {oldDir} -> {newDir}");
                }
                if (WebCertFile.Length == 0 || WebKeyFile.Length == 0)
                {
                    SetSetting(con, "webCertFile", cert);
                    SetSetting(con, "webKeyFile", key);
                    changes.Add("Panel HTTPS enabled with the new certificate");
                }
            }

            // Fold everything into the main file so it can be uploaded on its own.
            Exec(con, "PRAGMA wal_checkpoint(TRUNCATE);");
            Exec(con, "PRAGMA journal_mode=DELETE;");
            con.Close();
            SqliteConnection.ClearAllPools();
            return work;
        }
        catch
        {
            SqliteConnection.ClearAllPools();
            TryDelete(work);
            throw;
        }
    }

    // ---- helpers ---------------------------------------------------------

    private static string DirName(string unixPath)
    {
        var i = unixPath.TrimEnd('/').LastIndexOf('/');
        return i <= 0 ? "/" : unixPath[..i];
    }

    private static string CopyToTemp(string path)
    {
        var dest = Path.Combine(Path.GetTempPath(), $"maizy-{Guid.NewGuid():N}.db");
        File.Copy(path, dest, overwrite: true);
        // Bring along WAL/SHM side files if the backup has them.
        foreach (var ext in new[] { "-wal", "-shm" })
            if (File.Exists(path + ext)) File.Copy(path + ext, dest + ext, true);
        return dest;
    }

    public static void TryDelete(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        foreach (var p in new[] { path, path + "-wal", path + "-shm", path + "-journal" })
            try { if (File.Exists(p)) File.Delete(p); } catch { /* temp file, ignore */ }
    }

    private static SqliteConnection Open(string path)
    {
        var con = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        con.Open();
        return con;
    }

    private static bool TableExists(SqliteConnection con, string name)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name=$n";
        cmd.Parameters.AddWithValue("$n", name);
        return Convert.ToInt64(cmd.ExecuteScalar()) > 0;
    }

    private static string Scalar(SqliteConnection con, string sql)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar()) ?? "";
    }

    private static void Exec(SqliteConnection con, string sql, params (string name, object value)[] args)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v);
        cmd.ExecuteNonQuery();
    }

    private static void SetSetting(SqliteConnection con, string key, string value)
    {
        using var cmd = con.CreateCommand();
        cmd.CommandText = "UPDATE settings SET value=$v WHERE key=$k";
        cmd.Parameters.AddWithValue("$k", key);
        cmd.Parameters.AddWithValue("$v", value);
        if (cmd.ExecuteNonQuery() == 0)
        {
            cmd.CommandText = "INSERT INTO settings(key, value) VALUES($k, $v)";
            cmd.ExecuteNonQuery();
        }
    }
}
