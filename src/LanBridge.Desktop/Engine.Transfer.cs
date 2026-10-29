using System.Security.Cryptography;
using LanBridge.Core;

namespace LanBridge.Desktop;

internal sealed partial class LanBridgeEngine
{
    public void EnqueueFiles(IEnumerable<string> paths, IEnumerable<string> destinationIds)
    {
        var targets = destinationIds.Distinct().Where(id => OnlineMembers.Any(p => p.DeviceId == id)).ToList();
        var inputs = paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (targets.Count == 0 || inputs.Count == 0) throw new ArgumentException("Chọn file và ít nhất một máy online");
        var newJobs = new List<TransferJob>();
        foreach (var path in inputs)
        {
            if (!File.Exists(path)) throw new FileNotFoundException("Không tìm thấy file nguồn", path);
            var info = new FileInfo(path);
            if (info.Length > Constants.MaxFileBytes) throw new IOException("File vượt quá giới hạn 100 GiB");
            FileSafety.SafeName(info.Name);
            foreach (var id in targets)
            {
                newJobs.Add(new TransferJob { SourcePath = info.FullName, PeerId = id, Size = info.Length,
                    ModifiedUtcTicks = info.LastWriteTimeUtc.Ticks });
            }
        }
        lock (_gate) { _jobs.AddRange(newJobs); _store.SaveJobs(_jobs); }
        Inform($"Đã đưa {newJobs.Count} lượt truyền vào hàng đợi");
        PumpJobs(_shutdown.Token);
    }

    public void CancelTransfer(string transferId)
    {
        lock (_gate)
        {
            var job = _jobs.FirstOrDefault(x => x.TransferId == transferId);
            if (job == null) return;
            job.Status = "Canceled";
            _store.SaveJobs(_jobs);
        }
        Inform("Đã hủy yêu cầu gửi file");
    }

    private void UpdateJob(TransferJob job, string status, string error = "", bool persist = true)
    {
        lock (_gate)
        {
            if (job.Status == "Canceled") return;
            job.Status = status; job.Error = error;
            if (persist) _store.SaveJobs(_jobs);
        }
        Changed?.Invoke();
    }

    private void PumpJobs(CancellationToken ct)
    {
        if (ct.IsCancellationRequested) return;
        List<TransferJob> available;
        lock (_gate) available = _jobs.Where(x => x.Status is "Queued" or "Interrupted"
                && x.NextAttemptUtc <= DateTime.UtcNow).ToList();
        foreach (var job in available)
        {
            var peer = OnlineMembers.FirstOrDefault(x => x.DeviceId == job.PeerId);
            if (peer is null || !_activeTransfers.TryAdd(job.TransferId, 0)) continue;
            _ = Task.Run(async () =>
            {
                var slotAcquired = false;
                try
                {
                    await _sendSlots.WaitAsync(ct);
                    slotAcquired = true;
                    await SendFileAsync(peer, job, ct);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    lock (_gate)
                    {
                        if (job.Status != "Canceled")
                        {
                            job.RetryCount++;
                            job.NextAttemptUtc = DateTime.UtcNow + TimeSpan.FromSeconds(Math.Min(120, 5 * Math.Pow(2, Math.Min(5, job.RetryCount))));
                            job.Error = ex.Message;
                            job.Status = ex is FileNotFoundException or InvalidDataException ? "Failed" : "Interrupted";
                            _store.SaveJobs(_jobs);
                        }
                    }
                    Inform($"Truyền file lỗi: {ex.Message}");
                }
                finally
                {
                    if (slotAcquired) _sendSlots.Release();
                    _activeTransfers.TryRemove(job.TransferId, out _);
                    Changed?.Invoke();
                }
            }, ct);
        }
    }

    private async Task SendFileAsync(Peer peer, TransferJob job, CancellationToken ct)
    {
        UpdateJob(job, "Hashing");
        var info = new FileInfo(job.SourcePath);
        if (!info.Exists || info.Length != job.Size || info.LastWriteTimeUtc.Ticks != job.ModifiedUtcTicks)
            throw new InvalidDataException("File nguồn đã thay đổi kể từ lúc chọn");
        if (string.IsNullOrEmpty(job.Sha256))
        {
            await using var readHash = new FileStream(job.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                262144, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var hash = await SHA256.HashDataAsync(readHash, ct);
            lock (_gate)
            {
                job.Sha256 = Convert.ToHexString(hash).ToLowerInvariant();
                _store.SaveJobs(_jobs);
            }
        }
        if (job.Status == "Canceled") return;
        var offer = new FileOffer
        {
            TransferId = job.TransferId, Name = info.Name, Size = job.Size,
            ModifiedUtcTicks = job.ModifiedUtcTicks, Sha256 = job.Sha256
        };
        var (tcp, ssl) = await OpenClientAsync(peer, ct);
        using (tcp) using (ssl)
        {
            using var responseTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            responseTimeout.CancelAfter(TimeSpan.FromSeconds(15));
            await WireIO.SendAsync(ssl, WireEnvelope.Create(LocalId, "FileOffer", offer), responseTimeout.Token);
            var reply = await WireIO.ReceiveAsync(ssl, responseTimeout.Token);
            if (reply.Kind != "FileAccept") throw new IOException("Máy nhận không chấp nhận yêu cầu");
            var accepted = reply.Read<FileAccept>();
            if (!accepted.Accepted) throw new IOException(accepted.Error);
            if (accepted.Offset < 0 || accepted.Offset > job.Size) throw new InvalidDataException("Receiver returned bad offset");
            UpdateJob(job, "Transferring");
            await using var source = new FileStream(job.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                262144, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (source.Length != job.Size) throw new InvalidDataException("Source file size changed");
            source.Seek(accepted.Offset, SeekOrigin.Begin);
            job.BytesSent = accepted.Offset;
            var buffer = new byte[262144];
            long remaining = job.Size - accepted.Offset;
            long lastUpdate = 0;
            while (remaining > 0)
            {
                if (job.Status == "Canceled") throw new OperationCanceledException("Transfer canceled");
                var n = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
                if (n == 0) throw new EndOfStreamException("Source file truncated");
                await ssl.WriteAsync(buffer.AsMemory(0, n), ct);
                remaining -= n;
                job.BytesSent += n;
                if (job.BytesSent - lastUpdate >= 4L * 1024 * 1024)
                {
                    lastUpdate = job.BytesSent; Changed?.Invoke();
                }
            }
            await ssl.FlushAsync(ct);
            // The receiver returns success only after it verifies SHA256 and atomically publishes the file.
            using var verifyTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            verifyTimeout.CancelAfter(TimeSpan.FromMinutes(10));
            var verified = await WireIO.ReceiveAsync(ssl, verifyTimeout.Token);
            if (verified.Kind != "FileResult" || !verified.Read<TransferResult>().Success)
                throw new IOException(verified.Kind == "FileResult" ? verified.Read<TransferResult>().Message : "No verification response");
        }
        // Also catches a source that changed during transmission without altering length.
        var after = new FileInfo(job.SourcePath);
        if (!after.Exists || after.Length != job.Size || after.LastWriteTimeUtc.Ticks != job.ModifiedUtcTicks)
            throw new InvalidDataException("Source modified during transmission");
        UpdateJob(job, "Completed");
        Inform($"Đã gửi {info.Name} tới {peer.DisplayName}");
    }

    private sealed class CompletedReceipt
    {
        public string Sha256 { get; set; } = "";
        public long Size { get; set; }
        public string Destination { get; set; } = "";
        public DateTime CompletedUtc { get; set; }
    }

    private async Task ReceiveFileAsync(Stream stream, FileOffer offer, string senderId, CancellationToken ct)
    {
        var acceptance = new FileAccept();
        string tempFile = "";
        string receiptFile = "";
        long offset = 0;
        bool alreadyDone = false;
        try
        {
            FileSafety.SafeName(offer.Name);
            if (offer.Size < 0 || offer.Size > Constants.MaxFileBytes || offer.TransferId.Length != 32 ||
                offer.Sha256.Length != 64 || !offer.Sha256.All(Uri.IsHexDigit) ||
                !Guid.TryParseExact(offer.TransferId, "N", out _))
                throw new InvalidDataException("Invalid file metadata");
            var folder = Path.GetFullPath(Settings.ReceiveFolder);
            Directory.CreateDirectory(folder);
            var parts = Path.Combine(folder, ".lanbridge-parts");
            Directory.CreateDirectory(parts);
            tempFile = Path.Combine(parts, $"{senderId}-{offer.TransferId}.part");
            receiptFile = Path.Combine(parts, $"{senderId}-{offer.TransferId}.done.json");
            if (File.Exists(receiptFile))
            {
                var receipt = System.Text.Json.JsonSerializer.Deserialize<CompletedReceipt>(
                    File.ReadAllText(receiptFile), Constants.Json);
                if (receipt?.Sha256 == offer.Sha256 && receipt.Size == offer.Size &&
                    File.Exists(receipt.Destination))
                {
                    alreadyDone = true;
                    offset = offer.Size;
                }
                else throw new IOException("Transfer ID reused with incompatible file metadata");
            }
            else offset = File.Exists(tempFile) ? new FileInfo(tempFile).Length : 0;
            if (offset > offer.Size)
            {
                File.Delete(tempFile);
                offset = 0;
            }
            var diskRoot = Path.GetPathRoot(folder) ?? throw new IOException("Missing disk root");
            var drive = new DriveInfo(diskRoot);
            if (drive.AvailableFreeSpace < offer.Size - offset + 1024 * 1024)
                throw new IOException("Không đủ dung lượng ổ đĩa");
            acceptance.Accepted = true;
            acceptance.Offset = offset;
        }
        catch (Exception ex)
        {
            acceptance.Error = ex.Message;
        }
        await WireIO.SendAsync(stream, WireEnvelope.Create(LocalId, "FileAccept", acceptance), ct);
        if (!acceptance.Accepted) return;
        if (alreadyDone)
        {
            await WireIO.SendAsync(stream, WireEnvelope.Create(LocalId, "FileResult", new TransferResult
            { Success = true, Message = "Already verified" }), ct);
            return;
        }
        try
        {
            await using (var temp = new FileStream(tempFile, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None,
                262144, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                temp.Seek(offset, SeekOrigin.Begin);
                var remaining = offer.Size - offset;
                var buffer = new byte[262144];
                while (remaining > 0)
                {
                    using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    idleTimeout.CancelAfter(TimeSpan.FromSeconds(45));
                    var size = (int)Math.Min(buffer.Length, remaining);
                    var read = await stream.ReadAsync(buffer.AsMemory(0, size), idleTimeout.Token);
                    if (read == 0) throw new EndOfStreamException("Connection interrupted");
                    await temp.WriteAsync(buffer.AsMemory(0, read), ct);
                    remaining -= read;
                }
                await temp.FlushAsync(ct);
            }
            await using (var verify = new FileStream(tempFile, FileMode.Open, FileAccess.Read, FileShare.Read,
                262144, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var hash = Convert.ToHexString(await SHA256.HashDataAsync(verify, ct)).ToLowerInvariant();
                if (!hash.Equals(offer.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("SHA-256 không khớp: file nhận bị lỗi");
            }
            string completedPath;
            // Avoid overwriting a user's file. Atomic move (no overwrite) still guards against races.
            for (;;)
            {
                completedPath = FileSafety.AvailableDestination(Settings.ReceiveFolder, offer.Name);
                try { File.Move(tempFile, completedPath, false); break; }
                catch (IOException) when (File.Exists(completedPath)) { /* duplicate created concurrently */ }
            }
            var receipt = new CompletedReceipt { Sha256 = offer.Sha256, Size = offer.Size,
                Destination = completedPath, CompletedUtc = DateTime.UtcNow };
            File.WriteAllText(receiptFile, System.Text.Json.JsonSerializer.Serialize(receipt, Constants.Json));
            await WireIO.SendAsync(stream, WireEnvelope.Create(LocalId, "FileResult", new TransferResult
            { Success = true, Message = "Verified" }), ct);
            ReceivedFile?.Invoke(completedPath);
            Inform("Đã nhận " + offer.Name);
        }
        catch (Exception ex)
        {
            try
            {
                await WireIO.SendAsync(stream, WireEnvelope.Create(LocalId, "FileResult", new TransferResult
                { Success = false, Message = ex.Message }), ct);
            }
            catch (Exception) { /* peer disconnected */ }
            throw;
        }
    }
}
