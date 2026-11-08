using System.IO;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using WtfNel.Core.Auth;
using WtfNel.Ui.Services;

namespace WtfNel.Ui.Pages;

/// <summary>Dialog body for creating or editing a saved account.</summary>
public partial class AccountEditor : UserControl
{
    private readonly SavedAccount? _original;
    private string _channel = "netease";
    private bool _nameTouched;

    public AccountEditor(SavedAccount? original)
    {
        InitializeComponent();
        _original = original;
        if (original is null)
        {
            ChNetease.IsChecked = true;
        }
        else
        {
            ChannelArea.Visibility = Visibility.Collapsed;
            _channel = original.Channel;
            UserBox.Text = original.Email;
            PasswordBox.Password = original.Password;
            CookieBox.Text = original.Cookie;
            NameBox.Text = original.Name;
            _nameTouched = true;
            ApplyChannel();
        }
        NameBox.TextChanged += (_, _) => _nameTouched |= NameBox.IsKeyboardFocused;
        Loaded += (_, _) => (_channel == "cookie" ? CookieBox : UserBox).Focus();
    }

    private void OnChannel(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton { Tag: string channel })
        {
            _channel = channel;
            ApplyChannel();
        }
    }

    private void ApplyChannel()
    {
        bool cookie = _channel == "cookie";
        PasswordArea.Visibility = cookie ? Visibility.Collapsed : Visibility.Visible;
        CookieArea.Visibility = cookie ? Visibility.Visible : Visibility.Collapsed;
        UserLabel.Text = _channel == "netease" ? "邮箱" : "4399 用户名";
        UserBox.Tag = _channel == "netease" ? "name@163.com" : "4399 账号";
        ErrorBar.Visibility = Visibility.Collapsed;
    }

    private void OnUserChanged(object sender, TextChangedEventArgs e)
    {
        if (!_nameTouched) NameBox.Text = UserBox.Text.Trim();
    }

    private void OnImportCookie(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog { Filter = "文本或 JSON|*.txt;*.json|所有文件|*.*" };
        if (dialog.ShowDialog() == true) CookieBox.Text = File.ReadAllText(dialog.FileName).Trim();
    }

    public bool Validate()
    {
        string? error = null;
        var name = NameBox.Text.Trim();
        if (_channel == "cookie")
        {
            var cookie = CookieBox.Text.Trim();
            if (cookie.Length == 0) error = "请粘贴 sauth_json";
            else if (!cookie.StartsWith('{')) error = "sauth_json 应该是 {...} 形式的 JSON 对象";
        }
        else
        {
            if (UserBox.Text.Trim().Length == 0) error = _channel == "netease" ? "请填写邮箱" : "请填写 4399 用户名";
            else if (_channel == "netease" && !UserBox.Text.Contains('@')) error = "邮箱格式不对";
            else if (PasswordBox.Password.Length == 0) error = "请填写密码";
        }
        if (error is null && name.Length == 0 && _channel == "cookie") name = NameBox.Text = $"账号{AppState.Store.Accounts.Count + 1}";
        if (error is null && name.Length == 0) error = "请填写名称";
        if (error is null && AppState.Store.Accounts.Any(a => a != _original && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            error = "已有同名账号";

        ErrorText.Text = error ?? "";
        ErrorBar.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        return error is null;
    }

    public SavedAccount Build()
    {
        var account = new SavedAccount { Channel = _channel };
        ApplyTo(account);
        return account;
    }

    /// <summary>Writes the form into the account; returns true when the login credentials changed.</summary>
    public bool ApplyTo(SavedAccount account)
    {
        var before = (account.Email, account.Password, account.Cookie);
        account.Name = NameBox.Text.Trim();
        if (_channel == "cookie")
        {
            account.Cookie = CookieBox.Text.Trim();
        }
        else
        {
            account.Email = UserBox.Text.Trim();
            account.Password = PasswordBox.Password;
        }
        return before != (account.Email, account.Password, account.Cookie);
    }
}
