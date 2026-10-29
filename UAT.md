# LanBridge V1 — Windows acceptance checklist

**Status:** NOT RUN. This is the mandatory validation matrix for Windows 10/11 x64. Do not mark any box as passed without executing it on real machines.

## Build and installation

- [ ] Install .NET 10 SDK and Inno Setup 6 on a Windows build PC.
- [ ] Run `scripts/build.ps1`; smoke tests pass and WPF win-x64 publish compiles.
- [ ] `artifacts/setup/LanBridge-Setup-win-x64.exe` exists and installer runs with normal UAC confirmation.
- [ ] Fresh install works on a Windows user account without the .NET runtime.
- [ ] Verify **only** LanBridge TCP/47321 + UDP/47322 inbound rules, **Private + LocalSubnet**.
- [ ] Reinstall/upgrade keeps current-user group membership and device key.
- [ ] Uninstall removes the two LanBridge firewall rules but does not touch third-party rules.
- [ ] Windows firewall enabled; no File and Printer Sharing/SMB needed.
- [ ] Windows Public profile or corporate policy blocking rules results in a clear connection failure and does not disable Defender.

## Discovery, joining and authorization

- [ ] Two PCs on private LAN discover each other via UDP without typing an IP address.
- [ ] In case of blocked UDP broadcast, unicast IPv4 probe discovers the peer.
- [ ] Joining PC sees a six-digit code; Admin sees the **same** code for that request.
- [ ] Admin rejects a join request; requester has no access to clipboard or files.
- [ ] Admin approves matching request; requester becomes member.
- [ ] Promote another member to Admin; shut down creator; remaining Admin approves another PC.
- [ ] Demote a nonlast Admin; both machines converge after signed synchronization.
- [ ] Last Admin cannot remove/demote self without another Admin.
- [ ] Revoke a member; after sync, revoked member cannot connect or read new clipboard / receive new files.
- [ ] Offline PC wakes up, syncs signed group events, and enforces revocation.
- [ ] Two Admins issue simultaneous changes: verify deterministic convergence rather than permanent sync failure.
- [ ] Invalid/mutated signed group events are rejected without granting access.
- [ ] Machine-name spoofing, discovery packet spoofing, and bad peer certificate cannot bypass membership checks.

## Clipboard

- [ ] PC A select text in Notepad → `Ctrl+Shift+C` → PC B/C `Ctrl+Shift+V` pastes the same text.
- [ ] PC C copy → PC A/B paste; all group members share the latest event.
- [ ] Vietnamese accented text, emoji, multiline, URL, whitespace preservation.
- [ ] Regular Ctrl+C alone does not transmit to other PCs.
- [ ] No selected text / unsupported text clipboard does not transmit stale previous clipboard.
- [ ] Application running elevated cannot be controlled from unelevated LanBridge; error is visible.
- [ ] Test simultaneous copying at two machines; logical clock tie is consistent after sync.
- [ ] Test event duplication/out-of-order delivery, reconnection and 30-minute TTL expiry.
- [ ] Test 2 MiB limit and clipboard locked by another process.
- [ ] Test Ctrl+Shift+C conflict with other software; changing hotkeys works.
- [ ] Check log/config files: there is **no clipboard plaintext** on disk.

## File transfer

- [ ] Drag and drop multiple files into PC A; select B/C; Select All chooses only online members.
- [ ] Offline D cannot be selected for a new transfer.
- [ ] B and C receive the files automatically in their individually configured folders.
- [ ] Unicode filenames, same-name collision, empty files, multi-gigabyte files.
- [ ] Data matches `Get-FileHash -Algorithm SHA256` on each side.
- [ ] Disconnect B's Wi-Fi mid-transfer while C stays online; C finishes and B resumes when online.
- [ ] Restart the sending application mid-transfer; pending outgoing queue resumes on startup.
- [ ] Reconnect when final acknowledgement was lost; completion receipt prevents duplicate final file.
- [ ] Corrupted or truncated parts never become visible as finalized files.
- [ ] Sender source file changed since selection: transfer fails safely.
- [ ] Receiver disk full/unwritable: sender sees failure; unrelated recipients still work.
- [ ] Malware/EXE file is not automatically opened or executed.
- [ ] Offer filename path traversal (`../`, `..\\`, drive letter), reserved names and malformed transfer ID are rejected.
- [ ] Cancel transfer from UI; no further new transfer is initiated.

## Lifecycle and load

- [ ] Close window → app remains in System Tray; open from tray; Exit really exits.
- [ ] Login autostart launches minimized; toggling autostart updates HKCU Run.
- [ ] IP changes via DHCP; devices rediscover and resume without manual IP configuration.
- [ ] Perform tests with 2, 5, 10 PCs; simulate 20 peers; monitor memory and UI responsiveness.
- [ ] Keep 5+ PCs connected for at least 2 hours; no unbounded CPU/socket/task growth.
- [ ] New instance started while one is running is refused via mutex.