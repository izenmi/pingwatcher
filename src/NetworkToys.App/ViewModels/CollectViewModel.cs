using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using Microsoft.Win32;
using NetworkToys.App.Mvvm;
using NetworkToys.App.Services;
using NetworkToys.Core.Terminal;

namespace NetworkToys.App.ViewModels;

/// <summary>収集する機器 1 台ぶんの画面状態。</summary>
public sealed class CollectRowViewModel(DeviceEntry entry) : ObservableObject
{
    private string _host = entry.Host;
    private bool _useSsh = entry.UseSsh;
    private string _userName = entry.UserName;
    private string _memo = entry.Memo;
    private string _status = "◌ 待機";
    private string _password = "";
    private string _enablePassword = "";

    public string Host
    {
        get => _host;
        set => SetProperty(ref _host, value);
    }

    /// <summary>SSH でつなぐか。外すと Telnet。ポートは方式から決まる。</summary>
    public bool UseSsh
    {
        get => _useSsh;
        set
        {
            if (SetProperty(ref _useSsh, value))
                OnPropertyChanged(nameof(Method));
        }
    }

    /// <summary>コンボに出す選択肢。既定は SSH。</summary>
    public static IReadOnlyList<string> Methods { get; } = ["SSH", "Telnet"];

    /// <summary>コンボの選択。真偽値のチェックより「どちらを選ぶか」が分かりやすい。</summary>
    public string Method
    {
        get => UseSsh ? "SSH" : "Telnet";
        set => UseSsh = !string.Equals(value, "Telnet", StringComparison.OrdinalIgnoreCase);
    }

    public int Port => UseSsh ? DeviceEntry.SshPort : DeviceEntry.TelnetPort;

    public string Memo
    {
        get => _memo;
        set => SetProperty(ref _memo, value);
    }

    public string UserName
    {
        get => _userName;
        set => SetProperty(ref _userName, value);
    }

    /// <summary>
    /// ログインパスワード。<b>設定ファイルにも引き継ぎファイルにも入れない。</b>
    /// 画面は伏せ字（PasswordBox）で受け、ここへ一方通行で流し込む。
    /// </summary>
    public string Password
    {
        get => _password;
        set => SetProperty(ref _password, value);
    }

    public string EnablePassword
    {
        get => _enablePassword;
        set => SetProperty(ref _enablePassword, value);
    }

    /// <summary>記号と文字を併記する（色だけで状態を表さない決まり）。</summary>
    public string Status
    {
        get => _status;
        set => SetProperty(ref _status, value);
    }

    public void ClearSecrets()
    {
        Password = "";
        EnablePassword = "";
    }

    public DeviceEntry ToEntry() => new(Host, UseSsh, UserName, Memo);
}

/// <summary>
/// Cisco 機器へ入ってコマンド出力を集める画面。
///
/// 認証情報は<b>機器ごとに違う</b>ので行ごとに持つ。<b>覚えるのはユーザー名だけ</b>で、
/// パスワードは毎回入力してもらう（設定ファイルにも、画面の PNG 保存にも、
/// 管理者昇格の引き継ぎファイルにも残さない）。
/// </summary>
public sealed class CollectViewModel : ObservableObject
{
    private string _commandText = RecommendedCommands.Ios;
    private string _status = "宛先リストから取り込むか、機器を直接書いて「収集を開始」を押します。";
    private string _notice = "";
    private bool _isBusy;
    private int _connectSeconds = 10;
    private int _idleSeconds = 15;
    private string _lastFolder = "";
    private CancellationTokenSource? _cts;

    public CollectViewModel()
    {
        Presets = [.. RecommendedCommands.Presets.Select(p => new CollectPreset(p.Name, p.Commands))];
        _selectedPreset = Presets[0];

        AddDeviceCommand = new RelayCommand(() => AddRow(new DeviceEntry("", true, "", "")));
        ResetCommandsCommand = new RelayCommand(() => CommandText = SelectedPreset.Commands);
        ImportCsvCommand = new RelayCommand(ImportCsv);
        SaveCsvTemplateCommand = new RelayCommand(SaveCsvTemplate);
        RemoveDeviceCommand = new RelayCommand<CollectRowViewModel>(RemoveRow);
        ImportFromTargetsCommand = new RelayCommand(() => RequestImport?.Invoke(this, EventArgs.Empty));
        StartCommand = new RelayCommand(() => _ = RunAsync(), () => !IsBusy && Rows.Count > 0);
        RetryFailedCommand = new RelayCommand(
            () => _ = RunAsync(failedOnly: true),
            () => !IsBusy && Rows.Any(r => IsFailedStatus(r.Status)));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        OpenFolderCommand = new RelayCommand(OpenFolder, () => _lastFolder.Length > 0);

        // 前回のコマンドがあれば引き継ぐ。無ければ機種プリセットの初期値のまま
        if (Settings.Current.CollectCommands.Length > 0)
            _commandText = Settings.Current.CollectCommands;

        foreach (DeviceEntry entry in DeviceListParser.Parse(Settings.Current.CollectDevices, defaultUseSsh: true).Devices)
            AddRow(entry);
    }

    /// <summary>宛先リストから取り込みたい、という合図（実際の取り込みは画面側）。</summary>
    public event EventHandler? RequestImport;

    /// <summary>収集が終わって伏せ字欄を空にしてほしい、という合図。</summary>
    public event EventHandler? SecretsCleared;

    /// <summary>CSV からパスワードを取り込んだので、伏せ字欄へ映してほしいという合図。</summary>
    public event EventHandler? SecretsImported;

    public ObservableCollection<CollectRowViewModel> Rows { get; } = [];

    public IReadOnlyList<CollectPreset> Presets { get; }

    private CollectPreset _selectedPreset;

    public CollectPreset SelectedPreset
    {
        get => _selectedPreset;
        set
        {
            if (!SetProperty(ref _selectedPreset, value)) return;

            CommandText = value.Commands;
        }
    }

    public RelayCommand AddDeviceCommand { get; }

    /// <summary>コマンド欄を、選んでいる機種の既定へ戻す（2026-08-20 ユーザー指示）。</summary>
    public RelayCommand ResetCommandsCommand { get; }

    /// <summary>機器の一覧を CSV から取り込む（2026-08-20 ユーザー指示）。</summary>
    public RelayCommand ImportCsvCommand { get; }

    /// <summary>取り込みに使う CSV のひな型をファイルに書き出す。</summary>
    public RelayCommand SaveCsvTemplateCommand { get; }

    /// <summary>行の × から呼ばれる。消す行そのものを受け取る。</summary>
    public RelayCommand<CollectRowViewModel> RemoveDeviceCommand { get; }
    public RelayCommand ImportFromTargetsCommand { get; }
    public RelayCommand StartCommand { get; }

    /// <summary>「✕ 失敗」「⛔ パスワード未入力」の行だけをもう一度収集する（2026-08-20 の機能改善）。</summary>
    public RelayCommand RetryFailedCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand OpenFolderCommand { get; }

    public string CommandText
    {
        get => _commandText;
        set
        {
            if (SetProperty(ref _commandText, value))
                OnPropertyChanged(nameof(CommandSummary));
        }
    }

    /// <summary>実行するコマンドの本数と、弾いたものの内訳。</summary>
    public string CommandSummary
    {
        get
        {
            CommandListParseResult parsed = CommandListParser.Parse(CommandText);
            int blocked = parsed.Commands.Count(c => c.Risk == CommandRisk.Blocked);
            int warned = parsed.Commands.Count(c => c.Risk == CommandRisk.Warned);

            string text = $"実行 {parsed.RunnableCount} 本 / 注釈 {parsed.CommentLines} 行";

            if (blocked > 0) text += $"　⛔ 実行しません {blocked} 本";
            if (warned > 0) text += $"　△ 確認 {warned} 本";

            return text;
        }
    }

    /// <summary>弾いたコマンドの一覧（画面に理由つきで出す）。</summary>
    public IReadOnlyList<string> BlockedCommands =>
        [.. CommandListParser.Parse(CommandText).Commands
            .Where(c => c.Risk == CommandRisk.Blocked)
            .Select(c => $"⛔ {c.Command} — {c.Reason}")];

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string Notice
    {
        get => _notice;
        private set
        {
            if (SetProperty(ref _notice, value))
                OnPropertyChanged(nameof(HasNotice));
        }
    }

    public bool HasNotice => _notice.Length > 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;

            StartCommand.RaiseCanExecuteChanged();
            RetryFailedCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 同時に会話する機器の数。
    ///
    /// <b>順番に取る理由は無い</b>（機器ごとに独立した会話）が、
    /// 無制限に開くと踏み台や認証サーバ（TACACS+/AD）へ一度に殺到する。
    /// 数十台の現場で「速いが迷惑をかけない」あたりに置いてある。
    /// </summary>
    private const int MaxConcurrentDevices = 8;

    /// <summary>接続の待ち（秒）。</summary>
    public int ConnectSeconds
    {
        get => _connectSeconds;
        set => SetProperty(ref _connectSeconds, Math.Clamp(value, 3, 120));
    }

    /// <summary>コマンドの無音の待ち（秒）。</summary>
    public int IdleSeconds
    {
        get => _idleSeconds;
        set => SetProperty(ref _idleSeconds, Math.Clamp(value, 3, 300));
    }

    /// <summary>宛先リストから選ばれた分を行として足す（選ぶのは画面側）。</summary>
    public void Import(IEnumerable<(string Host, string Memo)> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);

        HashSet<string> known = [.. Rows.Select(r => r.Host)];
        int added = 0;

        foreach ((string host, string memo) in targets)
        {
            if (host.Length == 0 || !known.Add(host)) continue;

            // 前に使ったユーザー名があれば埋めておく(覚えているのはこれだけ)
            string user = Settings.Current.CollectUserNames.GetValueOrDefault(host, "");

            AddRow(new DeviceEntry(host, UseSsh: true, user, memo));
            added++;
        }

        Status = added == 0
            ? "選んだ宛先はすべて追加済みでした。"
            : $"{added} 台を追加しました。パスワードを入れてください。";
    }

    /// <summary>
    /// CSV の書き方は設定ファイルと同じ <see cref="DeviceListParser"/> に任せる
    /// （<c>宛先,ssh|telnet,ユーザー名,メモ</c>。区切りはカンマとタブ、行頭 # は注釈）。
    /// <b>2 本目の書式を作らない</b> — ひな型もこの書式で書き出す。
    /// </summary>
    private const string CsvTemplate =
        """
        # ログ採取の機器一覧。1 行 1 台で、この行のような # で始まる行は読み飛ばします。
        # 列は左から  宛先(IP かホスト名) , 接続方法(ssh か telnet) , ユーザー名 , パスワード , enable , メモ
        # ユーザー名から後ろは空でも構いません(パスワードは取り込んだ後に画面でも入れられます)。
        # ※ パスワードを書いたファイルは、使い終わったら消してください。アプリはこのファイルを書き出しません。
        192.168.1.1,ssh,admin,LoginPass,EnablePass,本社コアSW
        192.168.1.2,telnet,admin,LoginPass,,旧ルータ(enable なし)
        10.0.0.1,ssh,ops,,,パスワードは画面で入れる
        """;

    private void ImportCsv()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "CSV (*.csv)|*.csv|テキスト (*.txt)|*.txt|すべてのファイル (*.*)|*.*",
        };

        if (dialog.ShowDialog() != true) return;

        // 文字コードは自動判定(UTF-8 → cp932)。Excel が書く CSV は cp932 のことが多い
        if (DroppedText.TryRead(dialog.FileName, out string problem) is not { } text)
        {
            Status = problem;
            return;
        }

        ImportCsvText(text);
    }

    /// <summary>
    /// CSV の中身を取り込む。「CSV から取り込む」のファイル選択からも、
    /// 機器一覧へのファイルの放り込みからも来る（読み込み口はすべて放り込みに
    /// 対応させる決まり。この画面だけボタンからしか読めなかった）。
    /// </summary>
    public void ImportCsvText(string text)
    {
        DeviceListParser.CsvParseResult parsed = DeviceListParser.ParseCsv(text, defaultUseSsh: true);

        if (parsed.Devices.Count == 0)
        {
            Status = parsed.Errors.Count > 0
                ? $"読み取れませんでした: {parsed.Errors[0]}"
                : "機器の行が 1 つもありません。ひな型を保存して書き方を確かめてください。";
            return;
        }

        // 宛先リストからの取り込みと同じ足し方(既にある宛先は飛ばす)
        HashSet<string> known = [.. Rows.Select(r => r.Host)];
        int added = 0;
        int withSecrets = 0;

        foreach (DeviceListParser.ImportedDevice device in parsed.Devices)
        {
            if (!known.Add(device.Entry.Host)) continue;

            // ユーザー名が書かれていなければ、前に使ったものを埋める
            DeviceEntry filled = device.Entry.UserName.Length > 0
                ? device.Entry
                : device.Entry with { UserName = Settings.Current.CollectUserNames.GetValueOrDefault(device.Entry.Host, "") };

            CollectRowViewModel row = AddRow(filled);

            // パスワードは行の VM にだけ入れる（設定には書かない決まり。CSV の側は
            // ユーザーの手元のファイルで、書き出す口はひな型だけ）
            if (device.Password.Length > 0 || device.EnablePassword.Length > 0)
            {
                row.Password = device.Password;
                row.EnablePassword = device.EnablePassword;
                withSecrets++;
            }

            added++;
        }

        // PasswordBox はバインドを持たないので、画面側に「入れ直して」と合図する
        if (withSecrets > 0) SecretsImported?.Invoke(this, EventArgs.Empty);

        string skipped = parsed.Devices.Count - added > 0 ? $"（{parsed.Devices.Count - added} 台は追加済み）" : "";
        string errors = parsed.Errors.Count > 0 ? $"　⚠ {parsed.Errors[0]}" : "";
        string secrets = withSecrets > 0
            ? $"パスワードも {withSecrets} 台ぶん取り込みました。"
            : "パスワードを入れてください。";

        Status = $"CSV から {added} 台を追加しました{skipped}。{secrets}{errors}";
    }

    private void SaveCsvTemplate()
    {
        var dialog = new SaveFileDialog
        {
            FileName = "機器一覧.csv",
            DefaultExt = "csv",
            Filter = "CSV (*.csv)|*.csv|すべてのファイル (*.*)|*.*",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            // BOM 付き UTF-8。Excel でそのまま開いて書ける
            File.WriteAllText(dialog.FileName, CsvTemplate,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            Status = $"{Path.GetFileName(dialog.FileName)} に書き出しました。書き換えて「CSV から取り込む」で読み込めます。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"保存できませんでした: {ex.Message}";
        }
    }

    /// <summary>
    /// 全行へ同じ認証情報を流し込む（2026-08-20 の UX 改善。共通 TACACS+/AD の現場向け）。
    ///
    /// <b>欄単位</b>で見る — ユーザー名は前回値の自動補完で埋まっている行が多く、
    /// 行単位の「空だけ」では肝心のパスワードが入らない。<paramref name="overwrite"/> が
    /// 偽なら空の欄だけ、真なら入力済みも置き換える。引数の空文字は「その欄は触らない」。
    /// </summary>
    public void FillCredentials(string user, string password, string enable, bool overwrite)
    {
        int touched = 0;
        bool secrets = false;

        foreach (CollectRowViewModel row in Rows)
        {
            bool changed = false;

            if (user.Length > 0 && (overwrite || row.UserName.Length == 0))
            {
                row.UserName = user;
                changed = true;
            }

            if (password.Length > 0 && (overwrite || row.Password.Length == 0))
            {
                row.Password = password;
                changed = true;
                secrets = true;
            }

            if (enable.Length > 0 && (overwrite || row.EnablePassword.Length == 0))
            {
                row.EnablePassword = enable;
                changed = true;
                secrets = true;
            }

            if (changed) touched++;
        }

        // PasswordBox はバインドを持たないので、画面側に映してもらう（CSV 取り込みと同じ経路）
        if (secrets) SecretsImported?.Invoke(this, EventArgs.Empty);

        Status = touched > 0
            ? $"{touched} 行に認証情報を入れました。"
            : "入れる先がありませんでした（すべて入力済みです。上書きするときはチェックを入れてください）。";
    }

    private CollectRowViewModel AddRow(DeviceEntry entry)
    {
        var row = new CollectRowViewModel(entry);

        Rows.Add(row);
        StartCommand.RaiseCanExecuteChanged();
        RetryFailedCommand.RaiseCanExecuteChanged();

        return row;
    }

    private void RemoveRow(CollectRowViewModel? row)
    {
        if (row is null) return;

        Rows.Remove(row);
        StartCommand.RaiseCanExecuteChanged();
        RetryFailedCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// 失敗した行かどうか。<b>✕（失敗）と ⛔（パスワード未入力）だけ</b>を失敗とみなす。
    /// △ 一部失敗は含めない（出力は取れている）。internal は自己診断のため。
    /// </summary>
    internal static bool IsFailedStatus(string status)
        => status.StartsWith('✕') || status.StartsWith('⛔');

    private async Task RunAsync(bool failedOnly = false)
    {
        CommandListParseResult parsed = CommandListParser.Parse(CommandText);

        string[] commands = [.. parsed.Commands
            .Where(c => c.Risk != CommandRisk.Blocked)
            .Select(c => c.Command)];

        if (commands.Length == 0)
        {
            Status = "実行できるコマンドがありません。";
            return;
        }

        // 「失敗だけ再実行」は ✕（失敗）と ⛔（パスワード未入力で飛ばした）の行だけ。
        // △ 一部失敗は対象にしない — 出力は取れており、上書き再収集は証跡を汚す
        CollectRowViewModel[] targets = [.. Rows.Where(r =>
            r.Host.Trim().Length > 0 && (!failedOnly || IsFailedStatus(r.Status)))];

        if (targets.Length == 0)
        {
            Status = failedOnly ? "失敗した機器はありません。" : "機器が 1 台も入っていません。";
            return;
        }

        IsBusy = true;
        _cts = new CancellationTokenSource();

        // 保存先は collect 直下（2026-08-20 ユーザー指示で回ごとのフォルダをやめた）。
        // ファイル名に機器名と日時が入るので、同じ場所でも混ざらない
        string folder = DeviceCollector.RootDirectory;

        var options = new CiscoSessionOptions { IdleTimeout = TimeSpan.FromSeconds(IdleSeconds) };
        var progress = new Progress<CollectProgress>(p =>
        {
            foreach (CollectRowViewModel row in Rows.Where(r => r.Host == p.Host))
                // 進行中は ▶。● は Ping の「応答あり」専用（記号の規約は CLAUDE.md）
                row.Status = "▶ " + p.Message;
        });

        int done = 0;
        int failed = 0;

        // 機器ごとの会話は独立しているので、順番に待つ理由がない（2026-08-18 ユーザー指示）。
        // ただし無制限に開くと、踏み台や認証サーバに一度に殺到する。少しずつ開ける
        using var gate = new SemaphoreSlim(MaxConcurrentDevices);

        // 合図は先に控える（終わったあとに _cts を捨てるので、中で持ち回らない）
        CancellationToken token = _cts.Token;

        // 待ちの間は UI スレッドへ戻る（ConfigureAwait(false) を付けない）。
        // 行の状態も件数もここでしか触らないので、鍵を持ち回らずに済む
        async Task CollectOneAsync(CollectRowViewModel row)
        {
            if (row.Password.Length == 0)
            {
                row.Status = "⛔ パスワード未入力";
                failed++;
                return;
            }

            RememberUserName(row);

            var request = new CollectRequest(
                row.Host, row.Port, row.UseSsh,
                new DeviceCredentials(row.UserName, row.Password, row.EnablePassword),
                row.Memo);

            await gate.WaitAsync(token);

            DeviceCollectionResult result;

            try
            {
                result = await DeviceCollector.CollectAsync(
                    request, commands, options, TimeSpan.FromSeconds(ConnectSeconds),
                    progress, token);
            }
            finally
            {
                gate.Release();
            }

            string? saveError = DeviceCollector.Save(folder, result);

            if (result.FailureMessage is { } failure)
            {
                row.Status = $"✕ 失敗: {failure}";
                failed++;
            }
            else if (saveError is not null)
            {
                row.Status = $"✕ {saveError}";
                failed++;
            }
            else
            {
                int problems = result.Commands.Count(c => c.Problem is not null);

                // 特権モードに入れていないと show running-config などが軒並み断られる。
                // 表の上で分かるようにする（原因がコマンド側に見えてしまうため）
                string mode = result.ReachedEnable ? "" : "（ユーザーモードのまま）";

                row.Status = problems == 0
                    ? $"✓ 完了 {result.Commands.Count} 本{mode}"
                    : $"△ 一部失敗 {result.Commands.Count - problems}/{result.Commands.Count} 本{mode}";

                done++;
            }

            Status = $"{done + failed}/{targets.Length} 台（成功 {done} / 失敗 {failed}）";
        }

        try
        {
            Status = $"0/{targets.Length} 台（同時に {Math.Min(MaxConcurrentDevices, targets.Length)} 台まで）";

            await Task.WhenAll([.. targets.Select(CollectOneAsync)]);

            _lastFolder = folder;
            OpenFolderCommand.RaiseCanExecuteChanged();
            Status = failed > 0
                ? $"収集しました。{done} 台成功 / {failed} 台失敗　保存先: {folder}　"
                  + "認証情報を入れ直してから「失敗した機器だけ再実行」でやり直せます。"
                : $"収集しました。{done} 台成功 / {failed} 台失敗　保存先: {folder}";
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。ここまでの結果は保存してあります。";

            foreach (CollectRowViewModel row in targets.Where(r => r.Status.StartsWith('●')))
                row.Status = "⊘ 中断";
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "CollectViewModel.RunAsync");
            Status = $"収集に失敗しました: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;

            // 集め終わったら鍵は捨てる。画面の伏せ字欄も空にしてもらう
            foreach (CollectRowViewModel row in Rows)
                row.ClearSecrets();

            SecretsCleared?.Invoke(this, EventArgs.Empty);
            RetryFailedCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>ユーザー名だけ覚える。パスワードは覚えない。</summary>
    private static void RememberUserName(CollectRowViewModel row)
    {
        if (row.UserName.Length == 0) return;

        try
        {
            Settings.Current.CollectUserNames[row.Host] = row.UserName;
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 覚えられなくても収集は続く
        }
    }

    private void OpenFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = _lastFolder,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "CollectViewModel.OpenFolder");
        }
    }

    /// <summary>アプリを閉じるときに、機器の一覧とコマンドだけ覚える。</summary>
    public void Save()
    {
        try
        {
            Settings.Current.CollectDevices = DeviceListParser.Format(Rows.Select(r => r.ToEntry()));
            Settings.Current.CollectCommands = CommandText;
            Settings.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 覚えられなくても動作には影響しない
        }
    }

    public void Reset()
    {
        _cts?.Cancel();

        foreach (CollectRowViewModel row in Rows)
            row.ClearSecrets();

        SecretsCleared?.Invoke(this, EventArgs.Empty);

        Rows.Clear();
        StartCommand.RaiseCanExecuteChanged();
        RetryFailedCommand.RaiseCanExecuteChanged();
        CommandText = RecommendedCommands.Ios;
        Status = "宛先リストから取り込むか、機器を直接書いて「収集を開始」を押します。";
        Notice = "";
    }
}

/// <summary>コマンドのプリセット（機種ごと）。</summary>
public sealed record CollectPreset(string Name, string Commands);
