using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WtfNel.Core.Auth;
using WtfNel.Core.Logging;
using WtfNel.Ui.Services;

namespace WtfNel.Ui.Pages;

/// <summary>A saved account as shown in the account list.</summary>
public sealed record AccountItem(SavedAccount Account)
{
    public Geometry Icon => Dialogs.Geo(Account.Channel switch
    {
        "cookie" => "I.Cookie",
        "netease" => "I.Mail",
        _ => "I.User",
    });

    public string Initial => Account.Name.Length > 0 ? Account.Name[..1].ToUpperInvariant() : "?";

    public Visibility OnlineVisibility => AppState.Sessions.ContainsKey(Account) ? Visibility.Visible : Visibility.Collapsed;

    public string Meta
    {
        get
        {
            var parts = new List<string> { Account.Channel == "cookie" ? "sauth_json" : Account.Email };
            if (Account.UserId != 0) parts.Add($"uid {Account.UserId}");
            if (!string.IsNullOrEmpty(Account.LastRole)) parts.Add("角色 " + Account.LastRole);
            if (Account.LastUsed is { } t) parts.Add($"上次 {t:MM-dd HH:mm}");
            return string.Join("  ·  ", parts);
        }
    }
}

/// <summary>Account management: add, edit, test-login and delete saved accounts.</summary>
public partial class AccountsPage : UserControl
{
    public AccountsPage()
    {
        InitializeComponent();
        AppState.AccountsChanged += () => Dispatcher.BeginInvoke(Reload);
        Reload();
    }

    private void Reload()
    {
        var items = AppState.Store.Ordered().Select(a => new AccountItem(a)).ToList();
        List.ItemsSource = items;
        EmptyState.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        Summary.Text = items.Count == 0 ? "添加一个账号开始使用。" : $"{items.Count} 个账号，按最近使用排序";
    }

    private async void OnAdd(object sender, RoutedEventArgs e)
    {
        var editor = new AccountEditor(null);
        if (await Dialogs.ShowAsync("添加账号", editor, ["取消", "保存并登录"], width: 480, validate: i => i != 1 || editor.Validate()) != 1) return;
        var account = editor.Build();
        AppState.Store.Accounts.Add(account);
        AppState.SaveAccounts();
        if (await TryLoginAsync(account))
        {
            Dialogs.Toast($"已添加并登录 {account.Name}", DialogKind.Success);
            return;
        }
        if (!await Dialogs.ConfirmAsync("登录没成功", "仍然保留这个账号？之后可以编辑后重试。", "保留"))
        {
            AppState.Store.Accounts.Remove(account);
            AppState.SaveAccounts();
        }
    }

    private async void OnEdit(object sender, RoutedEventArgs e)
    {
        if (Item(sender) is not { } account) return;
        var editor = new AccountEditor(account);
        if (await Dialogs.ShowAsync("编辑账号", editor, ["取消", "保存"], width: 480, validate: i => i != 1 || editor.Validate()) != 1) return;
        if (editor.ApplyTo(account)) AppState.Logout(account);
        AppState.SaveAccounts();
        Dialogs.Toast("已保存", DialogKind.Success);
    }

    private async void OnTest(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || Item(sender) is not { } account || AppState.IsLoggingIn(account)) return;
        var label = button.Content;
        button.IsEnabled = false;
        button.Content = "登录中…";
        try
        {
            if (await TryLoginAsync(account)) Dialogs.Toast($"{account.Name} 登录成功", DialogKind.Success);
        }
        finally
        {
            button.Content = label;
            button.IsEnabled = true;
        }
    }

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        if (Item(sender) is not { } account) return;
        if (!await Dialogs.ConfirmAsync("删除账号", $"确定删除「{account.Name}」？此操作不能撤销。", "删除", danger: true)) return;
        AppState.Store.Accounts.Remove(account);
        AppState.Logout(account);
        AppState.SaveAccounts();
    }

    private static async Task<bool> TryLoginAsync(SavedAccount account)
    {
        try
        {
            await AppState.LoginAsync(account);
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug("[login] 登录失败 channel=" + account.Channel + Environment.NewLine + ex);
            var root = ex.GetBaseException();
            if (root is OperationCanceledException)
            {
                Dialogs.Toast(root.Message, DialogKind.Warning);
                return false;
            }
            await Dialogs.AlertAsync("登录失败", string.IsNullOrWhiteSpace(root.Message) ? root.GetType().Name : root.Message, DialogKind.Error);
            return false;
        }
    }

    private static SavedAccount? Item(object sender) => (sender as Button)?.CommandParameter is AccountItem item ? item.Account : null;
}
