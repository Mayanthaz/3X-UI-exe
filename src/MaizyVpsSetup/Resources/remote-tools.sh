#!/usr/bin/env bash
# Maizy VPS Setup - server management actions.
# The Windows app writes these above this line: ACTION DOMAIN CF_TOKEN BK_DIR BK_OWNER OPEN_PORTS

set -u
export PATH="$PATH:/usr/local/sbin:/usr/local/bin:/usr/sbin:/sbin"
export HOME=/root

step() { echo; echo "==> $*"; }
ok()   { echo "[OK] $*"; }
warn() { echo "[WARN] $*"; }
info() { echo "     $*"; }
die()  { echo "[ERROR] $*"; exit 1; }
nocolor() { sed -u -e 's/\x1b\[[0-9;]*[A-Za-z]//g' -e 's/\r//g'; }
has() { command -v "$1" >/dev/null 2>&1; }

[ "$(id -u)" -eq 0 ] || die "This must run as root."

XUI_BIN=/usr/local/x-ui/x-ui
DB_FILE=/etc/x-ui/x-ui.db
ACME="$HOME/.acme.sh/acme.sh"

svc() { if has systemctl; then systemctl "$1" x-ui >/dev/null 2>&1; else rc-service x-ui "$1" >/dev/null 2>&1; fi; }
svc_active() { if has systemctl; then systemctl is-active --quiet x-ui; else rc-service x-ui status >/dev/null 2>&1; fi; }

port_owner() {
    has ss || return 0
    ss -ltnpH "sport = :$1" 2>/dev/null | head -n1 | sed -n 's/.*users:(("\([^"]*\)".*/\1/p'
}

panel_cert() {
    "$XUI_BIN" setting -getCert true 2>/dev/null | awk -F': ' '/^cert:/ {print $2}' | tr -d '[:space:]'
}

show_logs() {
    if has journalctl; then
        journalctl -u x-ui -n "${1:-60}" --no-pager -o short-iso 2>/dev/null | nocolor | sed 's/^/     /'
    elif [ -f /var/log/x-ui/3xipl.log ]; then
        tail -n "${1:-60}" /var/log/x-ui/*.log | sed 's/^/     /'
    else
        info "No logs found."
    fi
}

[ -x "$XUI_BIN" ] || [ "$ACTION" = "health" ] || die "3x-ui is not installed on this server."

case "$ACTION" in
# ---------------------------------------------------------------------------
health)
    step "System"
    . /etc/os-release 2>/dev/null
    info "OS: ${PRETTY_NAME:-unknown}   Kernel: $(uname -r)"
    info "Uptime: $(uptime -p 2>/dev/null || uptime)"
    info "Load: $(cut -d' ' -f1-3 /proc/loadavg)   CPUs: $(nproc 2>/dev/null || echo ?)"
    awk '/^MemTotal/ {t=$2} /^MemAvailable/ {a=$2} /^SwapTotal/ {s=$2} /^SwapFree/ {sf=$2}
         END {printf "     RAM: %d MB free of %d MB   Swap: %d MB used of %d MB\n", a/1024, t/1024, (s-sf)/1024, s/1024}' /proc/meminfo
    df -Pm / | awk 'NR==2 {printf "     Disk: %d MB free of %d MB (%s used)\n", $4, $2, $5}'
    disk_pct=$(df -P / | awk 'NR==2 {gsub("%","",$5); print $5}')
    [ "${disk_pct:-0}" -ge 90 ] && warn "Disk is ${disk_pct}% full - x-ui can corrupt its database when the disk fills up."
    avail=$(awk '/^MemAvailable/ {print int($2/1024)}' /proc/meminfo)
    [ "${avail:-999}" -lt 100 ] && warn "Less than 100 MB RAM available."

    step "3x-ui"
    if [ -x "$XUI_BIN" ]; then
        info "Panel version: $("$XUI_BIN" -v 2>/dev/null | head -n1)"
        XRAY=$(ls /usr/local/x-ui/bin/xray-linux-* 2>/dev/null | head -n1)
        [ -n "$XRAY" ] && info "Xray: $("$XRAY" version 2>/dev/null | head -n1)"
        if svc_active; then ok "x-ui service is running"; else warn "x-ui service is NOT running (use Restart panel)."; fi
        if pgrep -f xray-linux >/dev/null; then ok "Xray core is running"; else warn "Xray core is not running - check the panel logs."; fi
        "$XUI_BIN" setting -show true 2>/dev/null | grep -E '^(port|webBasePath):' | sed 's/^/     /'
        [ -f "$DB_FILE" ] && info "Database: $DB_FILE ($(du -h "$DB_FILE" | cut -f1))"
    else
        warn "3x-ui is not installed."
    fi

    step "Ports"
    for p in $OPEN_PORTS; do
        owner=$(port_owner "$p")
        if [ -n "$owner" ]; then info "$p  listening ($owner)"; else warn "$p  nothing is listening"; fi
    done

    step "SSL certificate"
    CERT=$(panel_cert)
    [ -z "$CERT" ] && CERT="/root/cert/$DOMAIN/fullchain.pem"
    if [ -s "$CERT" ]; then
        info "File: $CERT"
        openssl x509 -noout -subject -enddate -in "$CERT" 2>/dev/null | sed 's/^/     /'
        end=$(openssl x509 -noout -enddate -in "$CERT" 2>/dev/null | cut -d= -f2)
        days=$(( ( $(date -d "$end" +%s 2>/dev/null || echo 0) - $(date +%s) ) / 86400 ))
        if [ "$days" -lt 0 ]; then warn "Certificate EXPIRED $(( -days )) days ago - use Renew SSL."
        elif [ "$days" -lt 15 ]; then warn "Certificate expires in $days days - use Renew SSL."
        else ok "Certificate valid for $days more days"; fi
        openssl x509 -noout -checkhost "$DOMAIN" -in "$CERT" 2>/dev/null | grep -q 'does match' \
            || warn "This certificate is not for $DOMAIN."
    else
        warn "No certificate file found (panel may be running on plain HTTP)."
    fi
    if [ -x "$ACME" ]; then
        crontab -l 2>/dev/null | grep -q acme.sh && ok "Auto-renew job is installed" || warn "Auto-renew cron job is missing (Renew SSL reinstalls it)."
    fi

    step "Network and time"
    info "Public IPv4: $(curl -4 -fs --max-time 5 https://api4.ipify.org 2>/dev/null || echo unknown)"
    resolved=$(getent ahostsv4 "$DOMAIN" 2>/dev/null | awk '{print $1; exit}')
    info "$DOMAIN resolves to: ${resolved:-nothing}"
    cc=$(sysctl -n net.ipv4.tcp_congestion_control 2>/dev/null)
    [ "$cc" = "bbr" ] && ok "BBR is on" || info "Congestion control: ${cc:-unknown} (BBR is off - tick the tweaks option in setup)"
    if has timedatectl; then
        if timedatectl show -p NTPSynchronized --value 2>/dev/null | grep -q yes; then ok "Clock is synced ($(date '+%Y-%m-%d %H:%M %Z'))"
        else warn "Clock is NOT synced ($(date -u '+%H:%M UTC')) - wrong time breaks TLS/VLESS."; fi
    fi
    getent hosts github.com >/dev/null 2>&1 && ok "DNS resolver works" || warn "Server cannot resolve domain names."

    step "Firewall"
    if has ufw && ufw status 2>/dev/null | grep -q "Status: active"; then info "ufw is active"; ufw status 2>/dev/null | sed -n '5,30p' | sed 's/^/     /'
    elif has firewall-cmd && firewall-cmd --state >/dev/null 2>&1; then info "firewalld ports: $(firewall-cmd --list-ports 2>/dev/null)"
    else info "No ufw/firewalld running."; fi
    if has fail2ban-client; then
        fail2ban-client status 2>/dev/null | grep -q 'Jail list' && info "fail2ban: $(fail2ban-client status 2>/dev/null | grep 'Jail list' | cut -d: -f2)"
    fi

    step "Recent errors in the panel log"
    if has journalctl; then
        errs=$(journalctl -u x-ui --since "-24h" --no-pager 2>/dev/null | nocolor | grep -iE 'error|failed|panic' | tail -n 8)
        if [ -n "$errs" ]; then echo "$errs" | sed 's/^/     /'; else ok "No errors in the last 24 hours"; fi
    fi
    ;;

# ---------------------------------------------------------------------------
restart)
    step "Restarting 3x-ui"
    svc restart
    sleep 4
    if svc_active; then ok "x-ui is running"; else warn "x-ui did not start. Last log lines:"; show_logs 30; exit 1; fi
    for p in $OPEN_PORTS; do
        owner=$(port_owner "$p")
        [ -n "$owner" ] && info "$p  listening ($owner)" || info "$p  not listening"
    done
    ;;

# ---------------------------------------------------------------------------
logs)
    step "Last 80 lines of the x-ui log"
    show_logs 80
    ;;

# ---------------------------------------------------------------------------
renew)
    step "Renewing the SSL certificate for $DOMAIN"
    [ -x "$ACME" ] || die "acme.sh is not installed. Run setup with 'Get SSL certificate' ticked first."
    [ -n "$CF_TOKEN" ] && export CF_Token="$CF_TOKEN"
    "$ACME" --renew -d "$DOMAIN" --ecc --force 2>&1 | nocolor
    rc=${PIPESTATUS[0]}
    crontab -l 2>/dev/null | grep -q acme.sh || "$ACME" --install-cronjob >/dev/null 2>&1
    svc start
    sleep 3
    if [ "$rc" -ne 0 ]; then
        svc_active || svc start
        die "Renewal failed (acme.sh exit code $rc). Run Health check for details."
    fi
    svc restart
    sleep 3
    CERT=$(panel_cert)
    [ -n "$CERT" ] && openssl x509 -noout -enddate -in "$CERT" 2>/dev/null | sed 's/^/     /'
    ok "Certificate renewed and x-ui restarted"
    ;;

# ---------------------------------------------------------------------------
backup)
    step "Copying the database"
    [ -f "$DB_FILE" ] || die "No database at $DB_FILE"
    rm -rf "$BK_DIR"
    mkdir -p "$BK_DIR"
    # Copy the main file and its WAL together so the app can merge them into one consistent file.
    cp -f "$DB_FILE" "$BK_DIR/x-ui.db"
    [ -f "$DB_FILE-wal" ] && cp -f "$DB_FILE-wal" "$BK_DIR/x-ui.db-wal"
    chown -R "$BK_OWNER" "$BK_DIR" 2>/dev/null
    chmod 700 "$BK_DIR"
    ok "Database ready for download"
    ;;

*)
    die "Unknown action: $ACTION"
    ;;
esac
exit 0
