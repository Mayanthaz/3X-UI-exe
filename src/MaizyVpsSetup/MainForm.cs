using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace MaizyVpsSetup;

public sealed class MainForm : Form
{
    private static readonly Color Accent = Color.FromArgb(37, 99, 235);
    private static readonly Color LogBack = Color.FromArgb(24, 26, 31);
    private static readonly Color LogText = Color.FromArgb(214, 218, 224);

    private readonly AppSettings _settings = AppSettings.Load();
    private BackupInfo? _backup;
    private CancellationTokenSource? _cts;
    private StreamWriter? _logFile;

    // Server
    private readonly TextBox _ip = new() { PlaceholderText = "e.g. 172.104.173.58" };
    private readonly NumericUpDown _sshPort = new() { Minimum = 1, Maximum = 65535, Value = 22, Width = 80 };
    private readonly TextBox _user = new() { Text = "root" };
    private readonly RadioButton _authPassword = new() { Text = "Password", AutoSize = true, Checked = true };
    private readonly RadioButton _authKey = new() { Text = "Private key file", AutoSize = true };
    private readonly TextBox _password = new() { UseSystemPasswordChar = true };
    private readonly TextBox _keyPath = new() { PlaceholderText = "id_ed25519 / id_rsa (OpenSSH or PuTTY .ppk not supported)" };
    private readonly TextBox _keyPass = new() { UseSystemPasswordChar = true, PlaceholderText = "only if the key has one" };
    private RowHandle _passwordRow = null!, _keyRow = null!, _keyPassRow = null!;

    // Backup
    private readonly TextBox _backupPath = new();
    private readonly Label _backupInfo = new() { AutoSize = true, MaximumSize = new Size(520, 0), ForeColor = SystemColors.GrayText, Text = "No backup selected." };

    // Panel / SSL
    private readonly TextBox _panelUrl = new() { Text = AppSettings.DefaultPanelUrl };
    private readonly TextBox _email = new() { PlaceholderText = "optional - for Let's Encrypt notices" };
    private readonly CheckBox _cfUpdate = new() { Text = "Point the domain to the new IP in Cloudflare", AutoSize = true };
    private readonly TextBox _cfToken = new() { UseSystemPasswordChar = true, PlaceholderText = "Cloudflare API token (Zone.DNS: Edit)" };
    private RowHandle _cfRow = null!;

    // Steps
    private readonly CheckBox _doInstall = new() { Text = "Install / update 3x-ui", AutoSize = true, Checked = true };
    private readonly CheckBox _doRestore = new() { Text = "Restore the backup", AutoSize = true, Checked = true };
    private readonly CheckBox _doSsl = new() { Text = "Get SSL certificate", AutoSize = true, Checked = true };
    private readonly CheckBox _doFirewall = new() { Text = "Open firewall ports", AutoSize = true, Checked = true };
    private readonly CheckBox _remember = new() { Text = "Remember password and token on this PC (encrypted)", AutoSize = true };

    // Actions
    private readonly Button _btnTest = new() { Text = "Test connection", AutoSize = true };
    private readonly Button _btnDns = new() { Text = "Check DNS", AutoSize = true };
    private readonly Button _btnStart = new() { Text = "Start setup", AutoSize = true };
    private readonly Button _btnCancel = new() { Text = "Cancel", AutoSize = true, Enabled = false };
    private readonly Button _btnOpen = new() { Text = "Open panel", AutoSize = true };

    private readonly RichTextBox _log = new()
    {
        Dock = DockStyle.Fill, ReadOnly = true, BorderStyle = BorderStyle.None, BackColor = LogBack,
        ForeColor = LogText, Font = new Font("Consolas", 9.5f), WordWrap = true, DetectUrls = true,
    };
    private readonly ToolStripStatusLabel _status = new() { Text = "Ready", Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripProgressBar _progress = new() { Style = ProgressBarStyle.Marquee, Visible = false, Width = 160 };

    public MainForm()
    {
        Text = "Maizy VPS Setup - move 3x-ui to a new server";
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        Icon = SystemIcons.Shield;

        BuildLayout();
        LoadSettings();
        WireEvents();
        UpdateAuthRows();
        _cfRow.Enabled = _cfUpdate.Checked;

        Size = new Size(LogicalToDeviceUnits(1240), LogicalToDeviceUnits(820));
        MinimumSize = new Size(LogicalToDeviceUnits(1000), LogicalToDeviceUnits(640));

        Shown += (_, _) =>
        {
            Log("How to use", LogKind.Step);
            Log("1. Enter the new server's IP and root login, then Test connection.");
            Log("2. Pick the x-ui .db backup (the newest one in the Backup folder is chosen for you).");
            Log("3. Check the panel address, then press Start setup.");
            Log("Tip: add a Cloudflare API token and the app also points the domain at the new IP.");
            if (_backup is not null) Log("[OK] Backup found: " + _backup.SourcePath);
            _status.Text = "Ready";
        };
    }

    // ------------------------------------------------------------------ layout

    private void BuildLayout()
    {
        var split = new SplitContainer
        {
            Dock = DockStyle.Fill, FixedPanel = FixedPanel.Panel1, SplitterWidth = 6,
        };
        Controls.Add(split);
        Shown += (_, _) => split.SplitterDistance = LogicalToDeviceUnits(600);

        var status = new StatusStrip();
        status.Items.AddRange(new ToolStripItem[] { _status, _progress });
        Controls.Add(status);

        // Header
        var header = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(64), BackColor = Accent };
        header.Controls.Add(new Label
        {
            Text = "Move your 3x-ui panel to a new VPS\nInstall  >  restore backup  >  DNS  >  SSL  >  firewall  >  verify",
            ForeColor = Color.White, Dock = DockStyle.Fill, Padding = new Padding(14, 10, 0, 0),
            Font = new Font("Segoe UI", 10f),
        });
        Controls.Add(header);
        header.SendToBack();
        status.SendToBack();

        // Left: settings
        // Dock=Top stack: each section added goes below the previous one (BringToFront).
        var left = new Panel { Dock = DockStyle.Fill, AutoScroll = true, Padding = new Padding(10, 8, 14, 8) };
        split.Panel1.Controls.Add(left);
        void Stack(Control c)
        {
            c.Dock = DockStyle.Top; left.Controls.Add(c); c.BringToFront();
            var gap = new Panel { Dock = DockStyle.Top, Height = LogicalToDeviceUnits(8) };
            left.Controls.Add(gap); gap.BringToFront();
        }

        var g1 = Group("1  New server (SSH)", out var t1);
        AddRow(t1, "Server IP", _ip);
        AddRow(t1, "SSH port", _sshPort);
        AddRow(t1, "Username", _user);
        AddRow(t1, "Login with", Flow(_authPassword, _authKey));
        _passwordRow = AddRow(t1, "Password", _password);
        _keyRow = AddRow(t1, "Key file", WithBrowse(_keyPath, BrowseKey));
        _keyPassRow = AddRow(t1, "Key passphrase", _keyPass);
        AddRow(t1, "", Flow(_btnTest));
        Stack(g1);

        var g2 = Group("2  Database backup", out var t2);
        AddRow(t2, "Backup file", WithBrowse(_backupPath, BrowseBackup));
        AddRow(t2, "", _backupInfo);
        Stack(g2);

        var g3 = Group("3  Panel address and SSL", out var t3);
        AddRow(t3, "Panel URL", _panelUrl);
        AddRow(t3, "Email", _email);
        AddRow(t3, "", _cfUpdate);
        _cfRow = AddRow(t3, "CF API token", _cfToken);
        AddRow(t3, "", new Label
        {
            AutoSize = true, MaximumSize = new Size(LogicalToDeviceUnits(410), 0), ForeColor = SystemColors.GrayText,
            Text = "Cloudflare dashboard > My Profile > API Tokens > \"Edit zone DNS\" template. " +
                   "With a token the certificate is checked over DNS, so port 80 does not need to be free. " +
                   "Without one, update the A record yourself before starting.",
        });
        AddRow(t3, "", Flow(_btnDns));
        _backupInfo.MaximumSize = new Size(LogicalToDeviceUnits(410), 0);
        Stack(g3);

        var g4 = Group("4  What to do", out var t4);
        AddRow(t4, "", Flow(_doInstall, _doRestore));
        AddRow(t4, "", Flow(_doSsl, _doFirewall));
        AddRow(t4, "", _remember);
        Stack(g4);

        _btnStart.BackColor = Accent;
        _btnStart.ForeColor = Color.White;
        _btnStart.FlatStyle = FlatStyle.Flat;
        _btnStart.FlatAppearance.BorderSize = 0;
        _btnStart.Font = new Font(Font.FontFamily, 10.5f, FontStyle.Bold);
        _btnStart.Padding = new Padding(18, 6, 18, 6);
        // Action buttons stay pinned under the scrolling settings.
        var actions = Flow(_btnStart, _btnCancel, _btnOpen);
        actions.Dock = DockStyle.Bottom;
        actions.Padding = new Padding(LogicalToDeviceUnits(10), LogicalToDeviceUnits(8), 0, LogicalToDeviceUnits(8));
        split.Panel1.Controls.Add(actions);
        left.BringToFront();

        // Right: log
        var logHost = new Panel { Dock = DockStyle.Fill, BackColor = LogBack, Padding = new Padding(10) };
        logHost.Controls.Add(_log);
        split.Panel2.Controls.Add(logHost);
        _log.LinkClicked += (_, e) => OpenUrl(e.LinkText ?? "");

        AcceptButton = _btnStart;
    }

    private GroupBox Group(string title, out TableLayoutPanel table)
    {
        var g = new GroupBox
        {
            Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(10, 6, 10, 8), Margin = new Padding(0, 0, 0, 10),
            Font = new Font(Font, FontStyle.Bold),
        };
        table = new TableLayoutPanel
        {
            Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2,
            Font = new Font(Font, FontStyle.Regular),
        };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, LogicalToDeviceUnits(110)));
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        g.Controls.Add(table);
        return g;
    }

    private static RowHandle AddRow(TableLayoutPanel t, string label, Control control)
    {
        var row = t.RowCount++;
        t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var lbl = new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 6, 6) };
        if (control is TextBox or Panel) control.Anchor = AnchorStyles.Left | AnchorStyles.Right;
        control.Margin = new Padding(0, 3, 0, 3);
        t.Controls.Add(lbl, 0, row);
        t.Controls.Add(control, 1, row);
        // Return a proxy that hides/enables both cells together.
        return new RowHandle(lbl, control);
    }

    /// <summary>Shows/hides or enables/disables a label + control row together.</summary>
    private sealed class RowHandle(Control label, Control control)
    {
        public bool Visible { set { label.Visible = value; control.Visible = value; } }
        public bool Enabled { set { label.Enabled = value; control.Enabled = value; } }
    }

    private static FlowLayoutPanel Flow(params Control[] controls)
    {
        var f = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = Padding.Empty };
        foreach (var c in controls) { c.Margin = new Padding(0, 2, 12, 2); f.Controls.Add(c); }
        return f;
    }

    private static Panel WithBrowse(TextBox box, Action onBrowse)
    {
        var btn = new Button { Text = "...", Width = 36, Dock = DockStyle.Right };
        btn.Click += (_, _) => onBrowse();
        box.Dock = DockStyle.Fill;
        var p = new Panel { Height = box.PreferredHeight + 2 };
        p.Controls.Add(box);
        p.Controls.Add(btn);
        return p;
    }

    // ------------------------------------------------------------------ settings

    private void LoadSettings()
    {
        var s = _settings;
        _ip.Text = s.ServerIp;
        _sshPort.Value = Math.Clamp(s.SshPort, 1, 65535);
        _user.Text = string.IsNullOrWhiteSpace(s.SshUser) ? "root" : s.SshUser;
        _authKey.Checked = s.UseKey;
        _authPassword.Checked = !s.UseKey;
        _keyPath.Text = s.KeyPath;
        _panelUrl.Text = string.IsNullOrWhiteSpace(s.PanelUrl) ? AppSettings.DefaultPanelUrl : s.PanelUrl;
        _email.Text = s.AcmeEmail;
        _cfUpdate.Checked = s.UpdateCloudflare;
        _remember.Checked = s.RememberSecrets;
        if (s.RememberSecrets)
        {
            _password.Text = AppSettings.Unprotect(s.PasswordProtected);
            _cfToken.Text = AppSettings.Unprotect(s.CfTokenProtected);
        }

        var backup = File.Exists(s.BackupPath) ? s.BackupPath : FindNewestBackup();
        if (backup is not null) SetBackup(backup, quiet: true);
    }

    private void SaveSettings()
    {
        var s = _settings;
        s.ServerIp = _ip.Text.Trim();
        s.SshPort = (int)_sshPort.Value;
        s.SshUser = _user.Text.Trim();
        s.UseKey = _authKey.Checked;
        s.KeyPath = _keyPath.Text.Trim();
        s.BackupPath = _backupPath.Text.Trim();
        s.PanelUrl = _panelUrl.Text.Trim();
        s.AcmeEmail = _email.Text.Trim();
        s.UpdateCloudflare = _cfUpdate.Checked;
        s.RememberSecrets = _remember.Checked;
        s.PasswordProtected = _remember.Checked ? AppSettings.Protect(_password.Text) : null;
        s.CfTokenProtected = _remember.Checked ? AppSettings.Protect(_cfToken.Text) : null;
        s.Save();
    }

    private static string? FindNewestBackup()
    {
        var dirs = new List<string>();
        var d = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && d is not null; i++, d = d.Parent)
        {
            dirs.Add(Path.Combine(d.FullName, "Backup"));
            dirs.Add(d.FullName);
        }
        return dirs.Where(Directory.Exists)
            .SelectMany(dir => { try { return Directory.GetFiles(dir, "*.db"); } catch { return Array.Empty<string>(); } })
            .Select(f => new FileInfo(f))
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    // ------------------------------------------------------------------ events

    private void WireEvents()
    {
        _authPassword.CheckedChanged += (_, _) => UpdateAuthRows();
        _authKey.CheckedChanged += (_, _) => UpdateAuthRows();
        _cfUpdate.CheckedChanged += (_, _) => _cfRow.Enabled = _cfUpdate.Checked;
        _panelUrl.Leave += (_, _) => RefreshBackupSummary();
        _backupPath.Leave += (_, _) => { if (_backupPath.Text.Trim() != _backup?.SourcePath) SetBackup(_backupPath.Text.Trim()); };
        _btnTest.Click += async (_, _) => await TestConnectionAsync();
        _btnDns.Click += async (_, _) => await CheckDnsAsync(interactive: true);
        _btnStart.Click += async (_, _) => await RunSetupAsync();
        _btnCancel.Click += (_, _) => { _cts?.Cancel(); Log("Cancelling...", LogKind.Warn); };
        _btnOpen.Click += (_, _) => { if (TryParsePanelUrl(out var u, out _)) OpenUrl(u!.ToString()); };
        FormClosing += (_, e) =>
        {
            if (_cts is not null && MessageBox.Show(this, "Setup is still running. Stop it and close?", Text,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.No)
            { e.Cancel = true; return; }
            _cts?.Cancel();
            SaveSettings();
        };

        AllowDrop = true;
        DragEnter += (_, e) => e.Effect = e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
        DragDrop += (_, e) =>
        {
            if (e.Data?.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } files) SetBackup(files[0]);
        };
    }

    private void UpdateAuthRows()
    {
        _passwordRow.Visible = _authPassword.Checked;
        _keyRow.Visible = _authKey.Checked;
        _keyPassRow.Visible = _authKey.Checked;
    }

    private void BrowseKey()
    {
        using var dlg = new OpenFileDialog { Title = "Choose your SSH private key", Filter = "All files|*.*" };
        var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
        if (Directory.Exists(sshDir)) dlg.InitialDirectory = sshDir;
        if (dlg.ShowDialog(this) == DialogResult.OK) _keyPath.Text = dlg.FileName;
    }

    private void BrowseBackup()
    {
        using var dlg = new OpenFileDialog { Title = "Choose the 3x-ui database backup", Filter = "x-ui database (*.db)|*.db|All files|*.*" };
        if (File.Exists(_backupPath.Text)) dlg.InitialDirectory = Path.GetDirectoryName(_backupPath.Text);
        if (dlg.ShowDialog(this) == DialogResult.OK) SetBackup(dlg.FileName);
    }

    private void SetBackup(string path, bool quiet = false)
    {
        _backupPath.Text = path;
        _backup = null;
        if (string.IsNullOrWhiteSpace(path)) { _backupInfo.Text = "No backup selected."; return; }
        try
        {
            _backup = BackupInfo.Load(path);
            RefreshBackupSummary();
            if (!quiet) Log("[OK] Backup loaded: " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            _backupInfo.ForeColor = Color.Firebrick;
            _backupInfo.Text = ex.Message;
            if (!quiet) Log("[ERROR] " + ex.Message);
        }
    }

    private void RefreshBackupSummary()
    {
        if (_backup is null) return;
        var b = _backup;
        var sb = new StringBuilder();
        // 3x-ui backups are named <old-ip>_<yyyy-MM-dd>_<HHmmss>.db
        var name = Regex.Match(Path.GetFileName(b.SourcePath), @"^(\d{1,3}(?:\.\d{1,3}){3})(?:_(\d{4}-\d{2}-\d{2})_(\d{2})(\d{2}))?");
        if (name.Success)
            sb.AppendLine($"Old server: {name.Groups[1].Value}" +
                          (name.Groups[2].Success ? $"   (backup taken {name.Groups[2].Value} {name.Groups[3].Value}:{name.Groups[4].Value})" : ""));
        sb.AppendLine($"Panel login: {string.Join(", ", b.AdminUsers)}   (your old password still works)");
        sb.AppendLine($"Panel in backup: port {b.WebPort}, path {b.WebBasePath}, " +
                      (b.WebCertFile.Length > 0 ? "HTTPS" : "no HTTPS"));
        if (b.WebCertFile.Length > 0) sb.AppendLine($"Certificate: {b.WebCertFile}");
        sb.AppendLine($"Inbounds ({b.Inbounds.Count}): " + string.Join(", ",
            b.Inbounds.Select(i => $"{i.Remark} :{i.Port}{(i.Enabled ? "" : " (off)")}")));
        if (b.SubEnable) sb.AppendLine($"Subscription port: {b.SubPort}");
        var panelPort = TryParsePanelUrl(out var u, out _) ? u!.Port : b.WebPort;
        foreach (var w in b.Warnings(panelPort)) sb.AppendLine("! " + w);
        _backupInfo.ForeColor = SystemColors.ControlText;
        _backupInfo.Text = sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ actions

    private bool TryParsePanelUrl(out Uri? uri, out string error)
    {
        error = "";
        var text = _panelUrl.Text.Trim();
        if (!text.Contains("://")) text = "https://" + text;
        if (!Uri.TryCreate(text, UriKind.Absolute, out uri) || uri.Scheme != Uri.UriSchemeHttps || uri.HostNameType != UriHostNameType.Dns)
        {
            error = "Panel URL must look like https://your.domain:port/";
            uri = null;
            return false;
        }
        return true;
    }

    private bool TryGetTarget(out SshTarget? target)
    {
        target = null;
        var ip = _ip.Text.Trim();
        if (!IPAddress.TryParse(ip, out var addr) || addr.AddressFamily != AddressFamily.InterNetwork)
        {
            Warn("Enter the new server's IPv4 address, for example 172.104.173.58.");
            _ip.Focus();
            return false;
        }
        if (string.IsNullOrWhiteSpace(_user.Text)) { Warn("Enter the SSH username (usually root)."); return false; }
        if (_authPassword.Checked && _password.Text.Length == 0) { Warn("Enter the SSH password."); _password.Focus(); return false; }
        if (_authKey.Checked && !File.Exists(_keyPath.Text.Trim())) { Warn("Choose a valid private key file."); return false; }

        target = new SshTarget
        {
            Host = ip, Port = (int)_sshPort.Value, User = _user.Text.Trim(), UseKey = _authKey.Checked,
            Password = _password.Text, KeyPath = _keyPath.Text.Trim(), KeyPassphrase = _keyPass.Text,
        };
        return true;
    }

    private async Task TestConnectionAsync()
    {
        if (!TryGetTarget(out var t)) return;
        SetBusy(true, "Testing SSH connection...");
        try
        {
            Log($"==> Testing SSH {t!.User}@{t.Host}:{t.Port}", LogKind.Step);
            var info = await Task.Run(() => Remote.Test(t));
            Log("[OK] " + info);
            if (!t.IsRoot) Log("[WARN] Not logged in as root - the app will use sudo.");
        }
        catch (Exception ex) { Log("[ERROR] " + Friendly(ex)); }
        finally { SetBusy(false); }
    }

    /// <summary>Returns true when the domain already resolves to the server IP.</summary>
    private async Task<bool> CheckDnsAsync(bool interactive)
    {
        if (!TryParsePanelUrl(out var uri, out var err)) { Warn(err); return false; }
        var ip = _ip.Text.Trim();
        if (interactive) SetBusy(true, "Checking DNS...");
        try
        {
            var ips = await DnsTools.ResolveARecordsAsync(uri!.Host);
            var list = ips.Count == 0 ? "nothing" : string.Join(", ", ips);
            if (ip.Length > 0 && ips.Contains(ip))
            {
                Log($"[OK] {uri.Host} points to {list}");
                return true;
            }
            Log($"[WARN] {uri.Host} points to {list}" + (ip.Length > 0 ? $", not the new server {ip}." : "."));
            return false;
        }
        catch (Exception ex) { Log("[ERROR] DNS lookup failed: " + Friendly(ex)); return false; }
        finally { if (interactive) SetBusy(false); }
    }

    private async Task RunSetupAsync()
    {
        if (!TryGetTarget(out var target)) return;
        if (!TryParsePanelUrl(out var uri, out var err)) { Warn(err); return; }
        if (!(_doInstall.Checked || _doRestore.Checked || _doSsl.Checked || _doFirewall.Checked))
        { Warn("Tick at least one step under \"What to do\"."); return; }
        if (_doRestore.Checked && _backup is null) { Warn("Choose a valid backup file first."); return; }
        var cfToken = _cfUpdate.Checked ? _cfToken.Text.Trim() : "";
        if (_cfUpdate.Checked && cfToken.Length == 0) { Warn("Enter the Cloudflare API token, or untick the Cloudflare option."); return; }

        var domain = uri!.Host.ToLowerInvariant();
        var panelPort = uri.Port;
        var basePath = BackupInfo.NormalizeBasePath(uri.AbsolutePath);

        var confirm = new StringBuilder();
        confirm.AppendLine($"Server: {target!.User}@{target.Host}");
        confirm.AppendLine($"Panel: https://{domain}:{panelPort}{basePath}");
        confirm.AppendLine();
        if (_doInstall.Checked) confirm.AppendLine("- Install / update 3x-ui");
        if (_doRestore.Checked) confirm.AppendLine("- Replace the server's 3x-ui database with " + Path.GetFileName(_backup!.SourcePath));
        if (_cfUpdate.Checked) confirm.AppendLine($"- Point {domain} to {target.Host} in Cloudflare");
        if (_doSsl.Checked) confirm.AppendLine($"- Get a Let's Encrypt certificate for {domain}");
        if (_doFirewall.Checked) confirm.AppendLine("- Open the panel and inbound ports in the firewall");
        confirm.AppendLine();
        confirm.Append("Continue?");
        if (MessageBox.Show(this, confirm.ToString(), "Start setup", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK)
            return;

        SaveSettings();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        string? uploadCopy = null;
        StartLogFile();
        SetBusy(true, "Running setup...");
        var sw = Stopwatch.StartNew();
        try
        {
            // 1. Cloudflare DNS
            if (_cfUpdate.Checked)
            {
                Log("==> Updating Cloudflare DNS", LogKind.Step);
                Log("[OK] " + await DnsTools.UpdateCloudflareAsync(cfToken, domain, target.Host, ct));
            }

            // 2. DNS sanity check when the cert is validated over HTTP
            if (_doSsl.Checked && cfToken.Length == 0)
            {
                Log("==> Checking DNS", LogKind.Step);
                if (!await CheckDnsAsync(interactive: false))
                {
                    var answer = MessageBox.Show(this,
                        $"{domain} does not point to {target.Host} yet, so Let's Encrypt will probably fail.\n\n" +
                        "Update the A record in Cloudflare (DNS only, grey cloud) and wait a minute, " +
                        "or tick \"Point the domain to the new IP in Cloudflare\".\n\nContinue anyway?",
                        "DNS not ready", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                    if (answer != DialogResult.Yes) throw new OperationCanceledException();
                }
            }

            // 3. Prepare the database copy
            var (certFile, keyFile) = _backup?.CertPathsFor(domain)
                ?? ($"/root/cert/{domain}/fullchain.pem", $"/root/cert/{domain}/privkey.pem");
            var ports = _backup?.RequiredPorts(panelPort) ?? new SortedSet<int> { 80, panelPort };
            if (_doRestore.Checked)
            {
                Log("==> Preparing backup", LogKind.Step);
                var changes = new List<string>();
                uploadCopy = _backup!.PrepareUploadCopy(domain, panelPort, basePath, _doSsl.Checked, changes);
                if (changes.Count == 0) Log("[OK] Backup already matches the panel address - uploading it as is");
                foreach (var c in changes) Log("[OK] " + c);
            }

            // 4. Run on the server
            var vars = new Dictionary<string, string>
            {
                ["DOMAIN"] = domain,
                ["CERT_FILE"] = certFile,
                ["KEY_FILE"] = keyFile,
                ["ACME_EMAIL"] = _email.Text.Trim(),
                ["CF_TOKEN"] = cfToken,
                ["DB_SRC"] = "",
                ["PANEL_PORT"] = panelPort.ToString(),
                ["PANEL_PATH"] = basePath,
                ["OPEN_PORTS"] = string.Join(' ', ports),
                ["SSH_PORT"] = target.Port.ToString(),
                ["DO_INSTALL"] = _doInstall.Checked ? "1" : "0",
                ["DO_RESTORE"] = _doRestore.Checked ? "1" : "0",
                ["DO_SSL"] = _doSsl.Checked ? "1" : "0",
                ["DO_FW"] = _doFirewall.Checked ? "1" : "0",
            };
            var exit = await Task.Run(() => Remote.RunSetup(target, uploadCopy, vars, line => Log(line), ct), ct);
            if (exit != 0) throw new InvalidOperationException($"The server script stopped with exit code {exit}. See the log above.");

            // 5. Check from this PC
            Log("==> Checking the panel from this PC", LogKind.Step);
            try
            {
                var code = await Remote.CheckPanelAsync(new Uri($"https://{domain}:{panelPort}{basePath}"), target.Host, ct);
                Log($"[OK] https://{domain}:{panelPort}{basePath} answers (HTTP {code}) with a valid certificate");
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Log("[WARN] Could not open the panel from this PC: " + Friendly(ex));
                Log("     If the server check passed, a firewall at your VPS provider may be blocking port " + panelPort + ".");
            }
            if (!await CheckDnsAsync(interactive: false))
                Log("     DNS changes can take a few minutes to reach every network.");

            Log("");
            Log($"[OK] Finished in {sw.Elapsed:mm\\:ss}.", LogKind.Ok);
            Log($"     Panel: https://{domain}:{panelPort}{basePath}");
            if (_backup is not null && _doRestore.Checked)
                Log($"     Login: {string.Join(", ", _backup.AdminUsers)} with your old password");
            Log($"     Ports in use: {string.Join(", ", ports)} (open these in your VPS provider's firewall too, if it has one)");
            _status.Text = "Done";
        }
        catch (OperationCanceledException) { Log("[WARN] Setup cancelled."); _status.Text = "Cancelled"; }
        catch (Exception ex) { Log("[ERROR] " + Friendly(ex)); _status.Text = "Failed - see the log"; }
        finally
        {
            BackupInfo.TryDelete(uploadCopy);
            _cts.Dispose();
            _cts = null;
            SetBusy(false, keepStatus: true);
            StopLogFile();
        }
    }

    // ------------------------------------------------------------------ helpers

    private void SetBusy(bool busy, string? status = null, bool keepStatus = false)
    {
        foreach (var b in new[] { _btnTest, _btnDns, _btnStart }) b.Enabled = !busy;
        _btnCancel.Enabled = busy && _cts is not null;
        _progress.Visible = busy;
        UseWaitCursor = busy;
        if (status is not null) _status.Text = status;
        else if (!busy && !keepStatus) _status.Text = "Ready";
    }

    private void Warn(string message) =>
        MessageBox.Show(this, message, Text, MessageBoxButtons.OK, MessageBoxIcon.Information);

    private static string Friendly(Exception ex)
    {
        var e = ex is AggregateException { InnerException: { } inner } ? inner : ex;
        return e switch
        {
            Renci.SshNet.Common.SshAuthenticationException => "SSH login failed - check the username and password/key.",
            SocketException se => $"Cannot reach the server ({se.SocketErrorCode}). Check the IP and SSH port.",
            Renci.SshNet.Common.SshOperationTimeoutException => "SSH connection timed out. Check the IP and SSH port.",
            HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException ae } =>
                "Certificate problem: " + ae.Message,
            _ => e.Message,
        };
    }

    private enum LogKind { Auto, Step, Ok, Warn, Error }
    private static readonly Regex Ansi = new(@"\x1B\[[0-9;?]*[A-Za-z]", RegexOptions.Compiled);

    private void Log(string line, LogKind kind = LogKind.Auto)
    {
        if (InvokeRequired) { BeginInvoke(() => Log(line, kind)); return; }
        line = Ansi.Replace(line, "").TrimEnd('\r');
        if (kind == LogKind.Auto)
        {
            var t = line.TrimStart();
            kind = t.StartsWith("==>") ? LogKind.Step
                : t.StartsWith("[OK]") ? LogKind.Ok
                : t.StartsWith("[WARN]") ? LogKind.Warn
                : t.StartsWith("[ERROR]") ? LogKind.Error
                : LogKind.Auto;
        }
        var (color, bold) = kind switch
        {
            LogKind.Step => (Color.FromArgb(125, 170, 255), true),
            LogKind.Ok => (Color.FromArgb(110, 210, 140), false),
            LogKind.Warn => (Color.FromArgb(240, 190, 90), false),
            LogKind.Error => (Color.FromArgb(255, 110, 110), true),
            _ => (LogText, false),
        };
        if (kind == LogKind.Step && _log.TextLength > 0) _log.AppendText("\n");
        _log.SelectionStart = _log.TextLength;
        _log.SelectionLength = 0;
        _log.SelectionColor = color;
        _log.SelectionFont = bold ? new Font(_log.Font, FontStyle.Bold) : _log.Font;
        _log.AppendText(line + "\n");
        _log.SelectionColor = _log.ForeColor;
        _log.ScrollToCaret();
        if (kind == LogKind.Step) _status.Text = line.TrimStart('=', '>', ' ');
        try { _logFile?.WriteLine(line); } catch { /* ignore */ }
    }

    private void StartLogFile()
    {
        try
        {
            var dir = Path.Combine(AppSettings.Folder, "logs");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"setup-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            _logFile = new StreamWriter(path, false, Encoding.UTF8) { AutoFlush = true };
            Log("Log file: " + path);
        }
        catch { _logFile = null; }
    }

    private void StopLogFile()
    {
        // Let queued BeginInvoke log lines land first.
        BeginInvoke(() => { _logFile?.Dispose(); _logFile = null; });
    }

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { /* ignore */ }
    }
}
