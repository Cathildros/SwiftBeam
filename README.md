# SwiftBeam

Lightweight, cross-platform native desktop, CLI, and **Android** file transfer app for **Windows**, **Linux**, and **Android**.

## Key Features

- **Zero-dependency standalone binaries & Android APK** — single executable for Windows (`SwiftBeam-Setup.exe`), Linux (`SwiftBeam-Linux-x64`), and installable native Android app (`SwiftBeam-Android.apk`).
- **Wi-Fi, Wired LAN, and Direct Ethernet/USB-C Cable** — automatically discovers peers across all active interfaces (IPv4 broadcast + IPv6 link-local multicast `ff02::53:4201`) and ranks direct cable links ahead of Wi-Fi.
- **Pull as many files & folders as you want** — browse remote folders, search/filter, multi-select files and entire directory trees while preserving folder structure.
- **Parallel transfers** — configurable 1–16 parallel streams: pulls multiple files at once AND splits large files into multi-stream chunks written concurrently.
- **Drop-proof resume & integrity check** — persists chunk state (`.sbpart` + `.sbstate.json`), automatically reconnects with backoff if Wi-Fi drops or a cable is unplugged, resumes from the exact remaining chunks, and verifies SHA-256 before finalizing.
- **Quick Pairing PIN + toggleable encryption** — 6-digit PIN verified via HMAC-SHA256 challenge-response (channel-bound to the TLS certificate), with TLS 1.3 encryption enabled by default for Wi-Fi and one-click toggle off for maximum raw Gigabit/10GbE direct-cable throughput.

## Quick Start

### Easiest: 1-Click Local Download Page (No USB Stick Needed!)
1. Open **SwiftBeam** on your first computer and click **Share App Link** in the top bar.
2. Open the shown URL (e.g. `http://192.168.1.42:45892`) in a web browser on your other Windows or Linux computer.
3. Click **Download for Windows / Linux** — or copy the one-line installer command right off the page:
   - **Linux:** `curl -fsSL http://<host-ip>:45892/install.sh | bash`
   - **Windows (PowerShell):** `irm http://<host-ip>:45892/install.ps1 | iex`

### Manual Install — Windows
Copy `dist/win-x64/SwiftBeam.exe` to any Windows PC and double-click it — or run `dist/win-x64/install.ps1` to install into `%LOCALAPPDATA%\SwiftBeam` with a Start Menu shortcut.

### Manual Install — Linux
Copy `dist/linux-x64/SwiftBeam` to any Linux PC and run:
```bash
chmod +x SwiftBeam
./SwiftBeam
```
Or run `./install.sh` (add `--firewall` to automatically open ports `45890/tcp` and `45891/udp` in UFW/firewalld).

### Manual Install — Android
Download `SwiftBeam-Android.apk` from Releases and install it on your phone (Android 8.0+):
- Automatically discovers nearby Windows and Linux SwiftBeam PCs over Wi-Fi (UDP `45891`).
- Connects over TLS 1.3 + 6-digit PIN authentication directly to your PC (`45890/tcp`).
- Browse PC drives/folders, upload photos/files, share directly from Gallery via Android's **Share** sheet, download files/folders to `Downloads/SwiftBeam`, and send Quick Text clips.

## Direct Ethernet Cable Setup (Windows ↔ Linux)

1. Plug an Ethernet (or USB-C network) cable directly between the two computers.
2. Open **SwiftBeam** on both computers.
3. Both Windows and Linux automatically assign IPv6 link-local (`fe80::...`) addresses on the cable within 1–2 seconds; SwiftBeam discovers the peer and labels it **`Direct Cable`**.
4. Select the peer, enter the 6-digit PIN shown in the other computer's top bar, optionally uncheck **Encrypt transfer (TLS 1.3)** for maximum line speed, and click **Connect**.
5. *(Optional)* If your firewall blocks UDP port `45891`, copy the `Direct Cable` IP shown in **Host & Settings → Active Network Interfaces** on the host and paste it into the **Connect & Pair** box manually.

## Headless Linux CLI Usage

```bash
# Share a directory on a Linux server or terminal
./swiftbeam-cli serve --dir /path/to/files --pin 482915

# Discover nearby SwiftBeam computers
./swiftbeam-cli discover

# Pull a folder with 8 parallel streams over a direct cable
./swiftbeam-cli pull --host 169.254.12.88 --pin 482915 --remote backups --out ./downloads --parallel 8 --no-tls
```

## Ports Used

- `45890/tcp` — File browsing & parallel chunk transfers (configurable in Host & Settings)
- `45891/udp` — Automatic peer discovery (IPv4 broadcast + IPv6 link-local multicast)
- `45892/tcp` — Local 1-click installer download page (only active when **Share App Link** is enabled)

## Building from Source

```powershell
# Run all integration tests
dotnet test SwiftBeam.slnx

# Build standalone single-file binaries for Windows and Linux
powershell -ExecutionPolicy Bypass -File scripts/publish.ps1
```
