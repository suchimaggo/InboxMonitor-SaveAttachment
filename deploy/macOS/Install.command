#!/bin/bash
set -euo pipefail

SOURCE_DIR="$(cd -- "$(dirname -- "$0")" && pwd)"
INSTALL_DIR="${HOME}/Applications/InboxUtilities"

if [[ "$(uname -s)" != "Darwin" ]]; then
  echo "This installer is for macOS."
  exit 1
fi

if [[ "$(uname -m)" != "arm64" ]]; then
  echo "This package targets Apple silicon (arm64). This Mac reports: $(uname -m)"
  exit 1
fi

if [[ -e "$INSTALL_DIR" ]]; then
  echo "An installation already exists at:"
  echo "  $INSTALL_DIR"
  echo "To avoid overwriting its mailbox settings, back it up and move it before installing an update."
  exit 1
fi

mkdir -p "${HOME}/Applications"
ditto "$SOURCE_DIR" "$INSTALL_DIR"
chmod +x \
  "$INSTALL_DIR/InboxReader/InboxReader" \
  "$INSTALL_DIR/Dashboard/UtilityDashboard" \
  "$INSTALL_DIR/NewCase/New Case Create" \
  "$INSTALL_DIR/LoanProcessing/LoanProcessing-Utility" \
  "$INSTALL_DIR/Start.command" \
  "$INSTALL_DIR/Stop.command"

echo "Installed to: $INSTALL_DIR"
echo "Next, configure InboxReader/appsettings.json with your mailbox settings."
echo "Then run Start.command from the installed folder."
read -r -p "Press Return to close this window. "
