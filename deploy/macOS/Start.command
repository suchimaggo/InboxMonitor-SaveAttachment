#!/bin/bash
set -euo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "$0")" && pwd)"
READER="$ROOT_DIR/InboxReader/InboxReader"
DASHBOARD="$ROOT_DIR/Dashboard/UtilityDashboard"
NEW_CASE="$ROOT_DIR/NewCase/New Case Create"
SETTINGS="$ROOT_DIR/InboxReader/appsettings.json"
LOG_DIR="$ROOT_DIR/logs"
PID_DIR="$ROOT_DIR/.pids"
export PATH="/opt/homebrew/bin:/usr/local/bin:$PATH"

if [[ ! -x "$READER" || ! -x "$DASHBOARD" || ! -x "$NEW_CASE" ]]; then
  echo "Application executables were not found. Run Install.command first."
  read -r -p "Press Return to close this window. "
  exit 1
fi

if ! grep -Eq '"Email"[[:space:]]*:[[:space:]]*"[^"]+"' "$SETTINGS"; then
  echo "Configure InboxReader.Email in:"
  echo "  $SETTINGS"
  read -r -p "Press Return to close this window. "
  exit 1
fi

MAIL_EXCHANGE_SERVER="$(sed -nE 's/^[[:space:]]*"MailExchangeServer"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' "$SETTINGS" | head -n 1)"
if [[ -z "$MAIL_EXCHANGE_SERVER" ]]; then
  MAIL_EXCHANGE_SERVER="$(sed -nE 's/^[[:space:]]*"Provider"[[:space:]]*:[[:space:]]*"([^"]+)".*/\1/p' "$SETTINGS" | head -n 1)"
fi

case "$MAIL_EXCHANGE_SERVER" in
  [Zz][Oo][Hh][Oo])
    if ! grep -Eq '"Password"[[:space:]]*:[[:space:]]*"[^"]+"' "$SETTINGS"; then
      echo "Configure the Zoho app-specific password in:"
      echo "  $SETTINGS"
      read -r -p "Press Return to close this window. "
      exit 1
    fi
    ;;
  [Oo][Uu][Tt][Ll][Oo][Oo][Kk])
    if ! grep -Eq '"OutlookClientId"[[:space:]]*:[[:space:]]*"[^"]+"' "$SETTINGS"; then
      echo "Configure OutlookClientId in:"
      echo "  $SETTINGS"
      read -r -p "Press Return to close this window. "
      exit 1
    fi
    ;;
  *)
    echo "Set MailExchangeServer to Zoho or Outlook in:"
    echo "  $SETTINGS"
    read -r -p "Press Return to close this window. "
    exit 1
    ;;
esac

mkdir -p "$LOG_DIR" "$PID_DIR"
export InboxReader__OutputFolder="${HOME}/InboxAttachments"

start_service() {
  local name="$1"
  local executable="$2"
  local url="$3"
  local pid_file="$PID_DIR/$name.pid"

  if [[ -f "$pid_file" ]] && kill -0 "$(cat "$pid_file")" 2>/dev/null; then
    if curl --silent --fail "$url" >/dev/null; then
      return
    fi
  fi

  nohup "$executable" >>"$LOG_DIR/$name.log" 2>&1 </dev/null &
  echo "$!" >"$pid_file"
}

start_service "InboxReader" "$READER" "http://127.0.0.1:5087/api/status"
start_service "NewCase" "$NEW_CASE" "http://127.0.0.1:5088/api/config"
start_service "UtilityDashboard" "$DASHBOARD" "http://127.0.0.1:5086/"
ready=false
for _ in {1..30}; do
  if curl --silent --fail "http://127.0.0.1:5086/" >/dev/null &&
     curl --silent --fail "http://127.0.0.1:5087/api/status" >/dev/null &&
     curl --silent --fail "http://127.0.0.1:5088/api/config" >/dev/null; then
    ready=true
    break
  fi
  sleep 1
done

if [[ "$ready" != true ]]; then
  echo "The utilities did not start. Check logs in:"
  echo "  $LOG_DIR"
  read -r -p "Press Return to close this window. "
  exit 1
fi

open "http://127.0.0.1:5086/"
