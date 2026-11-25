#!/usr/bin/env bash
# Maizy VPS Setup - server side.
# The Windows app writes the configuration variables above this line:
#   DOMAIN CERT_FILE KEY_FILE ACME_EMAIL CF_TOKEN DB_SRC PANEL_PORT PANEL_PATH
#   OPEN_PORTS SSH_PORT TIMEZONE DO_INSTALL DO_RESTORE DO_SSL DO_FW DO_TUNE DO_FREEPORTS

set -u
export DEBIAN_FRONTEND=noninteractive
export PATH="$PATH:/usr/local/sbin:/usr/local/bin:/usr/sbin:/sbin"
export HOME=/root

step() { echo; echo "==> $*"; }
ok()   { echo "[OK] $*"; }
warn() { echo "[WARN] $*"; }
info() { echo "     $*"; }
die()  { echo "[ERROR] $*"; exit 1; }
nocolor() { sed -u -e 's/\x1b\[[0-9;]*[A-Za-z]//g' -e 's/\r//g'; }

[ "$(id -u)" -eq 0 ] || die "This must run as root (log in as root, or use a user with sudo)."

XUI_DIR=/usr/local/x-ui
XUI_BIN=$XUI_DIR/x-ui
DB_DIR=/etc/x-ui
DB_FILE=$DB_DIR/x-ui.db
ACME="$HOME/.acme.sh/acme.sh"

has() { command -v "$1" >/dev/null 2>&1; }

svc() {
    if has systemctl; then systemctl "$1" x-ui >/dev/null 2>&1
    else rc-service x-ui "$1" >/dev/null 2>&1; fi
}

svc_active() {
    if has systemctl; then systemctl is-active --quiet x-ui
    else rc-service x-ui status >/dev/null 2>&1; fi
}

# Fresh Ubuntu/Debian VPSes run unattended-upgrades on first boot and hold the apt lock.
wait_pkg_lock() {
    has apt-get || return 0
    local i
    for i in $(seq 1 60); do
        if pgrep -x apt >/dev/null || pgrep -x apt-get >/dev/null || pgrep -x dpkg >/dev/null \
            || pgrep -f unattended-upgrade >/dev/null; then
            [ "$i" -eq 1 ] && info "Waiting for automatic updates to finish (apt is busy)..."
            sleep 5
        else
            dpkg --configure -a >/dev/null 2>&1
            return 0
        fi
    done
    warn "apt is still busy after 5 minutes - continuing anyway."
}

pkg_install() {
    wait_pkg_lock
    if has apt-get; then
        apt-get update -qq >/dev/null 2>&1
        apt-get install -y -qq "$@" >/dev/null 2>&1
    elif has dnf; then dnf install -y -q "$@" >/dev/null 2>&1
    elif has yum; then yum install -y -q "$@" >/dev/null 2>&1
    elif has apk; then apk add -q "$@" >/dev/null 2>&1
    elif has pacman; then pacman -Sy --noconfirm "$@" >/dev/null 2>&1
    elif has zypper; then zypper -q install -y "$@" >/dev/null 2>&1
    else return 1; fi
}

# Name of the process listening on a TCP port (empty when free).
port_owner() {
    has ss || return 0
    ss -ltnpH "sport = :$1" 2>/dev/null | head -n1 | sed -n 's/.*users:(("\([^"]*\)".*/\1/p'
}

# ---------------------------------------------------------------------------
step "Server check"
. /etc/os-release 2>/dev/null
OS_ID="${ID:-unknown}"
info "OS: ${PRETTY_NAME:-unknown}   Arch: $(uname -m)   Kernel: $(uname -r)"
case "$OS_ID" in
    ubuntu | debian | armbian | centos | rhel | rocky | almalinux | fedora | ol | amzn | arch | manjaro | alpine | opensuse* | sles) ;;
    *) warn "$OS_ID is not a tested OS. Ubuntu 22.04/24.04 or Debian 12 are recommended." ;;
esac
if [ "$OS_ID" = "centos" ] && [ "$(echo "${VERSION_ID:-0}" | cut -d. -f1)" -lt 8 ] 2>/dev/null; then
    warn "CentOS 7 is end-of-life and often fails. Reinstall the VPS with Ubuntu 24.04 or Debian 12 if anything breaks."
fi
VIRT=$(systemd-detect-virt 2>/dev/null || echo unknown)
MEM_MB=$(awk '/^MemTotal/ {print int($2/1024)}' /proc/meminfo)
SWAP_MB=$(awk '/^SwapTotal/ {print int($2/1024)}' /proc/meminfo)
DISK_MB=$(df -Pm / | awk 'NR==2 {print $4}')
info "Virtualization: $VIRT   RAM: ${MEM_MB} MB   Swap: ${SWAP_MB} MB   Free disk: ${DISK_MB} MB"
[ "${DISK_MB:-0}" -lt 500 ] && die "Less than 500 MB free disk space. Free some space (or resize the disk) and run again."
[ "${MEM_MB:-0}" -lt 400 ] && warn "Very little RAM (${MEM_MB} MB). A swap file will help."

# DNS resolver on the server: some providers ship a broken one.
if ! getent hosts github.com >/dev/null 2>&1; then
    warn "This server cannot resolve domain names. Adding public DNS servers (1.1.1.1, 8.8.8.8)."
    if [ -L /etc/resolv.conf ] && readlink /etc/resolv.conf | grep -q systemd && has systemctl; then
        mkdir -p /etc/systemd/resolved.conf.d
        printf '[Resolve]\nDNS=1.1.1.1 8.8.8.8\nFallbackDNS=1.0.0.1 8.8.4.4\n' > /etc/systemd/resolved.conf.d/maizy-dns.conf
        systemctl restart systemd-resolved >/dev/null 2>&1
    else
        cp -f /etc/resolv.conf /etc/resolv.conf.maizy-bak 2>/dev/null
        printf 'nameserver 1.1.1.1\nnameserver 8.8.8.8\n' > /etc/resolv.conf
    fi
    sleep 2
    getent hosts github.com >/dev/null 2>&1 || die "Still cannot resolve github.com. Check the VPS network settings in your provider's panel."
    ok "DNS resolver fixed"
fi

PUBLIC_IP=""
for u in https://api4.ipify.org https://ipv4.icanhazip.com https://4.ident.me; do
    PUBLIC_IP=$(curl -4 -fs --max-time 5 "$u" 2>/dev/null | tr -d '[:space:]')
    [[ "$PUBLIC_IP" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] && break
    PUBLIC_IP=""
done
info "Public IPv4: ${PUBLIC_IP:-unknown}"
[ -z "$PUBLIC_IP" ] && warn "No working IPv4 internet. IPv6-only servers cannot download from GitHub without a NAT64/WARP setup."
curl -fsS --max-time 15 -o /dev/null https://github.com 2>/dev/null || warn "github.com is not reachable over HTTPS from this server - the install may fail."

step "Installing required packages"
missing=""
for p in curl socat openssl tar; do has "$p" || missing="$missing $p"; done
if [ -n "$missing" ]; then
    info "Installing:$missing ca-certificates"
    pkg_install $missing ca-certificates || warn "Package install reported a problem."
fi
has crontab || pkg_install cron || pkg_install cronie || true
has ss || pkg_install iproute2 || pkg_install iproute || true
has curl || die "curl is not available and could not be installed."
ok "Packages ready"

# ---------------------------------------------------------------------------
if [ "$DO_TUNE" = "1" ]; then
    step "Speed and stability tweaks"

    # Correct time matters: TLS handshakes and VLESS/VMess fail when the clock is off.
    if has timedatectl; then
        timedatectl set-ntp true >/dev/null 2>&1
        if ! timedatectl show -p NTPSynchronized --value 2>/dev/null | grep -q yes; then
            pkg_install systemd-timesyncd >/dev/null 2>&1 || pkg_install chrony >/dev/null 2>&1
            systemctl enable --now systemd-timesyncd >/dev/null 2>&1 || systemctl enable --now chronyd >/dev/null 2>&1 \
                || systemctl enable --now chrony >/dev/null 2>&1
            timedatectl set-ntp true >/dev/null 2>&1
        fi
        ok "Time sync on (UTC now: $(date -u '+%Y-%m-%d %H:%M:%S'))"
        if [ -n "$TIMEZONE" ] && timedatectl list-timezones 2>/dev/null | grep -qx "$TIMEZONE"; then
            timedatectl set-timezone "$TIMEZONE" >/dev/null 2>&1 && ok "Timezone set to $TIMEZONE (used by traffic resets and the Telegram bot)"
        fi
    fi

    # BBR congestion control: noticeably faster on long-distance links.
    case "$VIRT" in
        openvz | lxc | lxc-libvirt)
            warn "$VIRT containers cannot change kernel settings - BBR and swap skipped." ;;
        *)
            modprobe tcp_bbr >/dev/null 2>&1
            cat > /etc/sysctl.d/99-maizy-tuning.conf <<'SYSCTL'
net.core.default_qdisc = fq
net.ipv4.tcp_congestion_control = bbr
net.ipv4.tcp_fastopen = 3
net.ipv4.tcp_mtu_probing = 1
net.core.rmem_max = 16777216
net.core.wmem_max = 16777216
net.ipv4.ip_local_port_range = 10000 65000
fs.file-max = 1048576
SYSCTL
            sysctl -p /etc/sysctl.d/99-maizy-tuning.conf >/dev/null 2>&1
            if [ "$(sysctl -n net.ipv4.tcp_congestion_control 2>/dev/null)" = "bbr" ]; then
                ok "BBR enabled"
            else
                warn "BBR is not supported by this kernel ($(uname -r))."
            fi

            # Swap: small VPSes run out of memory while installing or under load.
            if [ "${SWAP_MB:-0}" -lt 256 ] && [ "${MEM_MB:-0}" -lt 2048 ]; then
                if [ "${DISK_MB:-0}" -gt 3000 ] && [ ! -e /maizy.swap ]; then
                    fallocate -l 1G /maizy.swap 2>/dev/null || dd if=/dev/zero of=/maizy.swap bs=1M count=1024 status=none
                    chmod 600 /maizy.swap
                    if mkswap /maizy.swap >/dev/null 2>&1 && swapon /maizy.swap 2>/dev/null; then
                        grep -q '^/maizy.swap' /etc/fstab || echo '/maizy.swap none swap sw 0 0' >> /etc/fstab
                        ok "Created a 1 GB swap file"
                    else
                        rm -f /maizy.swap
                        warn "Could not enable swap on this VPS type."
                    fi
                else
                    warn "Not enough free disk for a swap file."
                fi
            else
                ok "Memory/swap is fine"
            fi
            ;;
    esac

    # Raise the open-files limit for x-ui/xray (many connections).
    if has systemctl; then
        mkdir -p /etc/systemd/system/x-ui.service.d
        printf '[Service]\nLimitNOFILE=1048576\n' > /etc/systemd/system/x-ui.service.d/20-maizy-limits.conf
        systemctl daemon-reload >/dev/null 2>&1
        ok "Open-files limit raised for x-ui"
    fi
fi

# ---------------------------------------------------------------------------
if [ "$DO_INSTALL" = "1" ]; then
    step "Installing / updating 3x-ui (latest release)"
    wait_pkg_lock
    curl -fsSL --max-time 90 -o /root/3x-ui-install.sh \
        https://raw.githubusercontent.com/MHSanaei/3x-ui/main/install.sh \
        || die "Could not download the 3x-ui installer from GitHub."
    info "(A fresh install prints temporary login details - they are replaced by your backup.)"
    XUI_NONINTERACTIVE=1 XUI_SSL_MODE=none XUI_DB_TYPE=sqlite \
        bash /root/3x-ui-install.sh </dev/null 2>&1 | nocolor | grep -v -E '^[[:space:]]*$'
    rc=${PIPESTATUS[0]}
    rm -f /root/3x-ui-install.sh
    [ -x "$XUI_BIN" ] || die "3x-ui installation failed (installer exit code $rc)."
    ok "3x-ui is installed"
else
    [ -x "$XUI_BIN" ] || die "3x-ui is not installed on this server. Tick 'Install / update 3x-ui' and run again."
fi

# ---------------------------------------------------------------------------
if [ "$DO_RESTORE" = "1" ]; then
    step "Restoring database backup"
    [ -s "$DB_SRC" ] || die "Uploaded backup not found at $DB_SRC"
    head -c 16 "$DB_SRC" | grep -aq "SQLite format 3" || die "Uploaded file is not a SQLite database."
    svc stop
    sleep 1
    mkdir -p "$DB_DIR"
    if [ -f "$DB_FILE" ]; then
        old="$DB_FILE.before-restore-$(date +%Y%m%d-%H%M%S)"
        cp -f "$DB_FILE" "$old" && info "Previous database saved as $old"
    fi
    rm -f "$DB_FILE-wal" "$DB_FILE-shm" "$DB_FILE-journal"
    install -m 600 -o root -g root "$DB_SRC" "$DB_FILE" || die "Could not write $DB_FILE"
    rm -f "$DB_SRC"
    ok "Database restored to $DB_FILE"
fi

# ---------------------------------------------------------------------------
if [ "$DO_FREEPORTS" = "1" ]; then
    step "Making sure the panel and inbound ports are free"
    svc stop
    sleep 1
    busy=0
    for p in $OPEN_PORTS; do
        owner=$(port_owner "$p")
        [ -z "$owner" ] && continue
        case "$owner" in
            nginx | apache2 | httpd | caddy | lighttpd | haproxy)
                svcname="$owner"
                if has systemctl; then
                    systemctl stop "$svcname" >/dev/null 2>&1
                    systemctl disable "$svcname" >/dev/null 2>&1
                else
                    rc-service "$svcname" stop >/dev/null 2>&1
                    rc-update del "$svcname" >/dev/null 2>&1
                fi
                ok "Stopped and disabled $owner (it was using port $p)"
                ;;
            xray* | x-ui*) ;;
            *)
                warn "Port $p is used by '$owner'. The x-ui inbound/panel on that port will not start until it is removed."
                busy=1
                ;;
        esac
    done
    [ "$busy" = "0" ] && ok "Ports are free: $OPEN_PORTS"
fi

# ---------------------------------------------------------------------------
RATE_LIMITED=0
explain_acme_failure() {
    local log="$1"
    RATE_LIMITED=0
    if grep -qiE 'rateLimited|too many certificates|too many failed' "$log"; then
        RATE_LIMITED=1
        warn "Let's Encrypt rate limit reached for $DOMAIN (max 5 identical certificates per week)."
    elif grep -qiE 'CAA' "$log"; then
        warn "A CAA record on your domain does not allow Let's Encrypt. Remove it or add letsencrypt.org in Cloudflare DNS."
    elif grep -qiE 'Invalid API|Authentication error|9109|10000|Invalid request headers' "$log"; then
        warn "Cloudflare rejected the API token. Create one with the 'Edit zone DNS' template for this domain."
    elif grep -qiE 'Timeout|Connection refused|connection reset|Fetching http|Invalid response|unauthorized' "$log"; then
        warn "Let's Encrypt could not reach http://$DOMAIN/ on port 80."
        warn "Check: the A record points to ${PUBLIC_IP:-this server}, the Cloudflare cloud is grey (DNS only),"
        warn "and your VPS provider's firewall/security group allows TCP 80."
    fi
}

if [ "$DO_SSL" = "1" ]; then
    step "SSL certificate for $DOMAIN"
    if [ ! -x "$ACME" ]; then
        info "Installing acme.sh"
        if [ -n "$ACME_EMAIL" ]; then
            curl -fsSL https://get.acme.sh | sh -s email="$ACME_EMAIL" >/dev/null 2>&1
        else
            curl -fsSL https://get.acme.sh | sh >/dev/null 2>&1
        fi
        [ -x "$ACME" ] || die "Could not install acme.sh."
    fi
    "$ACME" --set-default-ca --server letsencrypt >/dev/null 2>&1
    if [ -n "$ACME_EMAIL" ]; then
        "$ACME" --register-account -m "$ACME_EMAIL" --server letsencrypt >/dev/null 2>&1
    fi
    mkdir -p "$(dirname "$CERT_FILE")" "$(dirname "$KEY_FILE")"
    ACME_LOG=$(mktemp)

    HOOK_STOP="systemctl stop x-ui 2>/dev/null || rc-service x-ui stop 2>/dev/null; true"
    HOOK_START="systemctl start x-ui 2>/dev/null || rc-service x-ui start 2>/dev/null; true"

    issue_cert() { # $1 = CA server
        if [ -n "$CF_TOKEN" ]; then
            CF_Token="$CF_TOKEN" "$ACME" --issue -d "$DOMAIN" --dns dns_cf --server "$1" --keylength ec-256 2>&1 | nocolor | tee "$ACME_LOG"
        else
            "$ACME" --issue -d "$DOMAIN" --standalone --httpport 80 --server "$1" --keylength ec-256 \
                --pre-hook "$HOOK_STOP" --post-hook "$HOOK_START" 2>&1 | nocolor | tee "$ACME_LOG"
        fi
        return "${PIPESTATUS[0]}"
    }

    if [ -n "$CF_TOKEN" ]; then
        info "Validation method: Cloudflare DNS (port 80 does not need to be free)"
        export CF_Token="$CF_TOKEN"
    else
        info "Validation method: HTTP on port 80 (x-ui is paused while the cert is issued or renewed)"
        RESOLVED=$(getent ahostsv4 "$DOMAIN" 2>/dev/null | awk '{print $1; exit}')
        if [ -n "$PUBLIC_IP" ] && [ "$RESOLVED" != "$PUBLIC_IP" ]; then
            warn "$DOMAIN resolves to ${RESOLVED:-nothing} but this server is $PUBLIC_IP."
            warn "Let's Encrypt will fail until the DNS A record points to this server."
        fi
        svc stop
        sleep 1
        owner=$(port_owner 80)
        [ -n "$owner" ] && warn "Port 80 is in use by '$owner' - the certificate request will probably fail."
    fi

    issue_cert letsencrypt
    rc=$?

    # 0 = issued, 2 = already valid (renewal not due yet)
    if [ "$rc" -ne 0 ] && [ "$rc" -ne 2 ]; then
        explain_acme_failure "$ACME_LOG"
        if [ "$RATE_LIMITED" = "1" ]; then
            if [ -n "$ACME_EMAIL" ]; then
                info "Trying ZeroSSL instead..."
                "$ACME" --register-account -m "$ACME_EMAIL" --server zerossl >/dev/null 2>&1
                issue_cert zerossl
                rc=$?
                [ "$rc" -eq 0 ] || [ "$rc" -eq 2 ] || explain_acme_failure "$ACME_LOG"
            else
                warn "Enter an email in the app and run again to fall back to ZeroSSL, or wait a few days."
            fi
        fi
    fi
    if [ "$rc" -ne 0 ] && [ "$rc" -ne 2 ]; then
        rm -f "$ACME_LOG"
        svc start
        die "Certificate request failed (acme.sh exit code $rc). See the hints above."
    fi
    rm -f "$ACME_LOG"
    [ "$rc" -eq 2 ] && info "A valid certificate already exists - reusing it."

    "$ACME" --install-cert -d "$DOMAIN" --ecc \
        --key-file "$KEY_FILE" --fullchain-file "$CERT_FILE" \
        --reloadcmd "systemctl restart x-ui 2>/dev/null || rc-service x-ui restart 2>/dev/null; true" 2>&1 | nocolor
    [ -s "$CERT_FILE" ] && [ -s "$KEY_FILE" ] || die "Certificate files were not written to $CERT_FILE / $KEY_FILE"
    chmod 600 "$KEY_FILE"
    chmod 644 "$CERT_FILE"
    "$ACME" --upgrade --auto-upgrade >/dev/null 2>&1
    if ! crontab -l 2>/dev/null | grep -q acme.sh; then
        "$ACME" --install-cronjob >/dev/null 2>&1
    fi
    crontab -l 2>/dev/null | grep -q acme.sh && ok "Auto-renew job installed" || warn "No cron job for renewal - install cron and run: $ACME --install-cronjob"

    # Make sure the panel itself uses this certificate.
    "$XUI_BIN" cert -webCert "$CERT_FILE" -webCertKey "$KEY_FILE" >/dev/null 2>&1 || true

    openssl x509 -noout -subject -issuer -enddate -in "$CERT_FILE" 2>/dev/null | sed 's/^/     /'
    ok "Certificate installed (auto-renews every 60 days)"
    info "Cert: $CERT_FILE"
    info "Key:  $KEY_FILE"
fi

# ---------------------------------------------------------------------------
if [ "$DO_FW" = "1" ]; then
    step "Firewall"
    if has ufw && ufw status 2>/dev/null | grep -q "Status: active"; then
        ufw allow "$SSH_PORT"/tcp >/dev/null 2>&1
        for p in $OPEN_PORTS; do ufw allow "$p" >/dev/null 2>&1; done
        ok "ufw: opened ports $OPEN_PORTS"
    elif has firewall-cmd && firewall-cmd --state >/dev/null 2>&1; then
        for p in $OPEN_PORTS; do
            firewall-cmd --permanent --add-port="$p/tcp" >/dev/null 2>&1
            firewall-cmd --permanent --add-port="$p/udp" >/dev/null 2>&1
        done
        firewall-cmd --reload >/dev/null 2>&1
        ok "firewalld: opened ports $OPEN_PORTS"
    elif has iptables && iptables -S INPUT 2>/dev/null | grep -qE -- '-j (REJECT|DROP)|-P INPUT DROP'; then
        for p in $OPEN_PORTS; do
            iptables -C INPUT -p tcp --dport "$p" -j ACCEPT 2>/dev/null || iptables -I INPUT -p tcp --dport "$p" -j ACCEPT
            iptables -C INPUT -p udp --dport "$p" -j ACCEPT 2>/dev/null || iptables -I INPUT -p udp --dport "$p" -j ACCEPT
        done
        if has netfilter-persistent; then netfilter-persistent save >/dev/null 2>&1
        elif has iptables-save && [ -d /etc/iptables ]; then iptables-save > /etc/iptables/rules.v4
        fi
        ok "iptables: opened ports $OPEN_PORTS (Oracle Cloud style rules)"
    else
        ok "No local firewall is blocking."
    fi
    info "If your VPS provider has a cloud firewall / security group, open there too: $OPEN_PORTS"
fi

# ---------------------------------------------------------------------------
step "Starting 3x-ui"
has systemctl && systemctl enable x-ui >/dev/null 2>&1
svc restart
sleep 5
if svc_active; then
    ok "x-ui service is running"
else
    warn "x-ui service is NOT running. Last log lines:"
    has journalctl && journalctl -u x-ui -n 30 --no-pager 2>/dev/null | sed 's/^/     /'
    die "3x-ui failed to start."
fi

if has ss; then
    info "Ports on this server:"
    for p in $OPEN_PORTS; do
        owner=$(port_owner "$p")
        if [ -n "$owner" ]; then info "  $p  listening ($owner)"; else info "  $p  not listening (inbound disabled or failed - check the panel)"; fi
    done
fi

step "Checking the panel"
URL="https://$DOMAIN:$PANEL_PORT$PANEL_PATH"
code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 --resolve "$DOMAIN:$PANEL_PORT:127.0.0.1" "$URL")
if [ "$code" != "000" ]; then
    ok "Panel answers on $URL with a trusted certificate (HTTP $code)"
else
    code=$(curl -sk -o /dev/null -w '%{http_code}' --max-time 15 --resolve "$DOMAIN:$PANEL_PORT:127.0.0.1" "$URL")
    if [ "$code" != "000" ]; then
        warn "Panel answers (HTTP $code) but the certificate is not trusted for $DOMAIN."
    else
        code=$(curl -s -o /dev/null -w '%{http_code}' --max-time 15 "http://127.0.0.1:$PANEL_PORT$PANEL_PATH")
        warn "Panel does not answer over HTTPS (plain HTTP result: $code). Check the certificate settings."
    fi
fi

echo
ok "All done."
exit 0
