using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using LanBridge.Core;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace LanBridge.Desktop;

public partial class MainWindow : Window
{
    private readonly LanBridgeEngine _engine;
    private HotkeyService? _hotkeys;
    private Forms.NotifyIcon? _tray;
    private readonly HashSet<string> _selectedRecipients = new(StringComparer.Ordinal);
    private readonly List<string> _selectedFiles = [];
    private bool _quitting;
    private bool _refreshQueued;
    private bool _refreshRecipients;
    private sealed record PeerRow(Peer Peer, string Caption);
    private sealed record RequestRow(PendingJoin Join, string Caption);
    private sealed record MemberRow(Member Member, string Caption);
    private sealed record JobRow(TransferJob Job, string Caption);

    internal MainWindow(LanBridgeEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        FolderText.Text = engine.Settings.ReceiveFolder;
        CopyHotkeyText.Text = engine.Settings.CopyHotkey;
        PasteHotkeyText.Text = engine.Settings.PasteHotkey;
        EnableClipboardCheck.IsChecked = engine.Settings.ClipboardEnabled;
        AutostartCheck.IsChecked = engine.Settings.StartWithWindows;
        _engine.Changed += QueueRefresh;
        _engine.Message += message => Dispatcher.BeginInvoke(new Action(() => MessageStatus.Text = message));
        _engine.ReceivedFile += path => Dispatcher.BeginInvoke(new Action(() =>
        {
            _tray?.ShowBalloonTip(2500, "LanBridge – Đã nhận file", System.IO.Path.GetFileName(path), Forms.ToolTipIcon.Info);
            MessageStatus.Text = "Đã nhận: " + path;
        }));
        SourceInitialized += (_, _) =>
        {
            try
            {
                _hotkeys = new HotkeyService(this);
                _hotkeys.Configure(engine.Settings.CopyHotkey, engine.Settings.PasteHotkey);
                _hotkeys.CopyRequested += async () => await CopyHotkeyAsync();
                _hotkeys.PasteRequested += async () => await PasteHotkeyAsync();
            }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Không đăng ký được phím tắt"); }
        };
        SetupTray();
        try { ApplyAutostart(engine.Settings.StartWithWindows); }
        catch (Exception ex) { MessageStatus.Text = "Tự khởi động Windows: " + ex.Message; }
        RefreshView();
    }

    private void SetupTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Mở LanBridge", null, (_, _) => Dispatcher.Invoke(ShowWindow));
        menu.Items.Add("Mở thư mục nhận", null, (_, _) => Dispatcher.Invoke(OpenReceivedFolder));
        menu.Items.Add("Thoát", null, (_, _) => Dispatcher.Invoke(async () => await QuitAsync()));
        _tray = new Forms.NotifyIcon
        {
            Icon = Drawing.SystemIcons.Application,
            Text = "LanBridge - LAN Clipboard & Files",
            Visible = true,
            ContextMenuStrip = menu
        };
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowWindow);
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void QueueRefresh()
    {
        if (_refreshQueued || Dispatcher.HasShutdownStarted) return;
        _refreshQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _refreshQueued = false;
            RefreshView();
        }), System.Windows.Threading.DispatcherPriority.Background);
    }
    private void RefreshView()
    {
        GroupStatus.Text = $"{_engine.GroupLabel}   •   {_engine.OnlineMembers.Count} máy kết nối" +
            (_engine.IsAdmin ? "   •   Admin" : _engine.HasGroup ? "   •   Thành viên" : "");
        MyDevice.Text = $"Máy này: {_engine.Settings.DisplayName} | ID: {_engine.LocalId[..12]}";
        ClipboardState.Text = _engine.ClipboardLabel;
        PairingState.Text = string.IsNullOrWhiteSpace(_engine.PendingJoinCode) ? "" :
            "Đang chờ duyệt. Mã đối chiếu với Admin: " + _engine.PendingJoinCode;
        CreateGroupButton.IsEnabled = !_engine.HasGroup;
        JoinGroupButton.IsEnabled = !_engine.HasGroup;
        var previous = (DiscoveredList.SelectedItem as PeerRow)?.Peer.DeviceId;
        var peers = _engine.Peers.Select(p => new PeerRow(p,
            $"{p.DisplayName} | {p.Address} | {(p.IsOnline ? "Online" : "Offline")} | " +
            (p.GroupId.Length == 0 ? "Chưa có nhóm" : p.GroupName + (p.IsAdminAdvertised ? " (Admin)" : "")))).ToList();
        DiscoveredList.ItemsSource = peers;
        DiscoveredList.SelectedItem = peers.FirstOrDefault(x => x.Peer.DeviceId == previous);

        var oldReq = (RequestList.SelectedItem as RequestRow)?.Join.JoinId;
        var requests = _engine.PendingRequests.Select(x => new RequestRow(x,
            $"{x.DisplayName} ({x.DeviceId[..8]}) | Mã xác nhận: {x.Code}")).ToList();
        RequestList.ItemsSource = requests;
        RequestList.SelectedItem = requests.FirstOrDefault(x => x.Join.JoinId == oldReq);

        var oldMember = (MemberList.SelectedItem as MemberRow)?.Member.DeviceId;
        var members = _engine.Members.Select(m => new MemberRow(m,
            $"{m.DisplayName} ({m.DeviceId[..8]}) | {m.Role} | {(m.Active ? "Đang trong nhóm" : "Đã thu hồi")}" +
            (m.DeviceId == _engine.LocalId ? " (máy này)" : ""))).ToList();
        MemberList.ItemsSource = members;
        MemberList.SelectedItem = members.FirstOrDefault(x => x.Member.DeviceId == oldMember);

        _refreshRecipients = true;
        RecipientPanel.Children.Clear();
        var targets = _engine.OnlineMembers;
        _selectedRecipients.RemoveWhere(x => !targets.Any(p => p.DeviceId == x));
        foreach (var peer in targets)
        {
            var cb = new CheckBox
            {
                Content = $"{peer.DisplayName} ({peer.DeviceId[..8]})", Tag = peer.DeviceId,
                IsChecked = _selectedRecipients.Contains(peer.DeviceId), Margin = new Thickness(5, 3, 5, 3)
            };
            cb.Checked += RecipientChanged;
            cb.Unchecked += RecipientChanged;
            RecipientPanel.Children.Add(cb);
        }
        SelectAllMachines.IsChecked = targets.Count > 0 && targets.All(x => _selectedRecipients.Contains(x.DeviceId));
        _refreshRecipients = false;

        var oldJob = (TransferList.SelectedItem as JobRow)?.Job.TransferId;
        var jobs = _engine.Jobs.TakeLast(100).Reverse().Select(j => new JobRow(j,
            j.Display + (string.IsNullOrWhiteSpace(j.Error) ? "" : " | " + j.Error))).ToList();
        TransferList.ItemsSource = jobs;
        TransferList.SelectedItem = jobs.FirstOrDefault(x => x.Job.TransferId == oldJob);
        DiagnosticText.Text = $"TCP: {Constants.TcpPort} | UDP: {Constants.UdpPort} | " +
            $"Đã phát hiện {_engine.Peers.Count} PC | Online {_engine.Peers.Count(p => p.IsOnline)} | " +
            $"Nhóm: {_engine.GroupLabel}\n" +
            $"Device fingerprint: {_engine.LocalId}\n" +
            $"Thư mục nhận: {_engine.Settings.ReceiveFolder}";
    }

    private void RecipientChanged(object sender, RoutedEventArgs e)
    {
        if (_refreshRecipients || sender is not CheckBox cb || cb.Tag is not string id) return;
        if (cb.IsChecked == true) _selectedRecipients.Add(id);
        else _selectedRecipients.Remove(id);
    }
    private void SelectAllMachines_Changed(object sender, RoutedEventArgs e)
    {
        if (_refreshRecipients) return;
        foreach (var p in _engine.OnlineMembers)
        {
            if (SelectAllMachines.IsChecked == true) _selectedRecipients.Add(p.DeviceId);
            else _selectedRecipients.Remove(p.DeviceId);
        }
        QueueRefresh();
    }

    private void AddPaths(IEnumerable<string> files)
    {
        foreach (var file in files)
            if (System.IO.File.Exists(file) && !_selectedFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
                _selectedFiles.Add(file);
        FileList.ItemsSource = null;
        FileList.ItemsSource = _selectedFiles.ToList();
    }
    private void FileDrop_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void FileDrop_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) AddPaths(paths);
        e.Handled = true;
    }
    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true, Title = "Chọn file để gửi" };
        if (dialog.ShowDialog(this) == true) AddPaths(dialog.FileNames);
    }
    private void ClearFiles_Click(object sender, RoutedEventArgs e)
    {
        _selectedFiles.Clear(); FileList.ItemsSource = null;
    }
    private void Send_Click(object sender, RoutedEventArgs e)
    {
        TryAction(() =>
        {
            _engine.EnqueueFiles(_selectedFiles, _selectedRecipients);
            _selectedFiles.Clear(); FileList.ItemsSource = null;
            RefreshView();
        });
    }
    private void CancelTransfer_Click(object sender, RoutedEventArgs e)
    {
        if (TransferList.SelectedItem is JobRow row) _engine.CancelTransfer(row.Job.TransferId);
    }

    private async Task CopyHotkeyAsync()
    {
        try
        {
            if (!_engine.HasGroup) throw new InvalidOperationException("Hãy tạo hoặc tham gia nhóm trước");
            var text = await HotkeyService.CopySelectionAsync();
            await _engine.PublishClipboardAsync(text);
            MessageStatus.Text = "Đã chia sẻ văn bản tới nhóm";
        }
        catch (Exception ex) { MessageStatus.Text = "Copy: " + ex.Message; }
    }
    private async Task PasteHotkeyAsync()
    {
        try
        {
            var text = _engine.CurrentSharedClipboard;
            if (text is null) throw new InvalidOperationException("Clipboard chung đang rỗng hoặc đã hết hạn");
            await HotkeyService.PasteAsync(text);
            MessageStatus.Text = "Đã dán từ clipboard chung";
        }
        catch (Exception ex) { MessageStatus.Text = "Paste: " + ex.Message; }
    }
    private async void ClearClipboard_Click(object sender, RoutedEventArgs e)
    {
        try { await _engine.PublishClipboardAsync(null); }
        catch (Exception ex) { ShowError(ex); }
    }

    private void LeaveGroup_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.HasGroup) return;
        if (MessageBox.Show(this, "Rời nhóm trên máy này? Các PC khác sẽ không nhận được thông báo thu hồi " +
            "tự động khi bạn chỉ rời nhóm. Hãy dùng Admin xóa thành viên nếu cần thu hồi quyền trên toàn nhóm.",
            "Xác nhận rời nhóm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _engine.LeaveGroup();
    }

    private void CreateGroup_Click(object sender, RoutedEventArgs e)
    {
        var name = Prompt("Tên nhóm LanBridge", "Home LAN");
        if (name != null) TryAction(() => _engine.CreateGroup(name));
    }
    private async void JoinGroup_Click(object sender, RoutedEventArgs e)
    {
        if (DiscoveredList.SelectedItem is not PeerRow row) { ShowError("Hãy chọn một máy Admin đang online"); return; }
        if (!row.Peer.IsOnline || !row.Peer.IsAdminAdvertised)
        {
            ShowError("Máy đã chọn cần online và có quyền Admin của nhóm"); return;
        }
        try
        {
            var code = await _engine.RequestJoinAsync(row.Peer);
            MessageBox.Show(this, $"Gửi yêu cầu tới {row.Peer.DisplayName}.\n\n" +
                $"Mã ghép cặp: {code}\n\nHãy so sánh mã trên máy Admin. " +
                "Chỉ duyệt nếu cả hai máy hiển thị cùng mã.", "Ghép cặp LanBridge");
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void Approve_Click(object sender, RoutedEventArgs e)
    {
        if (RequestList.SelectedItem is not RequestRow row) return;
        if (MessageBox.Show(this, $"Mã ghép cặp: {row.Join.Code}\n\n" +
            $"Thiết bị: {row.Join.DisplayName}\n" +
            "Bạn đã trực tiếp đối chiếu mã giống nhau trên máy xin gia nhập?",
            "Duyệt thiết bị", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        try { await _engine.ApproveAsync(row.Join); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void Reject_Click(object sender, RoutedEventArgs e)
    {
        if (RequestList.SelectedItem is RequestRow row) _engine.Reject(row.Join);
    }
    private void ChangeRole(string action)
    {
        if (MemberList.SelectedItem is not MemberRow row) return;
        if (MessageBox.Show(this, $"{action}: {row.Member.DisplayName}?", "Xác nhận",
            MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
        TryAction(() => _engine.ChangeMember(row.Member.DeviceId, action));
    }
    private void Promote_Click(object sender, RoutedEventArgs e) => ChangeRole("Promote");
    private void Demote_Click(object sender, RoutedEventArgs e) => ChangeRole("Demote");
    private void Revoke_Click(object sender, RoutedEventArgs e) => ChangeRole("Revoke");
    private async void ManualIp_Click(object sender, RoutedEventArgs e)
    {
        var ip = Prompt("Nhập địa chỉ IPv4 của máy LanBridge trong LAN", "192.168.1.10");
        if (ip is null) return;
        try { await _engine.ProbeIpAsync(ip); MessageStatus.Text = "Đã gửi yêu cầu tìm máy tới " + ip; }
        catch (Exception ex) { ShowError(ex); }
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        { Description = "Chọn thư mục lưu file từ LanBridge", ShowNewFolderButton = true };
        if (dialog.ShowDialog() == Forms.DialogResult.OK) FolderText.Text = dialog.SelectedPath;
    }
    private void OpenReceivedFolder()
    {
        var folder = _engine.Settings.ReceiveFolder;
        System.IO.Directory.CreateDirectory(folder);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e) => TryAction(OpenReceivedFolder);

    private async void CheckFirewall_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var ps = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            ps.ArgumentList.Add("-NoProfile");
            ps.ArgumentList.Add("-NonInteractive");
            ps.ArgumentList.Add("-Command");
            ps.ArgumentList.Add("Get-NetFirewallRule -Name 'LanBridge-LAN-TCP','LanBridge-LAN-UDP' -ErrorAction SilentlyContinue | Select-Object Name,Enabled,Profile | Format-Table -AutoSize | Out-String");
            using var child = Process.Start(ps) ?? throw new IOException("PowerShell not available");
            var output = await child.StandardOutput.ReadToEndAsync();
            var error = await child.StandardError.ReadToEndAsync();
            await child.WaitForExitAsync();
            DiagnosticText.Text = string.IsNullOrWhiteSpace(output) ? "Không thấy Firewall Rule LanBridge. " + error : output;
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private async void RepairFirewall_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var script = System.IO.Path.Combine(AppContext.BaseDirectory, "firewall.ps1");
            if (!System.IO.File.Exists(script))
                throw new FileNotFoundException("Thiếu firewall.ps1. Hãy cài LanBridge bằng installer chính thức.");
            var ps = new ProcessStartInfo("powershell.exe")
            {
                UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden
            };
            foreach (var argument in new [] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass",
                     "-File", script, "-Action", "Install", "-ExePath", Environment.ProcessPath ?? "" })
                ps.ArgumentList.Add(argument);
            using var process = Process.Start(ps) ?? throw new IOException("Không thể chạy PowerShell elevated");
            await process.WaitForExitAsync();
            MessageStatus.Text = process.ExitCode == 0
                ? "Đã thiết lập lại firewall rules (Private/LocalSubnet)" : $"Firewall repair lỗi (exit {process.ExitCode})";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void SaveSettings_Click(object sender, RoutedEventArgs e)
    {
        TryAction(() =>
        {
            _hotkeys?.Configure(CopyHotkeyText.Text, PasteHotkeyText.Text);
            _engine.ChangeSettings(FolderText.Text, CopyHotkeyText.Text, PasteHotkeyText.Text,
                EnableClipboardCheck.IsChecked == true);
            _engine.Settings.StartWithWindows = AutostartCheck.IsChecked == true;
            ApplyAutostart(_engine.Settings.StartWithWindows);
            // Save the changed autostart field too.
            _engine.ChangeSettings(FolderText.Text, CopyHotkeyText.Text, PasteHotkeyText.Text,
                EnableClipboardCheck.IsChecked == true);
            MessageStatus.Text = "Đã lưu cấu hình";
        });
    }
    private static void ApplyAutostart(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true);
        if (key == null) return;
        if (enabled && Environment.ProcessPath != null)
            key.SetValue("LanBridge", $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue("LanBridge", false);
    }
    private static string? Prompt(string title, string initial)
    {
        var window = new Window
        {
            Title = title, Width = 460, Height = 150, WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.NoResize
        };
        var stack = new StackPanel { Margin = new Thickness(15) };
        var box = new TextBox { Text = initial };
        var button = new Button { Content = "Đồng ý", HorizontalAlignment = System.Windows.HorizontalAlignment.Right };
        button.Click += (_, _) => { window.DialogResult = true; window.Close(); };
        stack.Children.Add(box); stack.Children.Add(button);
        window.Content = stack;
        return window.ShowDialog() == true ? box.Text.Trim() : null;
    }
    private void TryAction(Action action)
    {
        try { action(); }
        catch (Exception ex) { ShowError(ex); }
    }
    private void ShowError(Exception ex) => ShowError(ex.Message);
    private void ShowError(string text) => MessageBox.Show(this, text, "LanBridge", MessageBoxButton.OK, MessageBoxImage.Warning);

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_quitting) return;
        e.Cancel = true;
        Hide();
        _tray?.ShowBalloonTip(1500, "LanBridge vẫn đang chạy", "Nhấp đúp icon khay hệ thống để mở lại.", Forms.ToolTipIcon.Info);
    }
    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        _hotkeys?.Dispose();
        if (_tray != null) { _tray.Visible = false; _tray.Dispose(); }
        await _engine.DisposeAsync();
        System.Windows.Application.Current.Shutdown();
    }
}
