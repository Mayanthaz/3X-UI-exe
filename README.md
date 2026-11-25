# 3X-UI exe

A Windows app that moves a [3x-ui](https://github.com/MHSanaei/3x-ui) panel to a new VPS in one click: install, restore the database backup, point DNS to the new IP, get an SSL certificate, open the firewall and check the result.

## Download

Get `MaizyVpsSetup.exe` from the [latest release](../../releases/latest). It is a single file and needs nothing else installed (Windows 10/11, 64-bit).

Windows SmartScreen may warn the first time because the exe is not code-signed: click **More info > Run anyway**.

## How to use

1. **New server** - enter the VPS IP, SSH port and root password (or a private key), then **Test connection**.
2. **Database backup** - pick the `x-ui.db` backup (for example the one the 3x-ui Telegram bot sends). A `Backup` folder next to the exe is searched automatically.
3. **Panel address** - defaults to `https://tunnel.inverses.dev:2020/`; change it to your own domain and port.
4. Optional: tick **Point the domain to the new IP in Cloudflare** and paste a Cloudflare API token ("Edit zone DNS" template).
5. Press **Start setup** and follow the log.

## What it does on the server

| Step | Details |
|---|---|
| Install / update 3x-ui | Runs the official installer non-interactively (latest release). |
| Restore backup | Uploads the backup to `/etc/x-ui/x-ui.db`; the previous database is kept as a copy. If you changed the domain, port or path, the backup is adjusted first. |
| Cloudflare DNS | Sets the A record to the new IP as **DNS only** (the orange-cloud proxy would break the panel and Xray TLS). |
| SSL certificate | Let's Encrypt via acme.sh, installed where the backup expects it (`/root/cert/<domain>/`). Uses Cloudflare DNS validation when a token is given, otherwise HTTP on port 80 (x-ui is paused for a few seconds during issue and renewal). Auto-renews. |
| Firewall | Opens the panel, subscription and inbound ports in ufw, firewalld or iptables. |
| Verify | Checks the panel answers over HTTPS with a trusted certificate, from the server and from your PC. |

## Common VPS problems it handles

| Problem | What the app does |
|---|---|
| Fresh VPS busy with automatic updates ("Could not get lock") | Waits for apt to finish, repairs dpkg |
| Server cannot resolve domains / reach GitHub | Adds public DNS servers (1.1.1.1, 8.8.8.8) |
| nginx / apache already using port 80 or 443 | Stops and disables them (option) |
| Wrong clock breaks TLS and VLESS | Turns on time sync, sets the timezone from your backup |
| Small VPS runs out of memory | Creates a 1 GB swap file when RAM < 2 GB |
| Slow speeds | Enables BBR and network tuning, raises the open-files limit |
| Domain behind the Cloudflare proxy (orange cloud) | Detects it; with a token switches it to DNS only |
| Old IPv6 (AAAA) record still pointing to the old server | Detects it; with a token removes it |
| Let's Encrypt rate limit | Explains it and falls back to ZeroSSL when an email is set |
| Port 80 unreachable / CAA record / bad Cloudflare token | Shows the exact reason instead of a generic failure |
| VPS provider firewall (Oracle, AWS, GCP, Azure...) blocking ports | Tests every port from your PC with a real TLS handshake |
| Oracle Cloud iptables rules | Opens the ports in iptables and saves them |
| Old server still running the same Telegram bot | Warns you to stop it |
| Low disk space, unsupported OS (CentOS 7) | Checks and warns before starting |

## Manage the server

The **Manage the server** buttons use the same SSH login:

- **Health check** - service, Xray, CPU/RAM/disk, ports, certificate expiry, BBR, clock, firewall, recent errors, plus DNS, port and HTTPS checks from your PC.
- **Restart panel**, **Panel logs**
- **Renew SSL** - forces a certificate renewal now.
- **Download backup** - saves the live database to the Backup folder as `<ip>_<date>_<time>.db`.

Settings are remembered in `%AppData%\MaizyVpsSetup` (passwords only if you tick the option, encrypted with Windows DPAPI). Each run's log is saved in `%AppData%\MaizyVpsSetup\logs`.

## Build from source

Requires the .NET 10 SDK.

```
cd src/MaizyVpsSetup
dotnet publish -c Release -o ../../dist
```

## License

[MIT](LICENSE)
