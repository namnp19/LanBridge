using LanBridge.Core;

namespace LanBridge.Desktop;

internal sealed partial class LanBridgeEngine
{
    public async Task<string> RequestJoinAsync(Peer admin)
    {
        if (HasGroup) throw new InvalidOperationException("Máy đã thuộc một nhóm");
        if (string.IsNullOrWhiteSpace(admin.GroupId)) throw new InvalidOperationException("Máy đích không thuộc nhóm");
        var request = new JoinRequest
        {
            JoinId = Guid.NewGuid().ToString("N"), GroupId = admin.GroupId, DisplayName = Settings.DisplayName
        };
        var code = DeviceIdentity.PairCode(admin.GroupId, admin.DeviceId, LocalId, request.JoinId);
        lock (_gate) _outgoingJoin = new PendingJoin
        {
            JoinId = request.JoinId, GroupId = admin.GroupId, DeviceId = LocalId, AdminId = admin.DeviceId,
            DisplayName = Settings.DisplayName, Address = admin.Address, Code = code
        };
        try
        {
            var response = await RequestAsync(admin, "JoinRequest", request, _shutdown.Token);
            if (response.Kind != "Reply" || response.Read<TransferResult>().Success == false)
                throw new InvalidOperationException("Máy đích không nhận yêu cầu tham gia");
            Inform("Đã gửi yêu cầu. Mã ghép cặp: " + code);
            return code;
        }
        catch
        {
            lock (_gate) _outgoingJoin = null;
            throw;
        }
    }

    private TransferResult HandleJoinRequest(JoinRequest request, string requesterId, string address)
    {
        lock (_gate)
        {
            if (_group == null || !IsAdmin || request.GroupId != _group.Document.GroupId ||
                request.JoinId.Length != 32 || request.DisplayName.Length > 120 || request.DisplayName.Length == 0)
                return new TransferResult { Message = "Không phải Admin của nhóm", Success = false };
            if (_group.IsActive(requesterId))
                return new TransferResult { Message = "Đã là thành viên", Success = false };
            var pending = new PendingJoin
            {
                JoinId = request.JoinId, GroupId = request.GroupId, DeviceId = requesterId, Address = address,
                DisplayName = request.DisplayName, AdminId = LocalId,
                Code = DeviceIdentity.PairCode(request.GroupId, LocalId, requesterId, request.JoinId)
            };
            _joinRequests[request.JoinId] = pending;
        }
        Inform("Có yêu cầu tham gia nhóm từ " + request.DisplayName);
        return new TransferResult { Success = true, Message = "Pending" };
    }

    public async Task ApproveAsync(PendingJoin request)
    {
        if (!IsAdmin) throw new UnauthorizedAccessException("Chỉ Admin được duyệt");
        if (!_joinRequests.TryGetValue(request.JoinId, out var approved) || approved.DeviceId != request.DeviceId)
            throw new InvalidOperationException("Yêu cầu không tồn tại hoặc đã hết hạn");
        if (DateTime.UtcNow - approved.CreatedUtc > TimeSpan.FromMinutes(10))
        {
            _joinRequests.TryRemove(request.JoinId, out _);
            throw new InvalidOperationException("Yêu cầu ghép cặp hết hạn, hãy gửi lại");
        }
        var peer = Peers.FirstOrDefault(x => x.DeviceId == approved.DeviceId && x.IsOnline)
            ?? throw new IOException("Máy xin gia nhập đang offline");
        // This extra outbound mutual TLS handshake authenticates the joining device's
        // certificate and binds its public key to the signed membership event.
        var (tcp, ssl) = await OpenClientAsync(peer, _shutdown.Token);
        using (tcp) using (ssl)
        {
            using var certificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(
                ssl.RemoteCertificate ?? throw new System.Security.Authentication.AuthenticationException("Missing peer certificate"));
            if (DeviceIdentity.IdentityOf(certificate) != approved.DeviceId)
                throw new System.Security.Authentication.AuthenticationException("Identity changed during pairing");
            GroupDocument snapshot;
            lock (_gate)
            {
                _group!.Issue("Joined", approved.DeviceId,
                    Convert.ToBase64String(certificate.Export(System.Security.Cryptography.X509Certificates.X509ContentType.Cert)),
                    "Member", Identity, approved.DisplayName);
                _store.SaveGroup(_group.Document);
                snapshot = CopyGroup()!;
            }
            var command = new JoinApproval { JoinId = approved.JoinId, Group = snapshot };
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await WireIO.SendAsync(ssl, WireEnvelope.Create(LocalId, "JoinApproved", command), timeout.Token);
            var result = await WireIO.ReceiveAsync(ssl, timeout.Token);
            if (result.Kind != "Reply" || !result.Read<TransferResult>().Success)
                throw new IOException("Thiết bị chưa xác nhận việc tham gia nhóm");
        }
        _joinRequests.TryRemove(approved.JoinId, out _);
        Inform("Đã duyệt thiết bị " + approved.DisplayName);
    }

    private TransferResult HandleJoinApproved(JoinApproval approval, string adminId)
    {
        try
        {
            lock (_gate)
            {
                if (_group != null || _outgoingJoin == null ||
                    _outgoingJoin.JoinId != approval.JoinId || _outgoingJoin.AdminId != adminId ||
                    approval.Group.GroupId != _outgoingJoin.GroupId)
                    return new TransferResult { Success = false, Message = "Không đúng phiên ghép cặp đang chờ" };
                var incoming = GroupLedger.Import(approval.Group);
                if (!incoming.IsAdmin(adminId) || !incoming.IsActive(LocalId))
                    return new TransferResult { Success = false, Message = "Nhóm không có chữ ký duyệt hợp lệ" };
                _group = incoming;
                _outgoingJoin = null;
                _store.SaveGroup(_group.Document);
            }
            Inform("Tham gia nhóm thành công: " + approval.Group.Name);
            return new TransferResult { Success = true };
        }
        catch (Exception ex) { return new TransferResult { Success = false, Message = ex.Message }; }
    }

    public void Reject(PendingJoin request)
    {
        _joinRequests.TryRemove(request.JoinId, out _);
        Changed?.Invoke();
    }

    public void ChangeMember(string deviceId, string action)
    {
        lock (_gate)
        {
            if (_group == null || !_group.IsAdmin(LocalId))
                throw new UnauthorizedAccessException("Admin required");
            if (!_group.Members.TryGetValue(deviceId, out var target) || !target.Active)
                throw new InvalidOperationException("Member unavailable");
            if (action == "Revoke") _group.Issue("Revoked", deviceId, "", "", Identity);
            else if (action == "Promote") _group.Issue("RoleChanged", deviceId, "", "Admin", Identity);
            else if (action == "Demote") _group.Issue("RoleChanged", deviceId, "", "Member", Identity);
            else throw new ArgumentException("Invalid member action");
            _store.SaveGroup(_group.Document);
        }
        Inform("Cập nhật quyền thiết bị " + deviceId[..8]);
    }
}
