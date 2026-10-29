using System.Text.Json;
using LanBridge.Core;

namespace LanBridge.Desktop;

internal sealed class LocalStore
{
    public string DirectoryPath { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LanBridge");
    private readonly object _writeLock = new();
    public LocalStore() => Directory.CreateDirectory(DirectoryPath);
    private string PathFor(string file) => Path.Combine(DirectoryPath, file);

    private T? Load<T>(string path) where T : class
    {
        if (!File.Exists(PathFor(path))) return null;
        return JsonSerializer.Deserialize<T>(File.ReadAllText(PathFor(path)), Constants.Json);
    }
    private void Save<T>(string path, T value)
    {
        lock (_writeLock)
        {
            var destination = PathFor(path);
            var tmp = destination + ".new";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value, Constants.Json));
            File.Move(tmp, destination, true);
        }
    }
    public LocalSettings LoadSettings() => Load<LocalSettings>("settings.json") ?? new();
    public void SaveSettings(LocalSettings value) => Save("settings.json", value);
    public GroupDocument? LoadGroup() => Load<GroupDocument>("group.json");
    public void SaveGroup(GroupDocument value) => Save("group.json", value);
    public List<TransferJob> LoadJobs() => Load<List<TransferJob>>("outgoing.json") ?? [];
    public void SaveJobs(List<TransferJob> value) => Save("outgoing.json", value);
    public long LoadClipboardClock()
    {
        var file = PathFor("clipboard-clock.txt");
        return File.Exists(file) && long.TryParse(File.ReadAllText(file), out var clock) ? clock : 0;
    }
    public void SaveClipboardClock(long clock)
    {
        // Lamport clock only. NEVER persist clipboard contents.
        lock (_writeLock) File.WriteAllText(PathFor("clipboard-clock.txt"), clock.ToString());
    }
    public void LeaveGroup()
    {
        lock (_writeLock) { if (File.Exists(PathFor("group.json"))) File.Delete(PathFor("group.json")); }
    }
}
