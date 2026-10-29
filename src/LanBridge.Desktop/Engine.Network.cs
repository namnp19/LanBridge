using System.Net;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using LanBridge.Core;

namespace LanBridge.Desktop;

internal sealed partial class LanBridgeEngine
{
    private static bool ValidatePresentedCertificate(object sender, X509Certificate? presented,
        X509Chain? chain, SslPolicyErrors errors)
    {
        if (presented is null) return false;
        using var cert = presented as X509Certificate2 ?? new X509Certificate2(presented);
        // Self-signed certificate is only transport identity; application must pin it
        // to a signed group member or a manually approved join request before data access.
        return DeviceIdentity.IsPlausible(cert);
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? client = null;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(ct);
                var accepted = client;
                if (!_incomingSlots.Wait(0)) { accepted.Dispose(); continue; }
                _ = Task.Run(async () =>
                {
                    using (accepted)
                    {
                        try { await HandleIncomingAsync(accepted, ct); }
                        catch (OperationCanceledException) { }
                        catch (Exception ex) { Message?.Invoke("Incoming connection: " + ex.Message); }
                        finally { _incomingSlots.Release(); }
                    }
                });
            }
            catch (OperationCanceledException) { client?.Dispose(); break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { client?.Dispose(); Message?.Invoke("Listener: " + ex.Message); }
        }
    }

    private async Task HandleIncomingAsync(TcpClient tcp, CancellationToken ct)
    {
        using var ssl = new SslStream(tcp.GetStream(), false);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
        {
            ServerCertificate = Identity.Certificate,
            ClientCertificateRequired = true,
            RemoteCertificateValidationCallback = ValidatePresentedCertificate,
            EnabledSslProtocols = SslProtocols.Tls13
        }, timeout.Token);
        using var presented = new X509Certificate2(ssl.RemoteCertificate
            ?? throw new AuthenticationException("Client certificate required"));
        var peerId = DeviceIdentity.IdentityOf(presented);
        var peerCert = Convert.ToBase64String(presented.Export(X509ContentType.Cert));
        var frame = await WireIO.ReceiveAsync(ssl, timeout.Token);
        if (frame.SenderId != peerId) throw new AuthenticationException("Sender identity mismatch");
        var ip = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address.ToString();

        if (frame.Kind == "JoinRequest")
        {
            var result = HandleJoinRequest(frame.Read<JoinRequest>(), peerId, ip);
            await WireIO.SendAsync(ssl, WireEnvelope.Create(LocalId, "Reply", result), ct);
            return;
        }
        if (frame.Kind == "JoinApproved")
        {
            var result = HandleJoinApproved(frame.Read<JoinApproval>(), peerId);
            await WireIO.SendAsync(ssl, WireEnvelope.Create(LocalId, "Reply", result), ct);
            return;
        }
        if (!IsMember(peerId))
        {
            await WireIO.SendAsync(ssl, WireEnvelope.Create(LocalId, "Error", "Thiết bị chưa được cấp quyền"), ct);
            return;
        }
        // Verify the exact key pinned to the signed group membership, not only its device ID.
        lock (_gate)
        {
            var member = _group!.Members[peerId];
            if (!string.Equals(member.CertificateBase64, peerCert, StringComparison.Ordinal))
                throw new AuthenticationException("Pinned certificate mismatch");
        }
        switch (frame.Kind)
        {
            case "Sync":
            {
                var incoming = frame.Read<SyncSnapshot>();
                MergeGroup(incoming.Group);
                if (incoming.Clipboard is { } candidate && IsMember(candidate.OriginDeviceId))
                    UpdateClipboard(candidate, true);
                var snapshot = new SyncSnapshot { Group = CopyGroup(), Clipboard = GetClipboardForSync() };
                await WireIO.SendAsync(ssl, WireEnvelope.Create(LocalId, "SyncReply", snapshot), ct);
                break;
            }
            case "Clipboard":
            {
                var item = frame.Read<ClipboardItem>();
                if (item.OriginDeviceId != peerId) throw new AuthenticationException("Spoofed clipboard origin");
                UpdateClipboard(item, true);
                await WireIO.SendAsync(ssl, WireEnvelope.Create(LocalId, "Reply", new { ok = true }), ct);
                break;
            }
            case "FileOffer":
                await ReceiveFileAsync(ssl, frame.Read<FileOffer>(), peerId, ct);
                break;
            default:
                await WireIO.SendAsync(ssl, WireEnvelope.Create(LocalId, "Error", "Unknown command"), ct);
                break;
        }
    }

    private ClipboardItem? GetClipboardForSync()
    {
        lock (_gate) return Settings.ClipboardEnabled && _clipboard is { IsAlive: true } ? _clipboard : null;
    }

    private async Task<(TcpClient Tcp, SslStream Tls)> OpenClientAsync(Peer peer, CancellationToken ct)
    {
        var tcp = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(9));
            await tcp.ConnectAsync(IPAddress.Parse(peer.Address), peer.Port, timeout.Token);
            var ssl = new SslStream(tcp.GetStream(), false);
            try
            {
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "LanBridge",
                    ClientCertificates = new X509CertificateCollection { Identity.Certificate },
                    RemoteCertificateValidationCallback = ValidatePresentedCertificate,
                    EnabledSslProtocols = SslProtocols.Tls13
                }, timeout.Token);
                using var presented = new X509Certificate2(ssl.RemoteCertificate
                    ?? throw new AuthenticationException("Server certificate missing"));
                if (DeviceIdentity.IdentityOf(presented) != peer.DeviceId)
                    throw new AuthenticationException("Peer certificate does not match discovered identity");
                lock (_gate)
                {
                    if (_group != null && _group.Members.TryGetValue(peer.DeviceId, out var member) &&
                        member.Active && member.CertificateBase64 != Convert.ToBase64String(presented.Export(X509ContentType.Cert)))
                        throw new AuthenticationException("Pinned certificate mismatch");
                }
                return (tcp, ssl);
            }
            catch { ssl.Dispose(); throw; }
        }
        catch { tcp.Dispose(); throw; }
    }

    private async Task<WireEnvelope> RequestAsync<T>(Peer peer, string kind, T payload, CancellationToken ct)
    {
        var (tcp, ssl) = await OpenClientAsync(peer, ct);
        using (tcp) using (ssl)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await WireIO.SendAsync(ssl, WireEnvelope.Create(LocalId, kind, payload), timeout.Token);
            var response = await WireIO.ReceiveAsync(ssl, timeout.Token);
            if (response.SenderId != peer.DeviceId) throw new AuthenticationException("Reply identity mismatch");
            if (response.Kind == "Error") throw new IOException(response.Read<string>());
            return response;
        }
    }

    private async Task SyncWithAsync(Peer peer, CancellationToken ct)
    {
        var packet = new SyncSnapshot { Group = CopyGroup(), Clipboard = GetClipboardForSync() };
        var response = await RequestAsync(peer, "Sync", packet, ct);
        if (response.Kind != "SyncReply") return;
        var payload = response.Read<SyncSnapshot>();
        var updated = MergeGroup(payload.Group);
        if (payload.Clipboard is { } item && IsMember(item.OriginDeviceId))
            UpdateClipboard(item, true);
        if (updated) Inform($"Đã đồng bộ thành viên từ {peer.DisplayName}");
    }
}
