using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using LanBridge.Core;

namespace LanBridge.Desktop;

internal sealed partial class LanBridgeEngine : IAsyncDisposable
{
    private readonly LocalStore _store = new();
    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, Peer> _peers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingJoin> _joinRequests = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _shutdown = new();
    private readonly ConcurrentDictionary<string, byte> _activeTransfers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _sendSlots = new(2);
    private readonly SemaphoreSlim _incomingSlots = new(32);
    private DateTime _lastPartsCleanupUtc = DateTime.MinValue;
    private readonly List<TransferJob> _jobs;
    private LanDiscovery? _discovery;
    private TcpListener? _listener;
    private PendingJoin? _outgoingJoin;
    private GroupLedger? _group;
    private ClipboardItem? _clipboard;
    private long _clipboardClock;
    private bool _disposed;

    public DeviceIdentity Identity { get; }
    public LocalSettings Settings { get; }
    public string LocalId => Identity.DeviceId;
    public string GroupLabel { get { lock (_gate) return _group?.Document.Name ?? "Chưa tham gia nhóm"; } }
    public bool HasGroup { get { lock (_gate) return _group != null; } }
    public bool IsAdmin { get { lock (_gate) return _group?.IsAdmin(LocalId) == true; } }
    public string ClipboardLabel { get { lock (_gate) return _clipboard is { IsAlive: true } c
        ? $"Nội dung mới nhất từ {c.OriginDeviceId[..8]}, {c.PublishedUtc.ToLocalTime():HH:mm:ss}" : "Chưa có clipboard chung"; } }
    public string PendingJoinCode { get { lock (_gate) return _outgoingJoin?.Code ?? ""; } }
    public event Action? Changed;
    public event Action<string>? Message;
    public event Action<string>? ReceivedFile;

    public LanBridgeEngine()
    {
        Identity = DeviceIdentity.LoadOrCreate(_store.DirectoryPath);
        Settings = _store.LoadSettings();
        _clipboardClock = _store.LoadClipboardClock();
        var existing = _store.LoadGroup();
        if (existing != null) _group = GroupLedger.Import(existing);
        _jobs = _store.LoadJobs();
        foreach (var job in _jobs.Where(x => x.Status is "Transferring" or "Hashing"))
            job.Status = "Interrupted";
    }

    public IReadOnlyList<Peer> Peers => _peers.Values.OrderBy(x => x.DisplayName).ToList();
    public IReadOnlyList<PendingJoin> PendingRequests => _joinRequests.Values.OrderBy(x => x.CreatedUtc).ToList();
    public IReadOnlyList<TransferJob> Jobs { get { lock (_gate) return _jobs.Select(j => new TransferJob
    { TransferId = j.TransferId, PeerId = j.PeerId, SourcePath = j.SourcePath,
      Size = j.Size, ModifiedUtcTicks = j.ModifiedUtcTicks, Sha256 = j.Sha256,
      Status = j.Status, Error = j.Error, BytesSent = j.BytesSent,
      RetryCount = j.RetryCount, NextAttemptUtc = j.NextAttemptUtc }).ToList(); } }
    public IReadOnlyList<Member> Members { get { lock (_gate) return _group?.Members.Values.OrderBy(m => m.DisplayName).ToList() ?? []; } }
    public IReadOnlyList<Peer> OnlineMembers => Peers.Where(p => p.IsOnline && IsMember(p.DeviceId)).ToList();
    public bool IsMember(string id) { lock (_gate) return _group?.IsActive(id) == true; }
    private GroupDocument? CopyGroup()
    {
        lock (_gate)
        {
            if (_group is null) return null;
            // Avoid sharing a mutable event list across async network handlers.
            return new GroupDocument { GroupId = _group.Document.GroupId, Name = _group.Document.Name,
                RootDeviceId = _group.Document.RootDeviceId,
                Events = _group.Document.Events.ToList() };
        }
    }
    private void Inform(string msg) { Message?.Invoke(msg); Changed?.Invoke(); }

    public Task StartAsync()
    {
        _listener = new TcpListener(IPAddress.Any, Constants.TcpPort);
        _listener.Start(64);
        _ = ListenAsync(_shutdown.Token);
        _discovery = new LanDiscovery(() =>
        {
            lock (_gate)
            {
                return new DiscoveryPacket { DeviceId = LocalId, DisplayName = Settings.DisplayName,
                    GroupId = _group?.Document.GroupId ?? "", GroupName = _group?.Document.Name ?? "",
                    IsAdmin = _group?.IsAdmin(LocalId) == true };
            }
        });
        _discovery.Found += peer =>
        {
            if (peer.DeviceId == LocalId) return;
            _peers.AddOrUpdate(peer.DeviceId, peer, (_, old) => peer);
            Changed?.Invoke();
        };
        _discovery.Start();
        _ = PeriodicAsync(_shutdown.Token);
        Inform("LanBridge đang chạy trong mạng LAN.");
        return Task.CompletedTask;
    }

    public async Task ProbeIpAsync(string address)
    {
        if (_discovery == null) throw new InvalidOperationException("Discovery not started");
        await _discovery.ProbeAsync(address);
    }

    public void ChangeSettings(string folder, string copyHotkey, string pasteHotkey, bool clipboardEnabled)
    {
        if (string.IsNullOrWhiteSpace(folder)) throw new ArgumentException("Thư mục nhận không hợp lệ");
        Directory.CreateDirectory(folder);
        Settings.ReceiveFolder = Path.GetFullPath(folder);
        Settings.CopyHotkey = copyHotkey;
        Settings.PasteHotkey = pasteHotkey;
        Settings.ClipboardEnabled = clipboardEnabled;
        _store.SaveSettings(Settings);
        Changed?.Invoke();
    }

    public void LeaveGroup()
    {
        lock (_gate)
        {
            if (_group == null) return;
            _group = null;
            _outgoingJoin = null;
            _clipboard = null;
            foreach (var job in _jobs.Where(x => x.Status is not ("Completed" or "Canceled")))
                job.Status = "Canceled";
            _store.SaveJobs(_jobs);
            _store.LeaveGroup();
        }
        Inform("Đã rời nhóm trên máy này. Các PC khác cần đồng bộ thu hồi quyền nếu muốn xóa hoàn toàn.");
    }

    public void CreateGroup(string name)
    {
        lock (_gate)
        {
            if (_group != null) throw new InvalidOperationException("Đã thuộc một nhóm");
            _group = GroupLedger.Create(name, Identity);
            _store.SaveGroup(_group.Document);
        }
        Inform("Đã tạo nhóm " + name);
    }

    private bool MergeGroup(GroupDocument? incoming)
    {
        if (incoming is null) return false;
        lock (_gate)
        {
            if (_group is null) return false;
            var updated = _group.Merge(incoming);
            if (updated) _store.SaveGroup(_group.Document);
            return updated;
        }
    }

    private void UpdateClipboard(ClipboardItem item, bool validatedSender)
    {
        if (!validatedSender || !Settings.ClipboardEnabled || item.Text is { } t &&
            System.Text.Encoding.UTF8.GetByteCount(t) > Constants.MaxClipboardBytes) return;
        lock (_gate)
        {
            if (item.Clock > _clipboardClock)
            {
                _clipboardClock = item.Clock;
                _store.SaveClipboardClock(_clipboardClock);
            }
            if (!ClipboardOrder.IsNewer(item, _clipboard)) return;
            _clipboard = item;
        }
        Changed?.Invoke();
    }
    public string? CurrentSharedClipboard
    {
        get { lock (_gate) return _clipboard is { IsAlive: true } ? _clipboard.Text : null; }
    }

    public async Task PublishClipboardAsync(string? text)
    {
        if (!Settings.ClipboardEnabled) throw new InvalidOperationException("Clipboard sharing is disabled");
        if (text is not null && System.Text.Encoding.UTF8.GetByteCount(text) > Constants.MaxClipboardBytes)
            throw new InvalidOperationException("Clipboard quá 2 MiB");
        ClipboardItem item;
        lock (_gate)
        {
            if (_group is null) throw new InvalidOperationException("Chưa tham gia nhóm");
            item = new ClipboardItem { OriginDeviceId = LocalId, Clock = ++_clipboardClock, Text = text };
            _store.SaveClipboardClock(_clipboardClock);
            _clipboard = item;
        }
        Changed?.Invoke();
        var targets = OnlineMembers;
        await Task.WhenAll(targets.Select(async peer =>
        {
            try { await RequestAsync(peer, "Clipboard", item, _shutdown.Token); }
            catch (Exception ex) { Message?.Invoke($"Không gửi clipboard tới {peer.DisplayName}: {ex.Message}"); }
        }));
    }

    private async Task PeriodicAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Bound network work; peer failures do not stall healthy peers.
                var targets = OnlineMembers;
                await Task.WhenAll(targets.Select(async peer =>
                {
                    try { await SyncWithAsync(peer, ct); }
                    catch (Exception) { /* next heartbeat will retry */ }
                }));
                PumpJobs(ct);
                if (DateTime.UtcNow - _lastPartsCleanupUtc > TimeSpan.FromHours(1))
                {
                    _lastPartsCleanupUtc = DateTime.UtcNow;
                    _ = Task.Run(CleanupPartialFiles, ct);
                }
                Changed?.Invoke();
            }
            catch (Exception ex) { Message?.Invoke("Background sync: " + ex.Message); }
            try { await Task.Delay(TimeSpan.FromSeconds(7), ct); } catch (OperationCanceledException) { break; }
        }
    }

    private void CleanupPartialFiles()
    {
        try
        {
            var folder = Path.Combine(Settings.ReceiveFolder, ".lanbridge-parts");
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                if ((!file.EndsWith(".part", StringComparison.OrdinalIgnoreCase) ||
                     File.GetLastWriteTimeUtc(file) >= DateTime.UtcNow.AddHours(-24)) &&
                    (!file.EndsWith(".done.json", StringComparison.OrdinalIgnoreCase) ||
                     File.GetLastWriteTimeUtc(file) >= DateTime.UtcNow.AddDays(-30))) continue;
                try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
        catch (Exception ex) { Message?.Invoke("File cleanup: " + ex.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _shutdown.Cancel();
        _listener?.Stop();
        if (_discovery != null) await _discovery.DisposeAsync();
        lock (_gate) _store.SaveJobs(_jobs);
        Identity.Dispose();
        // In-flight tasks observe cancellation. Avoid disposing semaphores under workers.
        _shutdown.Dispose();
    }
}
