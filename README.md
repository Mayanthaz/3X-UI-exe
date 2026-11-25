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

Settings are remembered in `%AppData%\MaizyVpsSetup` (passwords only if you tick the option, encrypted with Windows DPAPI). Each run's log is saved in `%AppData%\MaizyVpsSetup\logs`.

## Build from source

Requires the .NET 10 SDK.

```
cd src/MaizyVpsSetup
dotnet publish -c Release -o ../../dist
```

## License

[MIT](LICENSE)
