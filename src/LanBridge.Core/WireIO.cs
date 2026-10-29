using System.Buffers.Binary;
using System.Text.Json;

namespace LanBridge.Core;

public static class WireIO
{
    public static async Task SendAsync(Stream stream, WireEnvelope frame, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame, Constants.Json);
        if (bytes.Length > Constants.MaxControlBytes) throw new InvalidDataException("Control message too large");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, ct);
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }
    public static async Task<WireEnvelope> ReceiveAsync(Stream stream, CancellationToken ct)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, ct);
        var size = BinaryPrimitives.ReadInt32BigEndian(header);
        if (size < 2 || size > Constants.MaxControlBytes) throw new InvalidDataException("Invalid frame length");
        var content = new byte[size];
        await stream.ReadExactlyAsync(content, ct);
        var frame = JsonSerializer.Deserialize<WireEnvelope>(content, Constants.Json)
            ?? throw new InvalidDataException("Malformed frame");
        if (frame.Version != Constants.ProtocolVersion || frame.Kind.Length > 40 || frame.SenderId.Length != 64)
            throw new InvalidDataException("Unsupported or malformed protocol");
        return frame;
    }
}

public static class ClipboardOrder
{
    public static bool IsNewer(ClipboardItem incoming, ClipboardItem? previous)
    {
        if (!incoming.IsAlive || incoming.PublishedUtc > DateTime.UtcNow.AddMinutes(2)) return false;
        if (previous is null || !previous.IsAlive) return true;
        if (incoming.Clock != previous.Clock) return incoming.Clock > previous.Clock;
        var tie = string.CompareOrdinal(incoming.OriginDeviceId, previous.OriginDeviceId);
        return tie != 0 ? tie > 0 : string.CompareOrdinal(incoming.EventId, previous.EventId) > 0;
    }
}
