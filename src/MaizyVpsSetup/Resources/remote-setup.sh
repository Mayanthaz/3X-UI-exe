#!/usr/bin/env bash
# Maizy VPS Setup - server side.
# The Windows app writes the configuration variables above this line:
#   DOMAIN CERT_FILE KEY_FILE ACME_EMAIL CF_TOKEN DB_SRC PANEL_PORT PANEL_PATH
#   OPEN_PORTS SSH_PORT DO_INSTALL DO_RESTORE DO_SSL DO_FW

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

pkg_install() {
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

# ---------------------------------------------------------------------------
step "Server check"
. /etc/os-release 2>/dev/null
info "OS: ${PRETTY_NAME:-unknown}   Arch: $(uname -m)"
PUBLIC_IP=""
for u in https://api4.ipify.org https://ipv4.icanhazip.com https://4.ident.me; do
    PUBLIC_IP=$(curl -4 -fs --max-time 5 "$u" 2>/dev/null | tr -d '[:space:]')
    [[ "$PUBLIC_IP" =~ ^[0-9]+\.[0-9]+\.[0-9]+\.[0-9]+$ ]] && break
    PUBLIC_IP=""
done
info "Public IPv4: ${PUBLIC_IP:-unknown}"

step "Installing required packages"
missing=""
for p in curl socat openssl tar; do has "$p" || missing="$missing $p"; done
if [ -n "$missing" ]; then
    info "Installing:$missing ca-certificates"
    pkg_install $missing ca-certificates || warn "Package install reported a problem."
fi
has crontab || pkg_install cron || pkg_install cronie || true
has curl || die "curl is not available and could not be installed."
ok "Packages ready"

# ---------------------------------------------------------------------------
if [ "$DO_INSTALL" = "1" ]; then
    step "Installing / updating 3x-ui (latest release)"
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

    if [ -n "$CF_TOKEN" ]; then
        info "Validation method: Cloudflare DNS (no need to free port 80)"
        export CF_Token="$CF_TOKEN"
        "$ACME" --issue -d "$DOMAIN" --dns dns_cf --server letsencrypt --keylength ec-256 2>&1 | nocolor
        rc=${PIPESTATUS[0]}
    else
        info "Validation method: HTTP on port 80 (x-ui is paused while the cert is issued or renewed)"
        RESOLVED=$(getent ahostsv4 "$DOMAIN" 2>/dev/null | awk '{print $1; exit}')
        if [ -n "$PUBLIC_IP" ] && [ "$RESOLVED" != "$PUBLIC_IP" ]; then
            warn "$DOMAIN resolves to ${RESOLVED:-nothing} but this server is $PUBLIC_IP."
            warn "Let's Encrypt will fail until the DNS A record points to this server."
        fi
        svc stop
        sleep 1
        if has ss && ss -ltn 'sport = :80' 2>/dev/null | grep -q LISTEN; then
            warn "Something else is listening on port 80:"
            ss -ltnp 'sport = :80' 2>/dev/null | tail -n +2
        fi
        HOOK_STOP="systemctl stop x-ui 2>/dev/null || rc-service x-ui stop 2>/dev/null; true"
        HOOK_START="systemctl start x-ui 2>/dev/null || rc-service x-ui start 2>/dev/null; true"
        "$ACME" --issue -d "$DOMAIN" --standalone --httpport 80 --server letsencrypt --keylength ec-256 \
            --pre-hook "$HOOK_STOP" --post-hook "$HOOK_START" 2>&1 | nocolor
        rc=${PIPESTATUS[0]}
    fi

    # 0 = issued, 2 = already valid (renewal not due yet)
    if [ "$rc" -ne 0 ] && [ "$rc" -ne 2 ]; then
        svc start
        die "Certificate request failed (acme.sh exit code $rc). Check that $DOMAIN points to this server and port 80 is reachable."
    fi
    [ "$rc" -eq 2 ] && info "A valid certificate already exists - reusing it."

    "$ACME" --install-cert -d "$DOMAIN" --ecc \
        --key-file "$KEY_FILE" --fullchain-file "$CERT_FILE" \
        --reloadcmd "systemctl restart x-ui 2>/dev/null || rc-service x-ui restart 2>/dev/null; true" 2>&1 | nocolor
    [ -s "$CERT_FILE" ] && [ -s "$KEY_FILE" ] || die "Certificate files were not written to $CERT_FILE / $KEY_FILE"
    chmod 600 "$KEY_FILE"
    chmod 644 "$CERT_FILE"
    "$ACME" --upgrade --auto-upgrade >/dev/null 2>&1

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
        ok "iptables: opened ports $OPEN_PORTS"
    else
        ok "No local firewall is blocking. (If your VPS provider has a cloud firewall, open: $OPEN_PORTS)"
    fi
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
    info "Listening ports:"
    for p in $OPEN_PORTS; do
        if ss -ltn "sport = :$p" 2>/dev/null | grep -q LISTEN; then info "  $p  listening"; else info "  $p  not listening"; fi
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
