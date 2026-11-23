#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
BIN_DIR="${HOME}/.local/bin"
APP_DIR="${HOME}/.local/share/applications"
ICON_DIR="${HOME}/.local/share/icons/hicolor/256x256/apps"

mkdir -p "${BIN_DIR}" "${APP_DIR}" "${ICON_DIR}"

if [[ ! -f "${SCRIPT_DIR}/SwiftBeam" ]]; then
  echo "Error: SwiftBeam binary not found in ${SCRIPT_DIR}" >&2
  exit 1
fi

install -m 755 "${SCRIPT_DIR}/SwiftBeam" "${BIN_DIR}/SwiftBeam"
if [[ -f "${SCRIPT_DIR}/swiftbeam-cli" ]]; then
  install -m 755 "${SCRIPT_DIR}/swiftbeam-cli" "${BIN_DIR}/swiftbeam-cli"
fi
if [[ -f "${SCRIPT_DIR}/swiftbeam.png" ]]; then
  install -m 644 "${SCRIPT_DIR}/swiftbeam.png" "${ICON_DIR}/swiftbeam.png"
fi

cat > "${APP_DIR}/swiftbeam.desktop" <<EOF
[Desktop Entry]
Name=SwiftBeam
Comment=Fast cross-platform parallel file transfer over Wi-Fi or direct cable
Exec=${BIN_DIR}/SwiftBeam
Icon=swiftbeam
StartupWMClass=SwiftBeam
Terminal=false
Type=Application
Categories=Network;FileTransfer;Utility;
EOF

if command -v update-desktop-database >/dev/null 2>&1; then
  update-desktop-database "${APP_DIR}" >/dev/null 2>&1 || true
fi
if command -v gtk-update-icon-cache >/dev/null 2>&1; then
  gtk-update-icon-cache -f -t "${HOME}/.local/share/icons/hicolor" >/dev/null 2>&1 || true
fi

if [[ -d "${HOME}/Desktop" ]]; then
  cp "${APP_DIR}/swiftbeam.desktop" "${HOME}/Desktop/SwiftBeam.desktop"
  chmod 755 "${HOME}/Desktop/SwiftBeam.desktop"
  if command -v gio >/dev/null 2>&1; then
    gio set "${HOME}/Desktop/SwiftBeam.desktop" metadata::trusted true >/dev/null 2>&1 || true
  fi
fi

echo "Installed SwiftBeam to ${BIN_DIR}/SwiftBeam"
echo "Created application launcher at ${APP_DIR}/swiftbeam.desktop"

if [[ ":${PATH}:" != *":${BIN_DIR}:"* ]]; then
  echo "Tip: Add ${BIN_DIR} to your PATH (e.g. export PATH=\"\$HOME/.local/bin:\$PATH\")"
fi

if [[ "${1:-}" == "--firewall" ]]; then
  if command -v ufw >/dev/null 2>&1; then
    echo "Opening UFW ports 45890/tcp (transfer), 45891/udp (discovery), and 45892/tcp (local app installer page)..."
    sudo ufw allow 45890/tcp comment 'SwiftBeam transfer'
    sudo ufw allow 45891/udp comment 'SwiftBeam discovery'
    sudo ufw allow 45892/tcp comment 'SwiftBeam local installer'
  elif command -v firewall-cmd >/dev/null 2>&1; then
    echo "Opening firewalld ports 45890/tcp, 45891/udp, and 45892/tcp..."
    sudo firewall-cmd --permanent --add-port=45890/tcp
    sudo firewall-cmd --permanent --add-port=45891/udp
    sudo firewall-cmd --permanent --add-port=45892/tcp
    sudo firewall-cmd --reload
  fi
fi
