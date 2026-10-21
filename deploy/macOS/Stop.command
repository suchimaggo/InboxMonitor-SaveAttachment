#!/bin/bash
set -euo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "$0")" && pwd)"
PID_DIR="$ROOT_DIR/.pids"

for name in UtilityDashboard InboxReader NewCase; do
  pid_file="$PID_DIR/$name.pid"
  if [[ ! -f "$pid_file" ]]; then
    continue
  fi

  pid="$(cat "$pid_file")"
  case "$name" in
    UtilityDashboard) executable="$ROOT_DIR/Dashboard/UtilityDashboard" ;;
    InboxReader) executable="$ROOT_DIR/InboxReader/InboxReader" ;;
    NewCase) executable="$ROOT_DIR/NewCase/New Case Create" ;;
  esac
  process_path="$(lsof -a -p "$pid" -d txt -Fn 2>/dev/null | sed -n 's/^n//p' | head -n 1 || true)"

  if [[ -n "$process_path" && "$process_path" == "$executable" ]]; then
    kill "$pid"
    echo "Stopped $name (PID $pid)."
  elif kill -0 "$pid" 2>/dev/null; then
    echo "PID $pid no longer matches $name; leaving it running."
  fi

  rm -f "$pid_file"
done
