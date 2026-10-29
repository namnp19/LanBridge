using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LanBridge.Desktop;

internal sealed class HotkeyService : IDisposable
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_ALT = 0x0001, MOD_CONTROL = 0x0002, MOD_SHIFT = 0x0004, MOD_WIN = 0x0008, MOD_NOREPEAT = 0x4000;
    private const int COPY_ID = 0xA007, PASTE_ID = 0xA008;
    private readonly IntPtr _handle;
    private readonly HwndSource _source;
    private string _copy = "", _paste = "";
    public event Action? CopyRequested;
    public event Action? PasteRequested;

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint GetClipboardSequenceNumber();

    [StructLayout(LayoutKind.Sequential)] private struct KeyboardInput
    {
        public ushort Key; public ushort Scan; public uint Flags; public uint Time; public IntPtr Extra;
    }
    [StructLayout(LayoutKind.Explicit, Size = 32)] private struct InputUnion { [FieldOffset(0)] public KeyboardInput Keyboard; }
    [StructLayout(LayoutKind.Sequential)] private struct Input { public uint Type; public InputUnion Value; }
    private static Input Key(ushort key, bool up) => new() { Type = 1,
        Value = new InputUnion { Keyboard = new KeyboardInput { Key = key, Flags = up ? 2u : 0u } } };

    public HotkeyService(Window window)
    {
        _handle = new WindowInteropHelper(window).Handle;
        _source = HwndSource.FromHwnd(_handle) ?? throw new InvalidOperationException("Window handle unavailable");
        _source.AddHook(WndProc);
    }
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wp, IntPtr lp, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            handled = true;
            if (wp.ToInt32() == COPY_ID) CopyRequested?.Invoke();
            if (wp.ToInt32() == PASTE_ID) PasteRequested?.Invoke();
        }
        return IntPtr.Zero;
    }

    private static (uint Modifiers, uint Key) Parse(string value)
    {
        var parts = value.Replace(" ", "").ToUpperInvariant().Split('+', StringSplitOptions.RemoveEmptyEntries);
        uint mod = MOD_NOREPEAT, key = 0;
        foreach (var part in parts)
        {
            switch (part)
            {
                case "CTRL" or "CONTROL": mod |= MOD_CONTROL; break;
                case "SHIFT": mod |= MOD_SHIFT; break;
                case "ALT": mod |= MOD_ALT; break;
                case "WIN": mod |= MOD_WIN; break;
                default:
                    if (part.Length == 1 && char.IsAsciiLetterOrDigit(part[0])) key = part[0];
                    else if (part.StartsWith('F') && int.TryParse(part[1..], out var f) && f is >= 1 and <= 12)
                        key = (uint)(0x70 + f - 1);
                    else throw new ArgumentException("Phím tắt không hợp lệ: " + value);
                    break;
            }
        }
        if (key == 0 || (mod & (MOD_CONTROL | MOD_SHIFT | MOD_ALT | MOD_WIN)) == 0)
            throw new ArgumentException("Hotkey cần ít nhất một modifier và một phím");
        return (mod, key);
    }

    public void Configure(string copy, string paste)
    {
        var a = Parse(copy);
        var b = Parse(paste);
        if (a == b) throw new ArgumentException("Hai phím tắt không được trùng nhau");
        UnregisterHotKey(_handle, COPY_ID);
        UnregisterHotKey(_handle, PASTE_ID);
        if (!RegisterHotKey(_handle, COPY_ID, a.Modifiers, a.Key))
        {
            RestoreOld();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Không đăng ký được phím Copy (đã bị ứng dụng khác dùng?)");
        }
        if (!RegisterHotKey(_handle, PASTE_ID, b.Modifiers, b.Key))
        {
            UnregisterHotKey(_handle, COPY_ID);
            RestoreOld();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Không đăng ký được phím Paste (đã bị ứng dụng khác dùng?)");
        }
        _copy = copy; _paste = paste;
    }
    private void RestoreOld()
    {
        if (_copy.Length > 0) { var key = Parse(_copy); RegisterHotKey(_handle, COPY_ID, key.Modifiers, key.Key); }
        if (_paste.Length > 0) { var key = Parse(_paste); RegisterHotKey(_handle, PASTE_ID, key.Modifiers, key.Key); }
    }

    private static async Task WaitModifiersReleasedAsync()
    {
        for (var i = 0; i < 90; i++)
        {
            if ((GetAsyncKeyState(0x11) & 0x8000) == 0 && (GetAsyncKeyState(0x10) & 0x8000) == 0 &&
                (GetAsyncKeyState(0x12) & 0x8000) == 0) return;
            await Task.Delay(20);
        }
        throw new TimeoutException("Hãy thả phím Ctrl/Shift/Alt rồi thử lại");
    }
    private static void SimulateCtrlKey(ushort key)
    {
        var inputs = new[] { Key(0x11, false), Key(key, false), Key(key, true), Key(0x11, true) };
        if (SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>()) != (uint)inputs.Length)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows không cho phép gửi phím tới cửa sổ đang mở");
    }

    public static async Task<string> CopySelectionAsync()
    {
        await WaitModifiersReleasedAsync();
        var before = GetClipboardSequenceNumber();
        SimulateCtrlKey(0x43); // C
        var until = DateTime.UtcNow.AddMilliseconds(1700);
        while (DateTime.UtcNow < until)
        {
            await Task.Delay(45);
            if (GetClipboardSequenceNumber() == before) continue;
            for (var retry = 0; retry < 5; retry++)
            {
                try
                {
                    if (!Clipboard.ContainsText(System.Windows.TextDataFormat.UnicodeText))
                        throw new InvalidOperationException("Không có văn bản Unicode để chia sẻ");
                    return Clipboard.GetText(System.Windows.TextDataFormat.UnicodeText);
                }
                catch (System.Runtime.InteropServices.ExternalException) { await Task.Delay(50); }
            }
            throw new IOException("Clipboard đang bị ứng dụng khác khóa");
        }
        throw new TimeoutException("Không phát hiện thao tác Copy, không gửi lại clipboard cũ");
    }

    public static async Task PasteAsync(string text)
    {
        await WaitModifiersReleasedAsync();
        for (var i = 0; i < 6; i++)
        {
            try { Clipboard.SetText(text, System.Windows.TextDataFormat.UnicodeText); break; }
            catch (System.Runtime.InteropServices.ExternalException) when (i < 5) { await Task.Delay(60); }
        }
        await Task.Delay(65);
        SimulateCtrlKey(0x56); // V
    }
    public void Dispose()
    {
        UnregisterHotKey(_handle, COPY_ID);
        UnregisterHotKey(_handle, PASTE_ID);
        _source.RemoveHook(WndProc);
    }
}
