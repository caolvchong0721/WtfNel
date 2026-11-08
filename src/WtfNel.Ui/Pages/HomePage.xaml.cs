using System.Windows;
using System.Windows.Controls;
using WtfNel.Core.Auth;
using WtfNel.Core.Auth.Login;
using WtfNel.Core.Logging;
using WtfNel.Ui.Services;

namespace WtfNel.Ui.Pages;

/// <summary>A character entry in the role picker.</summary>
public sealed record RoleItem(string Name, bool LastUsed)
{
    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
    public Visibility LastUsedVisibility => LastUsed ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>Home page: pick an account, log in, pick a character and launch the game.</summary>
public partial class HomePage : UserControl
{
    private bool _busy;

    public HomePage()
    {
        InitializeComponent();
        AppState.AccountsChanged += () => Dispatcher.BeginInvoke(ReloadAccounts);
        AppState.RunningChanged += () => Dispatcher.BeginInvoke(UpdateSessionState);
        AppState.SessionChanged += account => Dispatcher.BeginInvoke(() => OnSessionChanged(account));
        LogBuffer.StatusChanged += s => { if (s is not null) StatusText.Text = s; };
        Log.Written += OnLog;
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) { RefreshInfo(); UpdateSessionState(); } };
        ReloadAccounts();
    }

    private SavedAccount? Account => AccountBox.SelectedItem as SavedAccount;

    private NeteaseSession? Session => Account is { } a && AppState.Sessions.TryGetValue(a, out var s) ? s : null;

    private void ReloadAccounts()
    {
        var selected = Account?.Name ?? AppState.Ui.LastAccount;
        var accounts = AppState.Store.Ordered().ToList();
        AccountBox.ItemsSource = accounts;
        EmptyState.Visibility = accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        MainArea.Visibility = accounts.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        AccountBox.SelectedItem = accounts.FirstOrDefault(a => a.Name == selected) ?? accounts.FirstOrDefault();
        UpdateSessionState();
    }

    private void OnSessionChanged(SavedAccount account)
    {
        if (account != Account) return;
        RoleList.ItemsSource = null;
        UpdateSessionState();
        if (Session is not null) _ = LoadRolesAsync();
    }

    private void RefreshInfo()
    {
        var cfg = AppState.Config;
        ServerText.Text = cfg.RemoteHost.Equals("auto", StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(cfg.RemoteHost)
            ? $"自动 · {cfg.GameIdOrDefault}"
            : $"{cfg.RemoteHost}:{cfg.RemotePort}";
        var inject = new List<string>();
        if (cfg.EnableProxy) inject.Add("proxy");
        if (cfg.EnableHwidSpoof) inject.Add("hwidspoof");
        InjectText.Text = inject.Count > 0 ? string.Join(" · ", inject) : "已关闭";
        DirText.Text = string.IsNullOrWhiteSpace(cfg.GameDir) ? "自动探测" : cfg.GameDir;
        DirText.ToolTip = DirText.Text;
    }

    private void OnAccountChanged(object sender, SelectionChangedEventArgs e)
    {
        RoleList.ItemsSource = null;
        UpdateSessionState();
        if (Session is not null) _ = LoadRolesAsync();
    }

    private void UpdateSessionState()
    {
        var session = Session;
        SessionText.Text = session is null ? "未登录" : $"已登录 · uid {session.UserId}";
        LoginText.Text = session is null ? "登录" : "重新登录";
        RoleHint.Text = session is null ? "登录后显示这个账号在服务器里的角色"
            : RoleList.Items.Count == 0 ? "这个账号还没有角色，新建一个" : $"共 {RoleList.Items.Count} 个角色";
        RoleList.Visibility = RoleList.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        RefreshRoles.IsEnabled = CreateRole.IsEnabled = session is not null && !_busy && !AppState.IsRunning;
        UpdateLaunchState();
    }

    private void UpdateLaunchState()
    {
        bool running = AppState.IsRunning;
        var role = (RoleList.SelectedItem as RoleItem)?.Name;
        LaunchButton.IsEnabled = running || (!_busy && Account is not null && (role is not null || Session is null));
        LaunchButton.Style = (Style)FindResource(running ? "Btn.Danger" : "Btn.Primary");
        RefreshInfo();
        LaunchIcon.Data = Dialogs.Geo(running ? "I.Stop" : "I.Play");
        LaunchText.Text = running ? "停止游戏" : Session is null ? "登录并选择角色" : "启动游戏";
        ProgressArea.Visibility = running || _busy ? Visibility.Visible : Visibility.Collapsed;
        LoginButton.IsEnabled = AccountBox.IsEnabled = RoleList.IsEnabled = !running && !_busy;
        LaunchSummary.Text = running ? $"正在运行：{AppState.RunningAccount?.Name}，角色 {AppState.RunningRole}"
            : Account is null ? "请先选择账号"
            : role is null ? $"{Account.Name}，未选择角色"
            : $"{Account.Name}，角色 {role}";
    }

    private void SetBusy(bool busy, string status = "")
    {
        _busy = busy;
        StatusText.Text = status;
        RoleLoading.Visibility = Visibility.Collapsed;
        UpdateSessionState();
    }

    private async void OnLogin(object sender, RoutedEventArgs e)
    {
        if (Account is { } account) await LoginAsync(account);
    }

    private async Task<bool> LoginAsync(SavedAccount account)
    {
        SetBusy(true, "正在登录 " + account.Name + "…");
        try
        {
            AppState.Logout(account);
            var session = await AppState.LoginAsync(account);
            Dialogs.Toast($"已登录 {account.Name}（uid {session.UserId}）", DialogKind.Success);
            SetBusy(false);
            return true;
        }
        catch (Exception ex)
        {
            SetBusy(false);
            Log.Debug("[login] 登录失败 channel=" + account.Channel + Environment.NewLine + ex);
            var root = ex.GetBaseException();
            if (root is OperationCanceledException) Dialogs.Toast(root.Message, DialogKind.Warning);
            else await Dialogs.AlertAsync("登录失败", string.IsNullOrWhiteSpace(root.Message) ? root.GetType().Name : root.Message, DialogKind.Error);
            return false;
        }
    }

    private async Task LoadRolesAsync()
    {
        if (Account is not { } account || Session is not { } session) return;
        _busy = true;
        RoleLoading.Visibility = Visibility.Visible;
        UpdateLaunchState();
        try
        {
            var roles = await AppState.QueryRolesAsync(session);
            if (Account != account) return;
            var items = roles.OrderByDescending(r => r == account.LastRole).Select(r => new RoleItem(r, r == account.LastRole)).ToList();
            RoleList.ItemsSource = items;
            RoleList.SelectedItem = items.FirstOrDefault();
        }
        catch (Exception ex)
        {
            Log.Debug("[tpl] 获取角色失败 " + ex);
            await Dialogs.AlertAsync("获取角色失败", ex.GetBaseException().Message, DialogKind.Error);
        }
        finally
        {
            SetBusy(false);
        }
    }

    private async void OnRefreshRoles(object sender, RoutedEventArgs e) => await LoadRolesAsync();

    private async void OnCreateRole(object sender, RoutedEventArgs e)
    {
        if (Session is not { } session) return;
        var name = await Dialogs.PromptAsync("新建角色", "角色名");
        if (name is null) return;
        SetBusy(true, "正在创建角色…");
        try
        {
            await AppState.CreateRoleAsync(session, name);
            Dialogs.Toast($"已创建角色 {name}", DialogKind.Success);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            await Dialogs.AlertAsync("创建失败", ex.GetBaseException().Message, DialogKind.Error);
            return;
        }
        SetBusy(false);
        await LoadRolesAsync();
        RoleList.SelectedItem = (RoleList.ItemsSource as IEnumerable<RoleItem>)?.FirstOrDefault(r => r.Name == name) ?? RoleList.SelectedItem;
    }

    private void OnRoleChanged(object sender, SelectionChangedEventArgs e) => UpdateLaunchState();

    private async void OnLaunch(object sender, RoutedEventArgs e)
    {
        if (AppState.IsRunning)
        {
            if (await Dialogs.ConfirmAsync("停止游戏", "会结束游戏客户端进程。", "停止", danger: true)) AppState.StopGame();
            return;
        }
        if (Account is not { } account) return;
        if (Session is null)
        {
            await LoginAsync(account);
            return;
        }
        if (RoleList.SelectedItem is not RoleItem role) return;

        StatusText.Text = "正在准备…";
        RefreshInfo();
        try
        {
            var task = AppState.RunGameAsync(account, Session!, role.Name);
            Dialogs.Toast($"正在以 {role.Name} 启动", DialogKind.Info);
            await task;
            Dialogs.Toast("游戏已退出", DialogKind.Info);
        }
        catch (Exception ex)
        {
            Log.Error("出错了: " + ex.GetBaseException().Message);
            Log.Debug(ex.ToString());
            await Dialogs.AlertAsync("启动失败", ex.GetBaseException().Message, DialogKind.Error);
        }
        finally
        {
            StatusText.Text = "";
            RefreshInfo();
            UpdateLaunchState();
        }
    }

    private void OnLog(string level, DateTime time, string message)
    {
        if (level is "INF" or "WRN" or "ERR" && AppState.IsRunning)
            Dispatcher.BeginInvoke(() => StatusText.Text = message);
    }

    private void OnGoAccounts(object sender, RoutedEventArgs e) => MainWindow.Instance?.Navigate("accounts");
}
