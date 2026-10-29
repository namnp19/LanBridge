using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;
using LanBridge.Core;

namespace LanBridge.Desktop;

internal sealed class LanDiscovery : IAsyncDisposable
{
    private UdpClient? _socket;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<DiscoveryPacket> _current;
    public event Action<Peer>? Found;
    public LanDiscovery(Func<DiscoveryPacket> current) => _current = current;
    public void Start()
    {
        _socket = new UdpClient(AddressFamily.InterNetwork);
        _socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _socket.Client.Bind(new IPEndPoint(IPAddress.Any, Constants.UdpPort));
        _socket.EnableBroadcast = true;
        _ = Listen(_cts.Token);
        _ = Announce(_cts.Token);
    }
    public async Task ProbeAsync(string ip)
    {
        if (_socket is null) return;
        if (!IPAddress.TryParse(ip, out var address) || address.AddressFamily != AddressFamily.InterNetwork)
            throw new ArgumentException("Please enter an IPv4 address");
        await SendAsync(new IPEndPoint(address, Constants.UdpPort), "probe");
    }
    private async Task SendAsync(IPEndPoint endpoint, string kind)
    {
        var socket = _socket;
        if (socket is null) return;
        var p = _current();
        p.Kind = kind;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(p, Constants.Json);
        if (bytes.Length > 1024) return;
        await socket.SendAsync(bytes, endpoint);
    }
    private static IEnumerable<IPAddress> BroadcastAddresses()
    {
        yield return IPAddress.Broadcast;
        foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (iface.OperationalStatus != OperationalStatus.Up ||
                iface.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) continue;
            foreach (var addr in iface.GetIPProperties().UnicastAddresses)
            {
                if (addr.Address.AddressFamily != AddressFamily.InterNetwork || addr.IPv4Mask is null) continue;
                var ip = addr.Address.GetAddressBytes(); var mask = addr.IPv4Mask.GetAddressBytes();
                var b = new byte[4];
                for (var i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(b);
            }
        }
    }
    private async Task Announce(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                foreach (var ip in BroadcastAddresses().Distinct())
                    await SendAsync(new IPEndPoint(ip, Constants.UdpPort), "announce");
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); } catch (OperationCanceledException) { break; }
        }
    }
    private async Task Listen(CancellationToken ct)
    {
        var socket = _socket!;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = await socket.ReceiveAsync(ct);
                if (result.Buffer.Length > 1024) continue;
                var p = JsonSerializer.Deserialize<DiscoveryPacket>(result.Buffer, Constants.Json);
                if (p is null || p.Version != Constants.ProtocolVersion ||
                    p.DeviceId.Length != 64 || p.DisplayName.Length > 120 || p.GroupId.Length > 64 ||
                    p.Port != Constants.TcpPort || p.Kind is not ("announce" or "probe")) continue;
                Found?.Invoke(new Peer
                {
                    DeviceId = p.DeviceId, DisplayName = p.DisplayName, Address = result.RemoteEndPoint.Address.ToString(),
                    GroupId = p.GroupId, GroupName = p.GroupName, IsAdminAdvertised = p.IsAdmin,
                    Port = p.Port, LastSeenUtc = DateTime.UtcNow
                });
                if (p.Kind == "probe") await SendAsync(result.RemoteEndPoint, "announce");
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) when (ex is JsonException or SocketException or IOException) { }
        }
    }
    public ValueTask DisposeAsync()
    {
        _cts.Cancel(); _socket?.Dispose(); _cts.Dispose();
        return ValueTask.CompletedTask;
    }
}
