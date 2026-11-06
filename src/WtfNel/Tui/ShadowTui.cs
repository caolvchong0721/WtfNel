using WtfNel.Core.Auth;
using WtfNel.Core.Auth.Login;
using WtfNel.Core.Auth.Yggdrasil;
using WtfNel.Core;
using WtfNel.Core.Injection;
using Spectre.Console;

namespace WtfNel.Tui;

/// <summary>
/// The interactive UI shown after double-clicking the exe (click to select; arrow keys / Enter also work):
/// the main menu lists accounts (click = log in and start the game), add account, manage accounts, and exit.
/// Accounts are stored in plain text in accounts.json next to the exe.
/// </summary>
public static class ShadowTui
{
    private enum Kind { Account, Add, Manage, Settings, Back, Exit }

    private sealed record Choice(Kind Kind, string Label, SavedAccount? Account = null);

    private static readonly Dictionary<SavedAccount, NeteaseSession> Sessions = new();

    public static void Banner()
    {
        AnsiConsole.Write(new FigletText("WtfNel").Color(Color.MediumPurple));
        AnsiConsole.MarkupLine("[grey]鼠标点击选择（方向键 + 回车也行）· Ctrl+C 退出[/]");
        AnsiConsole.WriteLine();
    }

    /// <summary>
    /// Main menu: pick an account → log in → pick a character (sub-account). Returns true on success: the session
    /// is handed to YggdrasilSetup and the character is written to cfg.UserName / cfg.Uuid (rewriting --username /
    /// --uuid at launch); returns false on exit.
    /// </summary>
    public static async Task<bool> RunAsync(ShadowConfig cfg, string gameId)
    {
        var store = AccountStore.Load();
        ImportLegacy(cfg, store);
        ClickMenu.ResetAnchor();

        while (true)
        {
            var choices = store.Ordered().Select(a => new Choice(Kind.Account, "▶ " + AccountLabel(a), a)).ToList();
            choices.Add(new Choice(Kind.Add, "[green]+ 添加账号[/]"));
            if (store.Accounts.Count > 0) choices.Add(new Choice(Kind.Manage, "[blue]≡ 管理账号[/]  [grey]重命名 / 修改 / 删除[/]"));
            choices.Add(new Choice(Kind.Settings, "[mediumpurple]⚙ 注入设置[/]  [grey]proxy / hwidspoof 开关[/]"));
            choices.Add(new Choice(Kind.Exit, "[grey]× 退出[/]"));

            var pick = ClickMenu.Select(store.Accounts.Count == 0
                ? "[yellow]还没有账号，先添加一个：[/]"
                : "[bold]选择账号启动游戏：[/]", choices, c => c.Label);

            switch (pick.Kind)
            {
                case Kind.Exit:
                    return false;
                case Kind.Add:
                    await AddAccountAsync(cfg, store);
                    break;
                case Kind.Manage:
                    await ManageAsync(cfg, store);
                    break;
                case Kind.Settings:
                    InjectionSettings(cfg);
                    break;
                case Kind.Account:
                    var acc = pick.Account!;
                    if (!await UseAccountAsync(cfg, store, acc)) break;
                    if (string.IsNullOrEmpty(gameId))
                    {
                        AnsiConsole.MarkupLine("[yellow]模板里没有 -DlauncherGameId，跳过角色选择，沿用模板里的角色[/]");
                        return true;
                    }
                    var role = await PickRoleAsync(cfg, store, acc, Sessions[acc], gameId);
                    if (role is null) break;
                    cfg.UserName = role;
                    cfg.Uuid = Skip32.RoleUuid(role, Sessions[acc].UserId);
                    return true;
            }
        }
    }

    private static void InjectionSettings(ShadowConfig cfg)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "shadow.json");
        while (true)
        {
            string[] items =
            [
                $"proxy       {(cfg.EnableProxy ? "[green]开[/]" : "[grey]关[/]")}",
                $"hwidspoof   {(cfg.EnableHwidSpoof ? "[green]开[/]" : "[grey]关[/]")}",
                "[grey]返回[/]",
            ];

            string pick = ClickMenu.Select("注入设置：", items, s => s);
            if (pick.StartsWith("proxy", StringComparison.Ordinal)) cfg.EnableProxy = !cfg.EnableProxy;
            else if (pick.StartsWith("hwidspoof", StringComparison.Ordinal)) cfg.EnableHwidSpoof = !cfg.EnableHwidSpoof;
            else return;
            cfg.Save(path);
        }
    }

    public static void WaitForKey()
    {
        AnsiConsole.WriteLine();
        AnsiConsole.MarkupLine("[grey]按任意键关闭窗口…[/]");
        try { Console.ReadKey(true); } catch (InvalidOperationException) { }
    }

    /// <summary>Logs in (once per run for the same account) and hands off to the next step.</summary>
    private static async Task<bool> UseAccountAsync(ShadowConfig cfg, AccountStore store, SavedAccount acc)
    {
        if (!Sessions.TryGetValue(acc, out var session))
        {
            session = await LoginAsync(cfg, acc.Channel, acc.Email, acc.Secret);
            if (session is null) return false;
            Sessions[acc] = session;
        }
        acc.UserId = session.UserId;
        acc.LastUsed = DateTime.Now;
        store.Save();
        YggdrasilSetup.UseSession(session);
        AnsiConsole.MarkupLine($"[green]✓[/] 已登录 [bold]{Markup.Escape(acc.Name)}[/] [grey](uid {session.UserId})[/]");
        return true;
    }

    /// <summary>Character (sub-account) list: pick one to enter, create a new one, or go back (null).</summary>
    private static async Task<string?> PickRoleAsync(ShadowConfig cfg, AccountStore store, SavedAccount acc, NeteaseSession session, string gameId)
    {
        var x19 = YggdrasilSetup.CreateModProvider(cfg);
        const string create = "<<create>>", back = "<<back>>";
        while (true)
        {
            List<string> roles;
            try
            {
                roles = await AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(new Style(Color.MediumPurple))
                    .StartAsync("正在获取角色…", _ => x19.QueryRolesAsync(gameId, session.UserId, session.UserToken));
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ 获取角色失败：[/]{Markup.Escape(ex.GetBaseException().Message)}");
                return null;
            }

            var items = roles.OrderByDescending(r => r == acc.LastRole).ToList();
            items.Add(create);
            items.Add(back);
            var pick = ClickMenu.Select(roles.Count == 0
                    ? $"[yellow]「{Markup.Escape(acc.Name)}」在这个服务器还没有角色，先新建一个：[/]"
                    : $"[bold]「{Markup.Escape(acc.Name)}」选择角色进入：[/]",
                items, r => r switch
                {
                    create => "[green]+ 新建角色[/]",
                    back => "[grey]← 返回[/]",
                    _ => "▶ [bold]" + Markup.Escape(r) + "[/]" + (r == acc.LastRole ? "  [grey]上次使用[/]" : ""),
                });

            if (pick == back) return null;
            if (pick != create)
            {
                acc.LastRole = pick;
                store.Save();
                AnsiConsole.MarkupLine($"[green]✓[/] 角色 [bold]{Markup.Escape(pick)}[/]");
                return pick;
            }

            var name = AnsiConsole.Prompt(new TextPrompt<string>("新角色名：")
                .Validate(n => n.Trim().Length > 0 ? ValidationResult.Success() : ValidationResult.Error("[red]名字不能为空[/]"))).Trim();
            try
            {
                await AnsiConsole.Status().Spinner(Spinner.Known.Dots).SpinnerStyle(new Style(Color.MediumPurple))
                    .StartAsync("正在创建角色…", _ => x19.CreateRoleAsync(gameId, session.UserId, session.UserToken, name));
                AnsiConsole.MarkupLine($"[green]✓[/] 已创建角色 [bold]{Markup.Escape(name)}[/]");
            }
            catch (Exception ex)
            {
                AnsiConsole.MarkupLine($"[red]✗ 创建失败：[/]{Markup.Escape(ex.GetBaseException().Message)}");
            }
            AnsiConsole.WriteLine();
        }
    }

    private static async Task AddAccountAsync(ShadowConfig cfg, AccountStore store)
    {
        AnsiConsole.Write(new Rule("[green]添加账号[/]").LeftJustified());
        var channel = ClickMenu.Select("登录方式：", new[] { "netease", "cookie", "" },
            c => c switch { "netease" => "网易邮箱（邮箱 + 密码）", "cookie" => "Cookie（sauth_json）", _ => "[grey]← 返回[/]" });
        if (channel == "") return;

        var acc = new SavedAccount { Channel = channel };
        AskCredentials(acc);
        acc.Name = AskName(store, channel == "netease" ? acc.Email : $"账号{store.Accounts.Count + 1}", null);

        var session = await LoginAsync(cfg, acc.Channel, acc.Email, acc.Secret);
        if (session is null && !ClickMenu.Confirm("登录没成功，仍然保存这个账号？")) return;
        if (session is not null) { acc.UserId = session.UserId; Sessions[acc] = session; }

        store.Accounts.Add(acc);
        store.Save();
        AnsiConsole.MarkupLine($"[green]✓[/] 已保存「{Markup.Escape(acc.Name)}」");
        AnsiConsole.WriteLine();
    }

    private static async Task ManageAsync(ShadowConfig cfg, AccountStore store)
    {
        AnsiConsole.Write(new Rule("[blue]管理账号[/]").LeftJustified());
        var picked = ClickMenu.Select("选择账号：",
            store.Ordered().Select(a => new Choice(Kind.Account, AccountLabel(a), a))
                .Append(new Choice(Kind.Back, "[grey]← 返回[/]")).ToList(), c => c.Label);
        if (picked.Account is not { } acc) return;

        const string rename = "重命名", edit = "修改登录信息", delete = "[red]删除[/]", back = "[grey]← 返回[/]";
        var action = ClickMenu.Select($"对「{Markup.Escape(acc.Name)}」：", new[] { rename, edit, delete, back }, s => s);

        switch (action)
        {
            case rename:
                acc.Name = AskName(store, acc.Name, acc);
                store.Save();
                AnsiConsole.MarkupLine("[green]✓[/] 已重命名");
                break;
            case edit:
                AskCredentials(acc);
                Sessions.Remove(acc);
                var session = await LoginAsync(cfg, acc.Channel, acc.Email, acc.Secret);
                if (session is not null) { acc.UserId = session.UserId; Sessions[acc] = session; }
                store.Save();
                AnsiConsole.MarkupLine("[green]✓[/] 已更新");
                break;
            case delete:
                if (ClickMenu.Confirm($"确定删除「{Markup.Escape(acc.Name)}」？"))
                {
                    store.Accounts.Remove(acc);
                    Sessions.Remove(acc);
                    store.Save();
                    AnsiConsole.MarkupLine("[green]✓[/] 已删除");
                }
                break;
        }
        AnsiConsole.WriteLine();
    }

    private static void AskCredentials(SavedAccount acc)
    {
        if (acc.Channel == "netease")
        {
            var email = new TextPrompt<string>("邮箱：")
                .Validate(s => s.Contains('@') ? ValidationResult.Success() : ValidationResult.Error("[red]不像邮箱[/]"));
            if (!string.IsNullOrEmpty(acc.Email)) email.DefaultValue(acc.Email);
            acc.Email = AnsiConsole.Prompt(email).Trim();
            acc.Password = AnsiConsole.Prompt(new TextPrompt<string>("密码：").Secret());
            return;
        }
        var input = AnsiConsole.Prompt(new TextPrompt<string>("粘贴 sauth_json（或 cookie 文件路径）：")).Trim().Trim('"');
        acc.Cookie = File.Exists(input) ? File.ReadAllText(input).Trim() : input;
    }

    private static string AskName(AccountStore store, string suggestion, SavedAccount? self)
    {
        var prompt = new TextPrompt<string>("给这个账号起个名字：")
            .Validate(n =>
            {
                n = n.Trim();
                if (n.Length == 0) return ValidationResult.Error("[red]名字不能为空[/]");
                return store.Accounts.Any(a => a != self && a.Name.Equals(n, StringComparison.OrdinalIgnoreCase))
                    ? ValidationResult.Error("[red]已有同名账号[/]")
                    : ValidationResult.Success();
            });
        if (!string.IsNullOrWhiteSpace(suggestion)) prompt.DefaultValue(suggestion);
        return AnsiConsole.Prompt(prompt).Trim();
    }

    private static async Task<NeteaseSession?> LoginAsync(ShadowConfig cfg, string channel, string email, string secret)
    {
        try
        {
            return await AnsiConsole.Status()
                .Spinner(Spinner.Known.Dots)
                .SpinnerStyle(new Style(Color.MediumPurple))
                .StartAsync("正在登录…", _ => YggdrasilSetup.LoginAsync(cfg, channel, email, secret));
        }
        catch (Exception ex)
        {
            AnsiConsole.MarkupLine($"[red]✗ 登录失败：[/]{Markup.Escape(ex.GetBaseException().Message)}");
            AnsiConsole.WriteLine();
            return null;
        }
    }

    private static string AccountLabel(SavedAccount a)
    {
        var meta = a.ChannelLabel + " · " + (a.Channel == "netease" ? a.Email : "sauth_json");
        if (a.UserId != 0) meta += $" · uid {a.UserId}";
        if (a.LastUsed is { } t) meta += $" · 上次 {t:MM-dd HH:mm}";
        return $"[bold]{Markup.Escape(a.Name)}[/]  [grey]{Markup.Escape(meta)}[/]";
    }

    /// <summary>First run: imports a legacy LoginChannel config / cookie.txt from shadow.json as one account.</summary>
    private static void ImportLegacy(ShadowConfig cfg, AccountStore store)
    {
        if (store.Accounts.Count > 0) return;
        var cookieFile = Path.Combine(AppContext.BaseDirectory, "cookie.txt");
        SavedAccount? acc = null;
        if (cfg.LoginChannel.Trim().Equals("netease", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrEmpty(cfg.LoginAccount) && !string.IsNullOrEmpty(cfg.LoginPassword))
        {
            acc = new SavedAccount { Name = cfg.LoginAccount, Channel = "netease", Email = cfg.LoginAccount, Password = cfg.LoginPassword };
        }
        else
        {
            var cookie = !string.IsNullOrWhiteSpace(cfg.LoginCookie) ? cfg.LoginCookie
                : File.Exists(cookieFile) ? File.ReadAllText(cookieFile).Trim() : "";
            if (cookie.Length > 0) acc = new SavedAccount { Name = "默认账号", Channel = "cookie", Cookie = cookie };
        }
        if (acc is null) return;
        store.Accounts.Add(acc);
        store.Save();
        AnsiConsole.MarkupLine($"[grey]已从旧配置导入账号「{Markup.Escape(acc.Name)}」[/]");
    }
}
