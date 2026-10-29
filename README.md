# LanBridge V1 — Clipboard & File Sharing for Windows LAN

A Windows desktop application for sharing **one explicit, group-wide text clipboard** and transferring files among **2–20 personally trusted Windows PCs** on the same private LAN. **No central server, cloud login, Windows network shares, or Internet connection required.**

> **Build status (2026-10-29): source delivered, Windows build and real-PC integration tests NOT executed in this environment.** The coding environment is Debian without the .NET SDK or Windows GUI. Treat this as source for review and Windows validation, not a verified production release. Never install it on sensitive corporate networks before completing the UAT/security checklist.

## Build on Windows

Requirements:
- Windows 10/11 x64
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) for building (not needed on recipient PCs when using the self-contained publish)
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) for producing a single `.exe` installer. The publish output is usable without Inno, but then firewall setup is manual/elevated.

Open `LanBridge.slnx` in Visual Studio (Windows) or build with PowerShell in this repository:

```powershell
Set-ExecutionPolicy -Scope Process Bypass
.\scripts\build.ps1
```

This runs the cross-platform core smoke tests, builds a self-contained `win-x64` desktop publish, copies `firewall.ps1`, and compiles Inno Setup if available. The expected resulting paths are:

- `artifacts/publish/LanBridge.Desktop.exe` — published desktop executable
- `artifacts/setup/LanBridge-Setup-win-x64.exe` — installer, **only after successful Inno compilation**

Manual build:

```powershell
dotnet run --project tests\LanBridge.SmokeTests\LanBridge.SmokeTests.csproj -c Release
dotnet publish src\LanBridge.Desktop\LanBridge.Desktop.csproj -c Release -r win-x64 --self-contained true -p:PublishTrimmed=false -o artifacts\publish
Copy-Item scripts\firewall.ps1 artifacts\publish\firewall.ps1
& 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe' installer\LanBridge.iss
```

The installer requests Windows UAC permission once to create **two scoped inbound rules** (TCP 47321 / UDP 47322, profile Private, remote LocalSubnet) and removes only its rules on uninstall. It never disables Defender or uses SMB. Windows enterprise policy, Public Wi-Fi and AP client isolation can still block connectivity; no legitimate installer can universally bypass those restrictions.

## First run — two or more PCs

1. Install the program on each Windows PC. Use a **Private** LAN, and approve Windows UAC if prompted.
2. On PC A, open **Thiết bị & Nhóm** and select **Tạo nhóm mới**.
3. On PC B, find A in the discovery list (or use the optional IP discovery probe); select A and click **Xin tham gia**.
4. Both PCs display a **6-digit verification code**. Compare the code directly on the two screens. On PC A, select the pending request and click **Duyệt** only if they match.
5. Repeat for C/D/etc. In **Thành viên trong nhóm**, use **Cấp Admin** to allow more than one Admin to approve newcomers even when PC A is off.
6. In **Cài đặt**, choose the receiving folder. Default: `%USERPROFILE%\Downloads\LanBridge`.

## Daily usage

- On **any paired PC**, select text and press `Ctrl+Shift+C`. The app simulates Ctrl+C, waits for a *new* text clipboard value, and shares it with group members over authenticated TLS.
- On **any paired PC**, focus an ordinary text field and press `Ctrl+Shift+V`. It writes the latest shared text into the local Windows clipboard and simulates Ctrl+V. It does not silently copy all regular Ctrl+C operations.
- For files, drag one or more files onto LanBridge, select the online recipients (or **Chọn tất cả**), click **Gửi**. Recipients save the file to their configured folder without prompts. Distinct files have per-recipient progress; SHA-256 verification and restartable `.part` transfer offsets are implemented.
- Close the window to minimize it to the System Tray; right-click its icon to open/exit.
- Change hotkeys in Settings if already used by another program. Windows prevents an unelevated app from injecting keystrokes into elevated Administrator windows; such copy/paste is not supported.

## Code layout

```text
src/
  LanBridge.Core/               # signed membership events, crypto identity, wire framing, models
  LanBridge.Desktop/            # WPF UI, tray, Windows hotkeys, discovery, mutual TLS, transfers
installer/
  LanBridge.iss                 # elevated all-users setup + uninstall cleanup
scripts/
  build.ps1                     # smoke test + publish + installer
  firewall.ps1                  # private/local-subnet firewall rules
  verify-source.py              # basic source/XML structural checks (not a compiler)
tests/
  LanBridge.SmokeTests/         # certificate, signing, membership, clipboard, framing checks
```

### Architecture and security notes

- Each Windows user profile creates a P-256 ECDSA certificate/key. The private PFX is **DPAPI-protected** under the Windows user profile, never transmitted. The device fingerprint derives from public key SPKI.
- Each TLS connection requires a client certificate. Discovered identities alone are **not** sufficient for authorization: the presented certificate must match a currently active device in the locally verified group log. Joining is a separate restricted command that requires on-screen 6-digit comparison and Admin approval.
- Group changes are signed (`Created`, `Joined`, `RoleChanged`, `Revoked`); peers periodically exchange and revalidate membership event logs. Events/transfer metadata/settings are stored as atomic local JSON, **not SQLite** in this implementation. Clipboard content is never written to disk; only its Lamport counter is persisted.
- The latest clipboard item is held only in RAM (30-minute TTL, 2-MiB limit). Updates converge deterministically by Lamport clock + device fingerprint tie breaker. A device offline longer than TTL won't receive old text.
- File frames and filenames have bounds checking. Receivers verify the full SHA-256 hash prior to revealing the finished file. `.part` files are kept for resume. Completion receipts make retried transfer IDs idempotent. Files never auto-execute.
- Each sender has two concurrent transfer slots. Interrupted transfers are queued on the **sender**, and are retried when the known recipient is online again; this **does not** allow creating a new transfer for an offline recipient.
- A removed peer can remain trusted by temporarily disconnected PCs until those PCs sync the signed revocation. There is no global immediate revocation guarantee in a serverless LAN.
- The UI is kept deliberately simple; no network shares, no Windows Service, no Internet relay.

## Known issues / required UAT before release

1. **Compilation has not been run in this environment.** Run the supplied Windows build and fix any SDK/API/GUI issues exposed by it.
2. Mutual-TLS certificate selection, discovery over each network adapter, Windows firewall setup/cleanup and hotkey foreground behavior require real Windows testing.
3. Test with 2, 5 and 10 PCs, including an Admin-offline join, simultaneous Admin role events, and revoked peer after resync.
4. Test actual interrupted/resumed transfers (including multi-gigabyte files); file source modification, duplicate filename, parallel sends, zero-byte files, and lost final acknowledgements.
5. Threat model is personally trusted LAN users; there is no defense against a malicious Windows Administrator on an already authorized endpoint, nor true revocation during disconnected network partitions. A third-party security audit is advised before using for work documents.
6. Current per-user settings and event records are stored in `%LOCALAPPDATA%\LanBridge` via atomic JSON files, rather than the originally planned SQLite. This is intentionally lightweight for the small-group V1 and can be migrated later.
7. No code signing is included. Unsigned installers may show Windows SmartScreen warnings. Code signing should be added before distributing beyond personal PCs.

See `UAT.md` for the Windows validation matrix and `LanBridge_V1_Implementation_Plan.md` for the original functional plan.

## GitHub Actions build (optional)

If you put these source files in your own GitHub repository, `.github/workflows/windows-build.yml` builds on Windows, runs the core tests, publishes a self-contained app and compiles an Inno Setup installer. **The workflow has not yet been executed.** Find artifacts in the Actions run after it succeeds.

## Publish the source to a new PUBLIC GitHub repository

This project can be published to **`namnp19/LanBridge`** using the included helper script. The project was prepared locally but **has not yet been uploaded**. Ensure the source is reviewed and meets your disclosure requirements before publishing. No license is granted by including the source in a public GitHub repository; add a `LICENSE` later if you want to allow reuse.

On Windows, install Git and the GitHub CLI, and authenticate with your GitHub account:

```powershell
winget install --id Git.Git -e
winget install --id GitHub.cli -e
# Restart your terminal so Git and gh are on PATH.
gh auth login
.\scripts\publish-github.ps1
```

The script checks that you are logged in as **namnp19**, refuses to overwrite an existing repository, and runs `gh repo create --public --source . --remote origin --push`. After it completes, verify <https://github.com/namnp19/LanBridge> is marked **Public**. GitHub Actions will run the Windows build automatically after the push; check its result before distributing the installer.