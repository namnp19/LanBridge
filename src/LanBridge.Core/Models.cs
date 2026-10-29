using System.Text.Json;

namespace LanBridge.Core;

public static class Constants
{
    public const int ProtocolVersion = 1;
    public const int TcpPort = 47321;
    public const int UdpPort = 47322;
    public const int MaxControlBytes = 4 * 1024 * 1024;
    public const int MaxClipboardBytes = 2 * 1024 * 1024;
    public const long MaxFileBytes = 100L * 1024 * 1024 * 1024;
    public static readonly TimeSpan ClipboardTtl = TimeSpan.FromMinutes(30);
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = true, WriteIndented = false };
}

public sealed class LocalSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string DisplayName { get; set; } = Environment.MachineName;
    public string ReceiveFolder { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "LanBridge");
    public string CopyHotkey { get; set; } = "Ctrl+Shift+C";
    public string PasteHotkey { get; set; } = "Ctrl+Shift+V";
    public bool ClipboardEnabled { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
}

public sealed record Member(string DeviceId, string CertificateBase64, string Role, bool Active, string DisplayName);

public sealed class MembershipEvent
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public string GroupId { get; set; } = "";
    public string Type { get; set; } = ""; // Created, Joined, RoleChanged, Revoked
    public string ActorId { get; set; } = "";
    public string SubjectId { get; set; } = "";
    public string SubjectCertificate { get; set; } = "";
    public string SubjectName { get; set; } = "";
    public string Role { get; set; } = "";
    public long Clock { get; set; }
    public string Signature { get; set; } = "";
    public string Canonical() => JsonSerializer.Serialize(new
    {
        EventId, GroupId, Type, ActorId, SubjectId, SubjectCertificate, SubjectName, Role, Clock
    }, Constants.Json);
}

public sealed class GroupDocument
{
    public string GroupId { get; set; } = "";
    public string Name { get; set; } = "";
    public string RootDeviceId { get; set; } = "";
    public List<MembershipEvent> Events { get; set; } = [];
}

public sealed class DiscoveryPacket
{
    public int Version { get; set; } = Constants.ProtocolVersion;
    public string Kind { get; set; } = "announce";
    public string DeviceId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string GroupName { get; set; } = "";
    public bool IsAdmin { get; set; }
    public int Port { get; set; } = Constants.TcpPort;
}

public sealed class Peer
{
    public string DeviceId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Address { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string GroupName { get; set; } = "";
    public bool IsAdminAdvertised { get; set; }
    public int Port { get; set; } = Constants.TcpPort;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public bool IsOnline => DateTime.UtcNow - LastSeenUtc < TimeSpan.FromSeconds(22);
    public override string ToString() => $"{DisplayName} ({Address})";
}

public sealed class PendingJoin
{
    public string GroupId { get; set; } = "";
    public string JoinId { get; set; } = "";
    public string DeviceId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string Address { get; set; } = "";
    public string AdminId { get; set; } = "";
    public string Code { get; set; } = "";
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}

public sealed class JoinRequest
{
    public string JoinId { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string DisplayName { get; set; } = "";
}

public sealed class JoinApproval
{
    public string JoinId { get; set; } = "";
    public GroupDocument Group { get; set; } = new();
}

public sealed class ClipboardItem
{
    public string EventId { get; set; } = Guid.NewGuid().ToString("N");
    public string OriginDeviceId { get; set; } = "";
    public long Clock { get; set; }
    public DateTime PublishedUtc { get; set; } = DateTime.UtcNow;
    public string? Text { get; set; } // null: clear shared clipboard
    public bool IsAlive => DateTime.UtcNow - PublishedUtc < Constants.ClipboardTtl;
}

public sealed class WireEnvelope
{
    public int Version { get; set; } = Constants.ProtocolVersion;
    public string SenderId { get; set; } = "";
    public string Kind { get; set; } = "";
    public string Payload { get; set; } = "{}";
    public static WireEnvelope Create<T>(string senderId, string kind, T payload) => new()
    { SenderId = senderId, Kind = kind, Payload = JsonSerializer.Serialize(payload, Constants.Json) };
    public T Read<T>() => JsonSerializer.Deserialize<T>(Payload, Constants.Json)
        ?? throw new InvalidDataException($"Invalid {Kind} payload");
}

public sealed class SyncSnapshot
{
    public GroupDocument? Group { get; set; }
    public ClipboardItem? Clipboard { get; set; }
}

public sealed class FileOffer
{
    public string TransferId { get; set; } = "";
    public string Name { get; set; } = "";
    public long Size { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public string Sha256 { get; set; } = "";
}

public sealed class FileAccept
{
    public bool Accepted { get; set; }
    public string Error { get; set; } = "";
    public long Offset { get; set; }
}

public sealed class TransferResult
{
    public bool Success { get; set; }
    public string Message { get; set; } = "";
}

public sealed class TransferJob
{
    public string TransferId { get; set; } = Guid.NewGuid().ToString("N");
    public string PeerId { get; set; } = "";
    public string SourcePath { get; set; } = "";
    public long Size { get; set; }
    public long ModifiedUtcTicks { get; set; }
    public string Sha256 { get; set; } = "";
    public string Status { get; set; } = "Queued";
    public string Error { get; set; } = "";
    public long BytesSent { get; set; }
    public int RetryCount { get; set; }
    public DateTime NextAttemptUtc { get; set; } = DateTime.UtcNow;
    public string Display => $"{Path.GetFileName(SourcePath)} → {PeerId[..Math.Min(8, PeerId.Length)]}: {Status} ({BytesSent:N0}/{Size:N0})";
}

public static class FileSafety
{
    public static string SafeName(string requested)
    {
        if (string.IsNullOrWhiteSpace(requested) || requested.Length > 240 || requested is "." or "..")
            throw new InvalidDataException("Invalid filename");
        if (requested.Contains('/') || requested.Contains('\\') || requested.Contains(':') || requested.Contains('\0'))
            throw new InvalidDataException("Filename cannot contain path characters");
        if (requested.Any(c => char.IsControl(c) || Path.GetInvalidFileNameChars().Contains(c)))
            throw new InvalidDataException("Invalid filename characters");
        if (requested.EndsWith(' ') || requested.EndsWith('.'))
            throw new InvalidDataException("Unsafe filename suffix");
        if (requested.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0)
            throw new InvalidDataException("Invalid Windows filename characters");
        var stem = Path.GetFileNameWithoutExtension(requested).TrimEnd(' ', '.').ToUpperInvariant();
        if (new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }.Contains(stem))
            throw new InvalidDataException("Reserved Windows filename");
        return requested;
    }

    public static string AvailableDestination(string folder, string fileName)
    {
        SafeName(fileName);
        var name = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 0; i < 100000; i++)
        {
            var path = Path.Combine(folder, i == 0 ? fileName : $"{name} ({i}){ext}");
            if (!File.Exists(path) && !Directory.Exists(path)) return path;
        }
        throw new IOException("Cannot choose a unique destination filename");
    }
}
