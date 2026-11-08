using System.IO;
using WtfNel.Core;
using WtfNel.Core.Auth;
using WtfNel.Core.Auth.Login;
using WtfNel.Core.Auth.Yggdrasil;
using WtfNel.Core.Launch;
using WtfNel.Core.Logging;

namespace WtfNel.Ui.Services;

/// <summary>Shared application state: configuration, saved accounts, live sessions and the running game.</summary>
public static class AppState
{
    private static CancellationTokenSource? _game;
    private static readonly Dictionary<SavedAccount, Task<NeteaseSession>> PendingLogins = new();

    public static string ConfigPath { get; } = Path.Combine(AppContext.BaseDirectory, "shadow.json");
    public static ShadowConfig Config { get; private set; } = new();
    public static AccountStore Store { get; private set; } = new();
    public static UiSettings Ui { get; private set; } = new();
    public static Dictionary<SavedAccount, NeteaseSession> Sessions { get; } = new();

    public static bool IsRunning => _game is not null;

    public static event Action? RunningChanged;
    public static event Action? AccountsChanged;
    public static event Action<SavedAccount>? SessionChanged;

    public static SavedAccount? RunningAccount { get; private set; }
    public static string? RunningRole { get; private set; }

    public static void Load()
    {
        Config = ShadowConfig.Load(ConfigPath);
        Store = AccountStore.Load();
        Ui = UiSettings.Load();
        Log.Verbose = false;
        Task.Run(() =>
        {
            try { GameDirLocator.Resolve(Config); }
            catch (Exception ex) { Log.Warn("[gamedir] " + ex.GetBaseException().Message); }
        });
    }

    public static void SaveConfig() => Config.Save(ConfigPath);

    public static void SaveAccounts()
    {
        Store.Save();
        AccountsChanged?.Invoke();
    }

    /// <summary>Logs the account in on a worker thread (captcha prompts are shown as dialogs) and caches the session.</summary>
    public static Task<NeteaseSession> LoginAsync(SavedAccount account)
    {
        if (PendingLogins.TryGetValue(account, out var pending)) return pending;
        var task = LoginCoreAsync(account);
        PendingLogins[account] = task;
        return task;
    }

    public static bool IsLoggingIn(SavedAccount account) => PendingLogins.ContainsKey(account);

    private static async Task<NeteaseSession> LoginCoreAsync(SavedAccount account)
    {
        try
        {
            var session = await Task.Run(() => YggdrasilSetup.LoginAsync(Config, account.Channel, account.Email, account.Secret, new UiCaptchaPrompt()));
            Sessions[account] = session;
            account.UserId = session.UserId;
            SaveAccounts();
            SessionChanged?.Invoke(account);
            return session;
        }
        finally
        {
            PendingLogins.Remove(account);
        }
    }

    public static Task<List<string>> QueryRolesAsync(NeteaseSession session) =>
        Task.Run(() => YggdrasilSetup.CreateModProvider(Config).QueryRolesAsync(Config.GameIdOrDefault, session.UserId, session.UserToken));

    public static Task CreateRoleAsync(NeteaseSession session, string name) =>
        Task.Run(() => YggdrasilSetup.CreateModProvider(Config).CreateRoleAsync(Config.GameIdOrDefault, session.UserId, session.UserToken, name));

    /// <summary>Starts the client with the given account and character and completes when the client exits.</summary>
    public static async Task RunGameAsync(SavedAccount account, NeteaseSession session, string role)
    {
        if (_game is not null) throw new InvalidOperationException("游戏已经在运行");
        YggdrasilSetup.UseSession(session);
        Config.UserName = role;
        Config.Uuid = Skip32.RoleUuid(role, session.UserId);
        account.LastRole = role;
        account.LastUsed = DateTime.Now;
        Ui.LastAccount = account.Name;
        Ui.Save();
        SaveAccounts();

        _game = new CancellationTokenSource();
        RunningAccount = account;
        RunningRole = role;
        RunningChanged?.Invoke();
        try
        {
            var token = _game.Token;
            await Task.Run(() => GameRunner.Run(Config, token));
        }
        finally
        {
            _game.Dispose();
            _game = null;
            RunningAccount = null;
            RunningRole = null;
            RunningChanged?.Invoke();
        }
    }

    public static void StopGame() => _game?.Cancel();

    public static void Logout(SavedAccount account)
    {
        if (Sessions.Remove(account)) SessionChanged?.Invoke(account);
    }
}
