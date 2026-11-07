using ProjectDeck.Core;
using ProjectDeck.Core.Settings;
using ProjectDeck.Services;

namespace ProjectDeck;

static class Program
{
    private const string MutexName = @"Local\ProjectDeck.SingleInstance";
    private const string ShowEventName = @"Local\ProjectDeck.Show";

    /// <summary>ランチャー本体の入口</summary>
    [STAThread]
    static void Main(string[] args)
    {
        // 二重起動の防止：2つ目は、起動済みのランチャーを表示させて終了する（仕様書 5.6）
        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            try { EventWaitHandle.OpenExisting(ShowEventName).Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return;
        }
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);

        Logger.Init(AppPaths.LogsDirectory, "app");
        Logger.Info($"起動 {Environment.ProcessPath} {string.Join(' ', args)}");
        Application.ThreadException += (_, e) => Logger.Error("UIスレッドの例外: " + e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.Error("未処理の例外: " + e.ExceptionObject);

        ApplicationConfiguration.Initialize();

        // settings.toml が無ければ同梱の原本から作る
        var settingsStore = new SettingsStore(AppPaths.SettingsFile);
        try { settingsStore.EnsureExists(AppPaths.BundledSettingsFile); }
        catch (IOException ex) { Logger.Warn("settings.toml を作れません: " + ex.Message); }
        var settings = settingsStore.Load();

        // PC起動時は delay_sec 秒待つ（ログオン直後の負荷を避ける）
        var autostarted = args.Contains(AutoStart.Argument, StringComparer.OrdinalIgnoreCase);
        if (autostarted && settings.Settings is { Startup.DelaySec: > 0 } s)
            Thread.Sleep(TimeSpan.FromSeconds(s.Startup.DelaySec));

        using var form = new MainForm(settingsStore, settings);
        _ = form.Handle; // ホットキー登録・Invoke のためにハンドルを先に作る

        // 2つ目の起動からの「表示して」を待つ
        var waiter = new Thread(() =>
        {
            while (showEvent.WaitOne())
            {
                if (form.IsDisposed) break;
                try { form.BeginInvoke(form.ShowLauncher); }
                catch (InvalidOperationException) { break; }
            }
        }) { IsBackground = true, Name = "ShowEventWaiter" };
        waiter.Start();

        if (settings.Settings?.Startup.ShowOnStart ?? true) form.Show();
        Application.Run(new ApplicationContext(form));
    }
}
