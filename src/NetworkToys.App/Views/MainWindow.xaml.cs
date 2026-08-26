using System.ComponentModel;
using System.IO;
using System.Reflection;
using Microsoft.Win32;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NetworkToys.App.Services;
using NetworkToys.App.ViewModels;
using NetworkToys.Core.Storage;

namespace NetworkToys.App.Views;

public partial class MainWindow : Window
{
    private readonly ShellViewModel _shell = new();

    /// <summary>差分の左右で縦スクロールを写している最中か（写した先の通知で往復しないため）。</summary>
    private bool _syncingDiffPanes;

    public MainWindow() : this(null)
    {
    }

    /// <param name="handover">
    /// 管理者として起動し直したときに前のプロセスから渡された状態。通常起動では null。
    /// </param>
    public MainWindow(NetworkToys.Core.Storage.HandoverDocument? handover)
    {
        InitializeComponent();

        // 既定の大きさは広い画面向けなので、狭い画面では収まるところまで縮める
        FitToScreen();

        // 「その他」の見出しは件数を数えて組み立てる（直書きにしない）
        UpdateOtherHeader();

        // 管理者で動いているときは、ドロップ欄に「届かない」旨の断りを足す
        MarkBlockedDropTargets();

        DataContext = _shell;
        UpdateThemeToggle();

        // 最前面固定を覚えている場合は復元する
        TopmostMenuItem.IsChecked = Settings.Current.Topmost;
        Topmost = Settings.Current.Topmost;

        UpdateScaleMenu();

        // 文字を大きくしたら列幅も比例させる。文字だけ大きくすると、
        // 幅がピクセルで決まっている列で文字が切れる
        UiScale.Changed += ratio =>
        {
            ViewModels.ColumnLayout.Instance.Scale(ratio);
            ViewModels.TableColumns.Instance.Scale(ratio);
        };

        // メニューに表記したショートカットの実体。表記(InputGestureText)は飾りなので、
        // ここに足したらメニューの表記も揃えること
        InputBindings.Add(new KeyBinding(
            new Mvvm.RelayCommand(StartFromShortcut), new KeyGesture(Key.F5)));
        InputBindings.Add(new KeyBinding(
            new Mvvm.RelayCommand(StopFromShortcut), new KeyGesture(Key.F5, ModifierKeys.Shift)));
        InputBindings.Add(new KeyBinding(
            new Mvvm.RelayCommand(() => OnScreenshot(this, new RoutedEventArgs())),
            new KeyGesture(Key.F12)));

        _shell.DeviceCompare.RequestScrollIntoView += OnScrollDiffIntoView;
        _shell.Aci.RequestScrollIntoView += (_, index) => ScrollIntoView(AciDiffBefore, index);

        // 「すべて消す」でキーを捨てたら、画面の伏せ字欄も空にする
        // (PasswordBox は中身をバインドできないので VM から知らせてもらう)
        _shell.Meraki.ApiKeyCleared += (_, _) => MerakiKeyBox.Clear();

        // 収集タブ: 宛先リストからの取り込みと、収集後の伏せ字欄の後始末
        _shell.Collect.RequestImport += (_, _) => ImportTargetsIntoCollect();
        _shell.Collect.SecretsCleared += (_, _) => ClearCollectPasswordBoxes();

        // CSV から取り込んだパスワードを伏せ字欄へ映す。行はいま足されたばかりで
        // 実体化がまだなので、レイアウトが済んでから流し込む
        _shell.Collect.SecretsImported += (_, _) => Dispatcher.BeginInvoke(
            FillCollectPasswordBoxes, System.Windows.Threading.DispatcherPriority.Loaded);

        // 宛先リストの名前を聞くのも画面の仕事（Ping と TCP で別々に持つ）
        // 別の名前を打ったのに既存とぶつかるときだけ、上書きしてよいか聞く。
        // いま選んでいる名前のままの保存（意図した更新）には聞かない（2026-08-20 の UI 改善）
        string? AskNameGuarded(string title, string message, string initial, Func<string, bool> exists)
        {
            string? name = TextPromptDialog.Ask(this, title, message, initial);

            if (name is null || name == initial || !exists(name)) return name;

            return ConfirmDialog.Confirm(
                this, title, $"「{name}」は既にあります。上書きしますか？", okLabel: "上書きする")
                ? name
                : null;
        }

        _shell.Monitor.AskListName = initial => AskNameGuarded(
            "宛先リストに名前を付けて残す",
            "いまの宛先リストに名前を付けて残します。",
            initial,
            name => _shell.Monitor.SavedLists.Contains(name));

        _shell.Tcp.AskListName = initial => AskNameGuarded(
            "宛先リストに名前を付けて残す",
            "いまの宛先リストに名前を付けて残します。",
            initial,
            name => _shell.Tcp.SavedLists.Contains(name));

        // 消すのは取り消せないので必ず聞く
        _shell.Monitor.ConfirmDelete = message => ConfirmDialog.Confirm(
            this, "宛先リストを消す", message, okLabel: "消す");

        _shell.Monitor.ConfirmClear = message => ConfirmDialog.Confirm(
            this, "履歴を消去", message, okLabel: "消す");

        _shell.Tcp.ConfirmClear = message => ConfirmDialog.Confirm(
            this, "履歴を消去", message, okLabel: "消す");

        _shell.IpConfig.ConfirmDelete = message => ConfirmDialog.Confirm(
            this, "プリセットを消す", message, okLabel: "消す");

        // 受信一覧の消去（FTP/TFTP/SFTP/syslog/Trap の 5 画面共通）
        foreach (ViewModels.FileServerViewModel server in
                 new ViewModels.FileServerViewModel[]
                 { _shell.Ftp, _shell.Tftp, _shell.Sftp, _shell.Syslog, _shell.SnmpTrap })
        {
            server.ConfirmClear = message => ConfirmDialog.Confirm(
                this, "一覧を消す", message, okLabel: "消す");
        }

        _shell.Tcp.ConfirmDelete = message => ConfirmDialog.Confirm(
            this, "宛先リストを消す", message, okLabel: "消す");

        // ひな型の名前を聞くのは画面の仕事（VM から窓を開かない）
        _shell.Verify.AskTemplateName = initial => AskNameGuarded(
            "ひな型に残す",
            "いまの試験項目に名前を付けて残します。",
            initial,
            name => _shell.Verify.Templates.Any(t => t.Name == name));

        _shell.Verify.ConfirmDelete = message => ConfirmDialog.Confirm(
            this, "ひな型を消す", message, okLabel: "消す");

        // ファイル転送も窓を開くのは画面の仕事。削除と上書きは取り消せないので必ず聞く
        _shell.Transfer.Confirm = message => ConfirmDialog.Confirm(
            this, "ファイル転送", message, okLabel: "実行する");

        _shell.Transfer.Ask = message => TextPromptDialog.Ask(this, "ファイル転送", message);

        // APIC の証明書を受け入れるかも画面の仕事。指紋を見比べてもらってから通す
        _shell.Aci.ConfirmFingerprint = message => ConfirmDialog.Confirm(
            this, "APIC の証明書を確認", message, okLabel: "この指紋を受け入れる");

        // VM から PasswordBox の中身は書けないので、消す合図だけ受け取る
        _shell.Aci.PasswordCleared += (_, _) => AciPasswordBox.Clear();


        // WFP の記録はシステム全体に効く設定なので、立てる前に内容を確認してもらう
        _shell.Wfp.ConfirmEnableCollection += () => ConfirmDialog.Confirm(
            this,
            "ネットワークイベントの記録を有効にする",
            "Windows に、遮断を含むネットワークイベントの記録を始めさせます。\n" +
            "これは PC 全体に効く設定で、他のアプリからも見えます。\n\n" +
            "NetworkToys を閉じるときに元へ戻します。続けますか？",
            okLabel: "有効にする");

        if (handover is not null)
            ApplyHandover(handover);
    }

    /// <summary>
    /// 昇格前の状態を書き戻す。宛先リストと設定は settings.json から
    /// すでに読めているので、ここで扱うのはファイルに残らないものだけ。
    /// </summary>
    private void ApplyHandover(NetworkToys.Core.Storage.HandoverDocument handover)
    {
        try
        {
            Services.HandoverService.Apply(handover, _shell);

            if (handover.WindowWidth > 0 && handover.WindowHeight > 0)
            {
                Left = handover.WindowLeft;
                Top = handover.WindowTop;
                Width = handover.WindowWidth;
                Height = handover.WindowHeight;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }

            if (handover.WindowMaximized)
                WindowState = WindowState.Maximized;

            // 束ねたタブも探せるよう内側までたどる。見つからなければ既定のまま
            if (FindTabByHeader(MainTabs, handover.SelectedTab) is { } saved)
                Show(saved);

            // 止まっていたなら止まったまま。動いていたなら測り直しではなく続きから
            if (handover.WasRunning && _shell.Monitor.StartCommand.CanExecute(null))
                _shell.Monitor.StartCommand.Execute(null);

            if (handover.TcpWasRunning && _shell.Tcp.StartCommand.CanExecute(null))
                _shell.Tcp.StartCommand.Execute(null);
        }
        catch (Exception ex)
        {
            // 引き継ぎに失敗しても起動はさせる（引き継げないより起動しない方が困る）
            CrashLog.Write(ex, "MainWindow.ApplyHandover");
        }
    }

    /// <summary>
    /// API キーを VM へ渡す。<see cref="PasswordBox.Password"/> はバインドできない
    /// (平文を依存関係プロパティに置かない設計)ので、変更のたびに手で押し込む。
    /// </summary>
    private void OnMerakiKeyChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
            _shell.Meraki.ApiKey = box.Password;
    }

    /// <summary>APIC のパスワード。<b>VM の中だけに置く</b>（設定にも引き継ぎにも書かない）。</summary>
    private void OnAciPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is PasswordBox box)
            _shell.Aci.Password = box.Password;
    }

    /// <summary>差分比較の「作業前」を、機器から直に取ってくる。</summary>
    private void OnDiffFetchBefore(object sender, RoutedEventArgs e) => FetchIntoDiff(before: true);

    /// <summary>差分比較の「作業後」を、機器から直に取ってくる。</summary>
    private void OnDiffFetchAfter(object sender, RoutedEventArgs e) => FetchIntoDiff(before: false);

    /// <summary>
    /// 小窓で機器へ入り、比較する対象の <c>show</c> を 1 本流して欄に入れる。
    /// <b>読み込みは置き換え</b>（ファイルから読むときと同じ。混ざると誤った差分を見る）。
    /// </summary>
    private void FetchIntoDiff(bool before)
    {
        ViewModels.DeviceCompareViewModel compare = _shell.DeviceCompare;

        string command = NetworkToys.Core.Work.DeviceComparison.CommandFor(compare.SelectedMode.Kind);

        if (DeviceFetchDialog.Fetch(
                this,
                before ? "機器から取得（作業前）" : "機器から取得（作業後）",
                command,
                AllTargetLists())
            is not { Length: > 0 } output)
        {
            return;
        }

        if (before) compare.BeforeText = output;
        else compare.AfterText = output;
    }

    /// <summary>
    /// 試験の 1 項目だけを、選んだプロキシで試す（2026-08-18 ユーザー指示）。
    ///
    /// <b>窓を開くのは画面の仕事</b>なので、プロキシの選択はここでメニューにして出す。
    /// 定義してあるプロキシ（直接・Windows の設定・一覧に書いたもの）がそのまま並ぶ。
    /// </summary>
    private void OnVerifyRunOne(object sender, RoutedEventArgs e)
    {
        // 行に小さな印を置くと何のボタンか伝わらない（2026-08-18 指摘）。
        // 一覧で選んでいる行に対して、上のボタンから走らせる
        if (VerifyItems.SelectedItem is not ViewModels.VerifyRowViewModel row)
        {
            _shell.Verify.Status = "先に、試したい項目の行を選んでください。";
            return;
        }

        ViewModels.VerifyViewModel verify = _shell.Verify;

        var menu = new ContextMenu
        {
            PlacementTarget = sender as UIElement,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        foreach (ViewModels.ProxyChoiceViewModel proxy in verify.Proxies)
        {
            ViewModels.ProxyChoiceViewModel captured = proxy;

            var entry = new MenuItem { Header = proxy.Name, ToolTip = proxy.Summary };

            entry.Click += (_, _) => _ = verify.RunOneAsync(row, captured.Choice);

            menu.Items.Add(entry);
        }

        menu.IsOpen = menu.Items.Count > 0;
    }

    /// <summary>
    /// 直前に取れた APIC の生の応答を出す。APIC の版で属性名が違うことがあり、
    /// 「表は空だが通信はできている」の切り分けが実機でしかできないため。
    /// </summary>
    private void OnAciShowResponse(object sender, RoutedEventArgs e)
    {
        string response = _shell.Aci.LastResponse;

        TextViewDialog.Show(
            this,
            "APIC の応答",
            response.Length > 0 ? response : "まだ何も取得していません。");
    }

    /// <summary>F5 での開始。ボタンと同じく、開始できたら Ping タブへ移る。</summary>
    /// <summary>
    /// F5 の相手。<b>TCP Ping タブを開いているときは TCP</b>、それ以外は Ping
    /// （2026-08-20 の UI 改善。以前は Monitor 固定で、TCP タブで押すと Ping タブへ飛ばされた）。
    /// </summary>
    private MonitorViewModel ShortcutTarget => IsShowing(TcpTab) ? _shell.Tcp : _shell.Monitor;

    private void StartFromShortcut()
    {
        MonitorViewModel target = ShortcutTarget;

        if (!target.StartCommand.CanExecute(null)) return;

        target.StartCommand.Execute(null);

        // TCP タブに居るときはそのまま。ほかのタブからは Ping タブへ
        if (ReferenceEquals(target, _shell.Monitor)) Show(PingTab);
    }

    private void StopFromShortcut()
    {
        MonitorViewModel target = ShortcutTarget;

        if (target.StopCommand.CanExecute(null)) target.StopCommand.Execute(null);
    }

    /// <summary>
    /// 左右の一覧の縦スクロールを互いに合わせる。
    ///
    /// <b>横は合わせない</b> — 長い行を左右それぞれで追えるように、別々の一覧にしてある
    /// （1 つの一覧に収めていたときは、片側が長いともう片側まで一緒に流れて読めなかった）。
    /// 縦がずれると見比べにならないので、こちらだけ写す。行数も行の高さも同じなので
    /// offset をそのまま渡せば揃う。
    /// </summary>
    private void OnDiffPaneScrolled(object sender, ScrollChangedEventArgs e)
    {
        // 写した先がまた通知してくる。1 往復で止める
        if (_syncingDiffPanes || e.VerticalChange == 0) return;

        // 相手は Tag で持たせてある（名前の対応表をここに置かない。
        // 差分の画面は ACI タブの中にもあるので、増えるたびに直す形にしない）
        if ((sender as FrameworkElement)?.Tag is not ListBox other) return;
        if (ScrollViewerOf(other) is not { } viewer) return;

        _syncingDiffPanes = true;

        try
        {
            viewer.ScrollToVerticalOffset(e.VerticalOffset);
        }
        finally
        {
            _syncingDiffPanes = false;
        }
    }

    /// <summary>一覧が内側に持っているスクロールの入れ物。テンプレートの中なので視覚ツリーで探す。</summary>
    private static ScrollViewer? ScrollViewerOf(DependencyObject root)
    {
        if (root is ScrollViewer found) return found;

        int count = VisualTreeHelper.GetChildrenCount(root);

        for (int i = 0; i < count; i++)
        {
            if (ScrollViewerOf(VisualTreeHelper.GetChild(root, i)) is { } viewer) return viewer;
        }

        return null;
    }

    /// <summary>
    /// 差分をたどるとき、移った先を見えるところへ持ってくる。
    ///
    /// 一覧は仮想化しているので、まだ実体のない行は <see cref="ListBox.ScrollIntoView"/> に
    /// 任せる（内部でスクロールしてから実体を作ってくれる）。
    /// </summary>
    private void OnScrollDiffIntoView(object? sender, int index) => ScrollIntoView(DiffList, index);

    /// <summary>片方を動かせば、もう片方は縦スクロールの合わせ込みで追いかける。</summary>
    private static void ScrollIntoView(ListBox pane, int index)
    {
        if (index < 0 || index >= pane.Items.Count) return;

        pane.ScrollIntoView(pane.Items[index]);
    }

    /// <summary>
    /// 既定の大きさを、いまの画面の作業領域（タスクバーを除いた広さ）に収める。
    ///
    /// XAML に書けるのは「広い画面での既定」だけで、<b>ノート PC ではみ出す</b>
    /// （2026-08-18 に縦がはみ出すと報告された）。<c>WindowStartupLocation</c> が
    /// 中央寄せなので、はみ出すと上下が画面の外へ出て操作できなくなる。
    /// <b>広げはしない</b> — 既定より大きくすると、今度は表がまばらになる。
    /// </summary>
    private void FitToScreen()
    {
        Rect area = SystemParameters.WorkArea;

        // 枠のぶんだけ余裕を見る（ぴったりだと影と枠が画面の縁にかかる）
        Width = Math.Max(MinWidth, Math.Min(Width, area.Width - 16));
        Height = Math.Max(MinHeight, Math.Min(Height, area.Height - 16));
    }

    /// <summary>
    /// タイトルバーの明暗を窓の中身に合わせる。ハンドルができてからでないと効かない。
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ApplyTitleBar();
    }

    private void ApplyTitleBar()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        Interop.NativeMethods.SetTitleBarDark(handle, ThemeManager.Current == AppTheme.Dark);
    }

    /// <summary>開始したら測定の画面へ移る。押した場所がどのタブでも、見たいのは結果。</summary>
    private void OnStartClicked(object sender, RoutedEventArgs e)
    {
        // TCP 側の開始で Ping タブへ飛ばさない(ユーザー指摘)。
        // ボタンの DataContext がどちらの画面かで見分ける
        if (sender is FrameworkElement { DataContext: MonitorViewModel { IsTcpScreen: true } }) return;

        Show(PingTab);
    }

    /// <summary>
    /// Ping 一覧の列幅ドラッグ。<b>掴んだ境目の左右の列だけ</b>が伸縮し、
    /// ほかの列は幅も位置も変わらない（2026-08-18 ユーザー指示）。
    ///
    /// 掴んでからの総移動量で決めるのは表の一覧と同じ理由
    /// （1 回ぶんの差分を足し込むと、つまみ自身が動くぶんを差し引かれて鈍る）。
    /// </summary>
    private void OnColumnGrab(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;

        _gripKey = key;
        _gripStartX = Mouse.GetPosition(this).X;
        ColumnLayout.Instance.BeginResize(key);
    }

    private void OnColumnResize(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;

        if (_gripKey != key)
        {
            ColumnLayout.Instance.BeginResize(key);
            ColumnLayout.Instance.Resize(key, e.HorizontalChange);
            return;
        }

        ColumnLayout.Instance.Resize(key, Mouse.GetPosition(this).X - _gripStartX);
    }

    /// <summary>
    /// Ping 以外の一覧の列幅。Tag は "テーブル名.列番号"。
    /// 掴んだ境界がカーソルから離れないための符号の判断は
    /// <see cref="TableColumns"/> の表の宣言 1 か所に閉じてある。
    /// </summary>
    /// <summary>掴んだつまみと、掴んだ位置（ウィンドウ座標）。</summary>
    private string? _gripKey;
    private double _gripStartX;

    private void OnTableColumnGrab(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;

        _gripKey = key;
        _gripStartX = Mouse.GetPosition(this).X;
        TableColumns.Instance.BeginResize(key);
    }

    /// <summary>
    /// 列幅のドラッグ。<b>掴んでからの総移動量</b>で決める。
    ///
    /// 1 回ぶんの差分を足し込むと、幅を変えたときにつまみ自体が動くぶんを
    /// <c>Thumb</c> が差し引いてしまい、伸び縮みが鈍る・端で詰まると飛ぶ。
    /// マウスの位置はウィンドウ基準で見れば、つまみが動いても影響を受けない。
    /// </summary>
    private void OnTableColumnResize(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string key }) return;

        // 掴み損ねた（DragStarted が来ていない）ときは、その場の差分で動かす
        if (_gripKey != key)
        {
            TableColumns.Instance.Drag(key, e.HorizontalChange);
            return;
        }

        TableColumns.Instance.Resize(key, Mouse.GetPosition(this).X - _gripStartX);
    }

    /// <summary>
    /// ファイル転送の左（この PC）でフォルダを開く。
    /// <b>ダブルクリックは行の上でしか受けない</b> — 一覧の余白を叩いても
    /// 直前に選んだフォルダへ入ってしまうのを避ける。
    /// </summary>
    private void OnLocalFileOpen(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(e) is { } row) _shell.Transfer.OpenLocal(row);
    }

    /// <summary>ファイル転送の右（接続先）でフォルダを開く。</summary>
    private void OnRemoteFileOpen(object sender, MouseButtonEventArgs e)
    {
        if (RowUnder(e) is { } row) _shell.Transfer.OpenRemote(row);
    }

    /// <summary>
    /// 叩かれたところから上へたどるときの親。
    ///
    /// <b><c>Run</c> は Visual ではない。</b>TextBlock の中を色分けすると
    /// （差分の色分けがそう）マウスの <c>OriginalSource</c> が <c>Run</c> になり、
    /// <see cref="VisualTreeHelper.GetParent"/> に渡した瞬間に
    /// <c>InvalidOperationException</c> でアプリごと落ちる（2026-08-17 に実際に落ちた）。
    /// <b>ContentElement は論理の親（＝載っている TextBlock）へ上がってから続ける。</b>
    ///
    /// クリックの出どころから上へたどるところは、必ずこれを通すこと。
    /// </summary>
    internal static DependencyObject? ClickedParentOf(DependencyObject node) => node switch
    {
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(node),
        System.Windows.ContentElement content =>
            ContentOperations.GetParent(content) ?? LogicalTreeHelper.GetParent(content),
        _ => LogicalTreeHelper.GetParent(node),
    };

    /// <summary>叩かれた場所にある行の VM。行の外なら null。</summary>
    private static FileRowViewModel? RowUnder(MouseButtonEventArgs e)
    {
        for (DependencyObject? node = e.OriginalSource as DependencyObject;
             node is not null;
             node = ClickedParentOf(node))
        {
            if (node is ListBoxItem { DataContext: FileRowViewModel row }) return row;
        }

        return null;
    }

    /// <summary>
    /// 表ごとの「列番号 → 並べ替えに使うプロパティ」。空文字の列は並べ替えない。
    /// 行テンプレートの Binding と同じ名前でないと効かないので、列を足したらここも直す。
    /// </summary>
    // internal は自己診断のため（並べ替えできるヘッダに Cursor と ToolTip が付いているかを突き合わせる）
    internal static readonly Dictionary<string, string[]> SortPaths = new()
    {
        ["trace"] = ["Ttl", "Address", "HostName", "Rtt", "Note"],
        ["scan"] = ["Address", "Rtt", "HostName", "Mac", "Vendor", "Ports", ""],
        ["ftplog"] = ["Time", "Remote", "Text"],
        ["tftplog"] = ["Time", "Remote", "Text"],
        ["sftplog"] = ["Time", "Remote", "Text"],
        // 名前でなく SortKey で並べる。フォルダを常に上に置きたいので
        ["local"] = ["", "SortKey", "Size", "Modified"],
        ["remote"] = ["", "SortKey", "Size", "Modified"],
        ["syslog"] = ["Time", "Remote", "Severity", "Text"],
        ["snmptrap"] = ["Time", "Remote", "Text"],
        ["snmpget"] = ["Oid", "Name", "Type", "Value"],
        ["vres"] = ["Name", "ProxyText", "VerdictText", "ElapsedMs", "Detail"],
        ["mdev"] = ["Name", "Model", "Serial", "Firmware", "Network", "State", "LanIp"],
        ["mup"] = ["Network", "Serial", "Interface", "State", "Ip", "Gateway", "PublicIp"],
        ["mcli"] = ["Network", "Description", "Ip", "Mac", "Vlan", "Manufacturer", "Usage", "LastSeen"],
        // スコアは数値のまま並べる（表示文字列で並べると 9 が 80 より後ろへ行く）
        ["mrsite"] = ["Network", "Clients", "Segments", "Note"],
        ["mrdhcp"] = ["Network", "Device", "Vlan", "Subnet", "Used", "Free", "UsagePercent"],
        ["mralert"] = ["Severity", "Type", "Network", "Device", "StartedAt", "Detail"],
        // 判定は列挙のまま並べる（合格・不合格・注意…の順にまとまる）
        ["mrcheck"] = ["Name", "Target", "Verdict", "Detail"],
        ["acihl"] = ["Kind", "Name", "Score", "State"],
        ["aciflt"] = ["Severity", "Code", "Created", "Target", "Description", "Ack"],
        // IF の列は InterfaceKey で並べる（eth1/2 が eth1/10 より前に来るように）
        ["aciport"] = ["InterfaceKey", "InterfaceKey", "OperState", "Speed", "Vlans", "Modes", "PortChannel",
                       "Epgs", "Reason", "LastChange", "Description"],
        ["acibd"] = ["Tenant", "Name", "Vrf", "Routing", "Subnets", "L2Unknown"],
        ["aciepg"] = ["Tenant", "AppProfile", "Name", "BridgeDomain", "Domains", "PathCount"],
        ["aciepgm"] = ["Node", "Path", "Encap", "Mode"],
        ["aciep"] = ["Mac", "Ip", "Tenant", "Epg", "Encap", "Node", "Path"],
        ["acilog"] = ["Time", "Kind", "Severity", "Target", "Text"],
        // ノードの列は NodeKey で並べる（101 が 9 より前に来ないように）
        ["acidev"] = ["NodeKey", "Name", "Role", "Model", "Serial", "Version", "State"],
        // RSSI は数値のまま並べる（表示文字列で並べると -100 が -58 より強いことになる）
        ["dnclc"] = ["Device", "Kind", "State", "Date", "Note"],
        ["wlcap"] = ["State", "Name", "Ip", "Mac", "Model", "Version", "Radios", "Clients", "Tags"],
        ["wlcjoin"] = ["State", "Name", "Mac", "LastJoin", "LastDisconnect", "Reason", "Joins", "Failures"],
        ["wlcssid"] = ["Ssid", "Profile", "Id", "State", "Clients", "Band24", "Band5", "Band6"],
        // 使用率と電波強度は数値のまま並べる
        ["wlcrf"] = ["ApName", "Radio", "Channel", "Power", "Utilization", "Noise", "Clients"],
        ["wlcrog"] = ["Kind", "Ssid", "Bssid", "Vendor", "Channel", "Rssi", "DetectedBy", "LastHeard", "Note"],
    };

    /// <summary>
    /// 見出しの列をクリックして並べ替える（自前の並べ替えを持たない一覧用）。
    ///
    /// どの列かは<b>クリックされた X 座標</b>から求める。列ごとに当たり判定の要素を
    /// 置くとヘッダ 1 つにつき数個ずつ増えるので、見出しの Grid 1 つで受ける。
    /// 並べ替えは <see cref="ICollectionView.SortDescriptions"/> に任せる（VM を
    /// 触らずに済み、仮想化も効いたまま）。押すたびに 昇順 → 降順 → 元の並び。
    /// </summary>
    private void OnTableHeaderSort(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not Grid { Tag: string table } header
            || !SortPaths.TryGetValue(table, out string[]? paths))
            return;

        int column = ColumnAt(header, e.GetPosition(header).X);
        if (column < 0 || column >= paths.Length || paths[column].Length == 0) return;

        if (FindList(header, table) is not { ItemsSource: { } source }) return;

        ICollectionView view = CollectionViewSource.GetDefaultView(source);
        string path = paths[column];

        ListSortDirection? current = null;
        foreach (SortDescription description in view.SortDescriptions)
        {
            if (description.PropertyName != path) continue;

            current = description.Direction;
            break;
        }

        view.SortDescriptions.Clear();

        // 昇順 → 降順 → 元の並び
        if (current is null)
            view.SortDescriptions.Add(new SortDescription(path, ListSortDirection.Ascending));
        else if (current == ListSortDirection.Ascending)
            view.SortDescriptions.Add(new SortDescription(path, ListSortDirection.Descending));

        ShowSortMark(header, column, view.SortDescriptions.Count == 0 ? null : view.SortDescriptions[0].Direction);
    }

    /// <summary>X 座標から列番号を求める。列幅はドラッグで変わるので実測値で見る。</summary>
    private static int ColumnAt(Grid header, double x)
    {
        double left = 0;

        for (int i = 0; i < header.ColumnDefinitions.Count; i++)
        {
            double width = header.ColumnDefinitions[i].ActualWidth;
            if (x >= left && x < left + width) return i;

            left += width;
        }

        return -1;
    }

    /// <summary>
    /// 見出しと同じ入れ物にいる一覧を探す。
    ///
    /// <b>見出しと同じ <c>Tag</c> を持つ一覧を優先する。</b>ほとんどの一覧は
    /// <see cref="ListBox"/> なのでそれで足りるが、経路のホップ一覧だけは
    /// <see cref="ItemsControl"/> で、ListBox しか見ていなかったころは
    /// 並べ替えが無反応だった。かといって「最初の ItemsControl」にはできない —
    /// 経路タブには手前に「何が変わったか」の ItemsControl があり、
    /// <see cref="ComboBox"/> も ItemsControl なので、別の一覧を掴んでしまう。
    /// </summary>
    private static ItemsControl? FindList(DependencyObject header, string table)
    {
        DependencyObject? parent = VisualTreeHelper.GetParent(header);
        if (parent is null) return null;

        return FindDescendant(parent, tagged: true) ?? FindDescendant(parent, tagged: false);

        ItemsControl? FindDescendant(DependencyObject node, bool tagged)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);

                // 見出しの Grid 自身や中の TextBlock を拾わないよう、一覧だけを見る
                if (tagged)
                {
                    if (child is ItemsControl { Tag: string tag } list && tag == table) return list;
                }
                else if (child is ListBox list)
                {
                    return list;
                }

                if (FindDescendant(child, tagged) is { } found) return found;
            }

            return null;
        }
    }

    /// <summary>並べ替えた列の見出しに ▲▼ を付ける。どこで並べ替えているか分かるように。</summary>
    private static void ShowSortMark(Grid header, int column, ListSortDirection? direction)
    {
        foreach (UIElement child in header.Children)
        {
            if (child is not TextBlock text) continue;

            string label = text.Text.TrimEnd(' ', '▲', '▼');

            text.Text = Grid.GetColumn(text) == column && direction is { } d
                ? label + (d == ListSortDirection.Ascending ? " ▲" : " ▼")
                : label;
        }
    }

    /// <summary>一覧の見出しクリックで並べ替える。列名は Tag、対象は DataContext で見分ける。</summary>
    private void OnListHeaderSort(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string column } element)
            return;

        switch (element.DataContext)
        {
            case MonitorViewModel monitor:
                monitor.SortBy(column);
                break;
            case ConnectionsViewModel connections:
                connections.SortBy(column);
                break;
            case WifiViewModel wifi:
                wifi.SortAccessPointsBy(column);
                break;
            case WfpViewModel wfp:
                wfp.SortBy(column);
                break;
        }
    }

    /// <summary>
    /// WFP の記録を有効にする。<b>システム全体に効く設定</b>を変えるので、
    /// VM から確認ダイアログを出してもらってから実行する。
    /// </summary>
    private void OnEnableWfpCollection(object sender, RoutedEventArgs e) => _shell.Wfp.EnableCollection();

    /// <summary>ログ採取: 全行へ同じ認証情報を流し込む。伏せ字欄への反映は SecretsImported 経由。</summary>
    private void OnCollectFillCredentials(object sender, RoutedEventArgs e)
    {
        if (CollectCredentialsDialog.Ask(this) is not { } filled) return;

        _shell.Collect.FillCredentials(filled.User, filled.Password, filled.Enable, filled.Overwrite);
    }

    /// <summary>
    /// 接続タブの通信量(ETW)や遮断一覧のための管理者再起動。asInvoker は変えない方針なので、
    /// 昇格したい人だけがここから明示的に選ぶ。
    ///
    /// いまの状態は引き継ぎファイルに控えて次のプロセスへ渡す。渡せないのは
    /// 待受中のサーバ(ソケットはプロセスをまたげない)と、進行中の処理だけ。
    /// </summary>
    private void OnRelaunchAsAdmin(object sender, RoutedEventArgs e)
    {
        bool serversRunning = _shell.Ftp.IsRunning || _shell.Tftp.IsRunning || _shell.Sftp.IsRunning
                              || _shell.Syslog.IsRunning || _shell.SnmpTrap.IsRunning;

        string caution = serversRunning
            ? "待受中のサーバは引き継げないので停止します（開き直してください）。\n"
            : "";

        bool confirmed = ConfirmDialog.Confirm(
            this,
            "管理者として再起動",
            "いったん終了して、管理者権限で起動し直します。\n" +
            "測定の結果と各画面の入力はそのまま引き継ぎます。\n" +
            caution +
            "パスワードと Meraki の API キーは、保存しない決まりなので引き継ぎません。\n" +
            "（ログ採取・差分比較・ACI・FTP/SFTP のパスワードは入れ直してください）\n\n" +
            "続けますか？",
            okLabel: "再起動する");

        if (!confirmed) return;

        string handoverPath = HandoverService.NewPath();

        try
        {
            // 単一ファイル発行では Assembly.Location が空になるので Environment.ProcessPath を使う
            string exePath = Environment.ProcessPath
                ?? throw new InvalidOperationException("実行ファイルの場所が分かりません");

            HandoverDocument document = HandoverService.Capture(_shell);

            // 押したボタンが載っているタブへ戻す。束ねたタブの中（通信状況）から押されても
            // その画面で開き直せる — 外側の「その他」を控えると先頭の画面で開いてしまう
            document.SelectedTab = (TabOf(sender) ?? ShownTab())?.Header?.ToString() ?? "";
            document.WindowMaximized = WindowState == WindowState.Maximized;

            // 最大化中は Left/Width が画面いっぱいの値になるので、元の大きさを控える
            Rect bounds = WindowState == WindowState.Normal
                ? new Rect(Left, Top, Width, Height)
                : RestoreBounds;

            document.WindowLeft = bounds.Left;
            document.WindowTop = bounds.Top;
            document.WindowWidth = bounds.Width;
            document.WindowHeight = bounds.Height;

            HandoverStore.Save(handoverPath, document);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                Arguments = $"{HandoverService.Switch} \"{handoverPath}\"",
                UseShellExecute = true,   // UAC の昇格ダイアログを出すのに必須
                Verb = "runas",
                WorkingDirectory = Environment.CurrentDirectory,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException
                                      or IOException or UnauthorizedAccessException)
        {
            // UAC で「いいえ」が選ばれた(1223)か、起動できなかった。いまの窓のまま続ける。
            // 控えたファイルは機器の出力を含みうるので必ず消す
            HandoverStore.Delete(handoverPath);
            return;
        }

        Application.Current.Shutdown();
    }

    /// <summary>
    /// いまの画面を PNG にして logs フォルダへ残す。証跡は「見たままの画面」が
    /// 一番説明が早いので、レポートとは別にワンボタンで撮れるようにしている。
    /// </summary>
    private void OnScreenshot(object sender, RoutedEventArgs e)
    {
        try
        {
            string path = SessionLogService.NewScreenshotPath();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);

            using (FileStream stream = File.Create(path))
                CaptureWindow().Save(stream);

            ShowNotice("✓ 保存しました（クリックで開く）", openPath: path);
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnScreenshot");
            ShowNotice("保存できません", isProblem: true);
        }
    }

    /// <summary>
    /// ウィンドウの中身を実 DPI で描画して PNG エンコーダに載せる。
    /// Window そのものを Render すると枠ぶんの余白がずれることがあるため、
    /// VisualBrush で中身を描き直す。地色は Window 側が持っているので先に敷く。
    /// </summary>
    internal PngBitmapEncoder CaptureWindow()
    {
        var root = (FrameworkElement)Content;
        var area = new Rect(new Size(root.ActualWidth, root.ActualHeight));

        var visual = new DrawingVisual();
        using (DrawingContext context = visual.RenderOpen())
        {
            context.DrawRectangle(Background, null, area);
            context.DrawRectangle(new VisualBrush(root), null, area);
        }

        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        var bitmap = new RenderTargetBitmap(
            (int)Math.Ceiling(area.Width * dpi.DpiScaleX),
            (int)Math.Ceiling(area.Height * dpi.DpiScaleY),
            dpi.PixelsPerInchX,
            dpi.PixelsPerInchY,
            PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        return encoder;
    }

    /// <summary>
    /// IP 設定の適用。自分の足場(この PC の通信)を壊せる操作なので、
    /// UAC の前に内容の確認を挟む(UAC は「昇格の同意」、こちらは「内容の確認」)。
    /// </summary>
    private async void OnApplyIpConfig(object sender, RoutedEventArgs e)
    {
        string summary = _shell.IpConfig.ConfirmationSummary();
        if (summary.Length == 0)
            return;

        bool confirmed = ConfirmDialog.Confirm(
            this,
            "IP 設定の適用",
            summary + "\n\nこの PC の通信が一時的に切れることがあります。続けますか？",
            okLabel: "適用する");

        if (!confirmed)
            return;

        try
        {
            await _shell.IpConfig.ApplyAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnApplyIpConfig");
        }
    }

    /// <summary>
    /// ファイルメニューを開くたびに、保存できるかを聞き直す。
    ///
    /// 記録の画面を畳んだので（2026-08-16）、判定を見直す機会がここしかない。
    /// 保存したあとの結果もヘッダの文字で知らせる。
    /// </summary>
    private void OnFileMenuOpened(object sender, RoutedEventArgs e)
        => _shell.Report.RefreshSaveCommands();

    /// <summary>知らせを消すタイマー。<b>1 本だけ</b>持つ — 呼ぶたびに作ると、
    /// 連続で知らせたとき前のタイマーが後の文字を早々に消してしまう（2 秒時代に実際に起きうる作りだった）。</summary>
    private DispatcherTimer? _noticeTimer;

    /// <summary>
    /// 短い知らせをヘッダの文字で出す。トーストや別窓は出さない。
    /// 2 秒では保存の成否を見逃すので、通常 5 秒・<paramref name="isProblem"/> なら 10 秒
    /// （2026-08-20 の UI 改善。失敗はゆっくり読めるように）。
    /// </summary>
    /// <summary>知らせのクリックで開くファイル。無ければ普通の知らせ（クリックしても何も起きない）。</summary>
    private string? _noticeOpenPath;

    /// <summary>
    /// 知らせのクリック。開く対象があるときだけ、explorer でそのファイルを選択表示する。
    /// 結線は常時（付け外しの設計をしない — 外し忘れの道を作らないため）。
    /// </summary>
    private void OnNoticeClick(object sender, MouseButtonEventArgs e)
    {
        if (_noticeOpenPath is not { Length: > 0 } path) return;

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnNoticeClick");
        }
    }

    // internal は自己診断のため（開く対象の有無でカーソルが付け外しされることを見る）
    internal void ShowNotice(string text, bool isProblem = false, string? openPath = null)
    {
        _noticeTimer?.Stop();

        // 開く対象は知らせごとに必ず入れ替える（前のパスを引きずらない）
        _noticeOpenPath = openPath;
        HeaderNotice.Cursor = openPath is null ? null : Cursors.Hand;

        HeaderNotice.Text = text;

        _noticeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(isProblem ? 10 : 5) };
        _noticeTimer.Tick += (_, _) =>
        {
            _noticeTimer.Stop();
            HeaderNotice.Text = "";
            _noticeOpenPath = null;
            HeaderNotice.Cursor = null;
        };
        _noticeTimer.Start();
    }

    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        ThemeManager.Toggle();
        UpdateThemeToggle();
        ApplyTitleBar();
    }

    /// <summary>
    /// ボタンにもメニューにも「切り替えた先」を出す。いまの状態を出すと、
    /// 押すとどうなるのかが読み取れない。
    /// </summary>
    private void UpdateThemeToggle()
    {
        bool dark = ThemeManager.Current == AppTheme.Dark;

        ThemeToggle.Content = dark ? "☀ ライト" : "☾ ダーク";
        ThemeMenuItem.Header = dark ? "☀ ライトモードにする(_M)" : "☾ ダークモードにする(_M)";
    }

    /// <summary>
    /// 文字の大きさを変える。押した項目の <c>Tag</c> が倍率。
    /// <b>ラジオのように 1 つだけ選ばれた状態</b>にするので、
    /// 押したものが既に選ばれていても外させない。
    /// </summary>
    private void OnScaleMenu(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag }
            && double.TryParse(tag, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out double scale))
        {
            UiScale.Apply(scale);
        }

        UpdateScaleMenu();
    }

    /// <summary>いまの大きさにだけチェックを付ける。</summary>
    private void UpdateScaleMenu()
    {
        ScaleSmall.IsChecked = UiScale.Is(0.85);
        ScaleNormal.IsChecked = UiScale.Is(1.0);
        ScaleLarge.IsChecked = UiScale.Is(1.25);
        ScaleExtraLarge.IsChecked = UiScale.Is(1.5);
    }

    /// <summary>最前面固定。<b>次回起動でも覚える</b>（以前は毎回外れていた）。</summary>
    private void OnTopmostMenu(object sender, RoutedEventArgs e)
    {
        Topmost = TopmostMenuItem.IsChecked;

        try
        {
            Settings.Current.Topmost = Topmost;
            Settings.Save();
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            // 覚えられなくても、いまの固定は効いている
        }
    }

    private void OnExit(object sender, RoutedEventArgs e) => Close();

    private void OnOpenDataFolder(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppData.Directory(),
                UseShellExecute = true,   // フォルダはシェル(エクスプローラー)に開かせる
            });
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnOpenDataFolder");
        }
    }

    /// <summary>
    /// 使い方を出す。<b>Markdown は組んで見せる</b>（2026-08-18 ユーザー指示。
    /// 生の <c>#</c> や <c>|</c> の並びは画面で読むものとして辛い）。
    ///
    /// <b>exe に埋め込んである</b>ので、ネットワークが無くても、
    /// zip から exe だけ取り出されていても読める（配布物では隣にも置いてある）。
    /// </summary>
    private void OnShowUsage(object sender, RoutedEventArgs e)
    {
        if (ReadEmbedded("使い方.md") is not { Length: > 0 } markdown)
        {
            TextViewDialog.Show(
                this,
                "使い方",
                "使い方を読み込めませんでした。\n"
                + "配布物の「使い方.md」か、リポジトリの docs/USAGE.md をご覧ください。\n"
                + "https://github.com/izenmi/networktoys/blob/main/docs/USAGE.md");

            return;
        }

        UsageDialog.Show(this, "使い方", markdown);
    }

    /// <summary>
    /// 同梱物の著作権表示とライセンス本文を出す。
    ///
    /// <b>exe に埋め込んである</b>ので、zip から exe だけ取り出されていても読める
    /// （MIT / Apache-2.0 / OFL 1.1 はいずれも再配布時の添付を求めている）。
    /// </summary>
    private void OnShowLicenses(object sender, RoutedEventArgs e)
    {
        string text = ReadNotices()
            ?? "ライセンス情報を読み込めませんでした。\n"
             + "リポジトリの THIRD-PARTY-NOTICES.txt をご覧ください。\n"
             + "https://github.com/izenmi/networktoys/blob/main/THIRD-PARTY-NOTICES.txt";

        // ライセンス本文は桁を揃えて書かれているので折り返さない（罫線と字下げが崩れる）
        TextViewDialog.Show(this, "ライセンス情報", text, wrap: false);
    }

    /// <summary>埋め込んだライセンス本文。読めなければ null。</summary>
    internal static string? ReadNotices() => ReadEmbedded("THIRD-PARTY-NOTICES.txt");

    /// <summary>exe に埋め込んだテキスト。読めなければ null。</summary>
    internal static string? ReadEmbedded(string name)
    {
        try
        {
            using System.IO.Stream? stream = typeof(MainWindow).Assembly.GetManifestResourceStream(name);

            if (stream is null) return null;

            using var reader = new System.IO.StreamReader(stream);

            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is System.IO.IOException or NotSupportedException)
        {
            CrashLog.Write(ex, "MainWindow.ReadEmbedded");
            return null;
        }
    }

    /// <summary>
    /// 列幅を既定に戻す。<b>2 系統あるので両方まとめて</b>戻す
    /// （Ping / TCP 一覧の <see cref="ViewModels.ColumnLayout"/> と、
    /// それ以外の表の <see cref="ViewModels.TableColumns"/>）。
    /// </summary>
    private void OnResetColumns(object sender, RoutedEventArgs e)
    {
        if (!ConfirmDialog.Confirm(
                this,
                "列幅を既定に戻す",
                "すべての一覧の列幅を、はじめの状態に戻します。\n"
                + "測定の結果や宛先リストには触りません。",
                "戻す"))
        {
            return;
        }

        ViewModels.ColumnLayout.Instance.Reset();
        ViewModels.TableColumns.Instance.Reset();

        ShowNotice("✓ 列幅を戻しました");
    }

    private void OnAbout(object sender, RoutedEventArgs e)
    {
        // 単一ファイル発行でも AssemblyInformationalVersion は埋め込まれて読める。
        // ビルド環境によっては "+コミットID" が付くので表示用に落とす
        string version = (typeof(MainWindow).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? "不明").Split('+')[0];

        // 設定と記録の置き場所も出す。持ち出して使う前提のアプリなので、
        // 「どこに残るのか」が見えないと困る（exe の横に書けないときは退避している）
        string where = AppData.IsBesideExecutable
            ? "設定と記録の場所（exe と同じフォルダ）:"
            : "設定と記録の場所（exe の横に書けないため退避しています）:";

        ConfirmDialog.Show(
            this,
            "バージョン情報",
            $"NetworkToys {version}\n\n" +
            "色々できるネットワーク診断ツール\n\n" +
            $"{where}\n{AppData.Directory()}");
    }

    /// <summary>
    /// 測った結果だけを消す。宛先も、作業の記録も、機器の貼り付けも残る。
    ///
    /// やり直しの範囲が小さく、Ping タブの「履歴を消去」と同じ重さなので確認は挟まない。
    /// </summary>
    private async void OnClearPingResults(object sender, RoutedEventArgs e)
    {
        try
        {
            await _shell.Monitor.ResetResultsAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnClearPingResults");
        }
    }

    /// <summary>
    /// 測った結果をすべて捨てて起動直後に戻す。宛先リストだけは残す。
    ///
    /// 作業中に誤って押すと、作業前の記録も不通の記録も消えて取り返しがつかない。
    /// 元に戻す手段が無い操作なので、ここだけは確認を挟む。
    /// </summary>
    private async void OnClearAll(object sender, RoutedEventArgs e)
    {
        // MessageBox はネイティブ描画で、ダークテーマだとそこだけ白い箱が出る。
        // アプリの配色で描く自前の確認ダイアログを使う
        bool confirmed = ConfirmDialog.Confirm(
            this,
            "クリア",
            "測定結果・作業の記録・各画面の入力を、起動直後の状態に戻します。\n" +
            "宛先リストは残ります。保存済みのレポートやセッションのファイルは消えません。\n\n" +
            "元に戻せません。実行しますか？",
            okLabel: "すべて消す");

        if (!confirmed) return;

        try
        {
            await _shell.ClearAllAsync();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnClearAll");
        }
    }

    /// <summary>
    /// 右クリックされた行を取り出す。
    ///
    /// <see cref="ContextMenu"/> は論理ツリーが本体から切れているため
    /// 親を辿れない。XAML 側で <c>PlacementTarget</c> の DataContext を
    /// 引き継いであるので、ここでは送り主の DataContext を見ればよい。
    /// <b>選択行は使わない</b>（右クリックでは選択が動かないため）。
    /// </summary>
    private static TargetRowViewModel? RowOf(object sender)
        => (sender as FrameworkElement)?.DataContext as TargetRowViewModel;

    /// <summary>
    /// 落ちている宛先を見つけたとき、そのまま経路を追えるようにする。
    /// 打ち直しの手間と打ち間違いを無くすのが目的。
    /// </summary>
    /// <summary>
    /// 右クリックした宛先を TCP タブの宛先リストへ足す。
    ///
    /// ICMP で見ている相手のポートまで見たくなる場面は多いが、宛先リストは
    /// 画面ごとに分かれているので打ち直しになっていた。ポートは TCP タブに
    /// 入っている既定値を使う（宛先ごとに変えたければ後から書き換えられる）。
    /// </summary>
    private void OnAddToTcpFromRow(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || row.Host.Length == 0) return;

        string port = _shell.Tcp.TcpPort.Trim();
        string line = port.Length > 0 ? $"{row.Host}:{port}" : row.Host;

        if (row.Comment.Length > 0)
            line += "\t" + row.Comment;

        _shell.Tcp.AppendToTargetList([line]);
        Show(TcpTab);
    }

    /// <summary>
    /// Ping / TCP の行から、その相手をログ採取の機器に足す。
    /// <b>備考はそのまま持っていく</b>（採取したファイルの名前に効く）。
    /// </summary>
    private void OnCollectFromRow(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || row.Host.Length == 0) return;

        _shell.Collect.Import([(row.Host, row.Comment)]);
        Show(CollectTab);
    }

    /// <summary>
    /// FTP / SFTP サーバのパスワードを VM へ渡す。
    /// <see cref="PasswordBox.Password"/> はバインドできない（平文を依存関係プロパティに
    /// 置かない設計）ので、変更のたびに手で押し込む。どちらの画面かは Tag で見分ける。
    /// </summary>
    private void OnFileServerPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox { Tag: string which } box) return;

        switch (which)
        {
            case "ftp": _shell.Ftp.Password = box.Password; break;
            case "sftp": _shell.Sftp.Password = box.Password; break;
            case "xfer": _shell.Transfer.Password = box.Password; break;
        }
    }

    /// <summary>
    /// 収集タブのパスワードを行の VM へ渡す。
    /// <see cref="PasswordBox.Password"/> はバインドできないので、変更のたびに手で押し込む。
    /// 行は <c>DataTemplate</c> の中にあるので、<c>Style</c> の <c>Setter</c> に
    /// イベント付き要素を置いたときの事故（起動時 XamlParseException）には当たらない。
    /// </summary>
    private void OnCollectPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (sender is not PasswordBox { DataContext: CollectRowViewModel row } box) return;

        if (Equals(box.Tag, "enable"))
            row.EnablePassword = box.Password;
        else
            row.Password = box.Password;
    }

    /// <summary>
    /// Ping と TCP の宛先から、取り込む相手を選んでもらう。
    /// 全部入れると使わない機器まで並ぶので、絞り込みつきの選択画面を出す。
    /// </summary>
    /// <summary>
    /// Ping / TCP に登録してある宛先。<b>同じ宛先が両方にあることがあるので畳む</b>。
    /// 収集タブへの取り込みと、差分比較の「機器から」の両方で使う。
    /// </summary>
    /// <summary>
    /// 宛先の選択窓に出すリスト一覧。いま出ている宛先に加えて、名前を付けて残した
    /// リスト(Ping / TCP)全部から選べる(2026-08-21 ユーザー指示)。差分比較の「宛先から」と
    /// ログ採取の「宛先リストから選ぶ」の両方がこれを使う — 片方にだけリストの
    /// メニューが出るのは不揃いだと指摘された(2026-08-23)。
    /// </summary>
    private IReadOnlyList<TargetListSource> AllTargetLists()
    {
        List<TargetListSource> sources = [new("いまの宛先（Ping / TCP）", KnownTargets())];

        foreach ((string prefix, System.Collections.Generic.Dictionary<string, string> store) in
                 new (string, System.Collections.Generic.Dictionary<string, string>)[]
                 {
                     ("Ping: ", Settings.Current.PingTargetLists),
                     ("TCP: ", Settings.Current.TcpTargetLists),
                 })
        {
            foreach (string name in store.Keys.OrderBy(n => n, StringComparer.CurrentCulture))
            {
                (string Host, string Memo)[] targets =
                    [.. TargetListParser.Parse(store[name]).Targets
                        .Where(t => t.Host.Length > 0)
                        .Select(t => (t.Host, t.Comment))];

                if (targets.Length > 0)
                    sources.Add(new($"{prefix}{name}", targets));
            }
        }

        return sources;
    }

    private IReadOnlyList<(string Host, string Memo)> KnownTargets()
    {
        Dictionary<string, string> unique = [];

        foreach ((string host, string memo) in
                 _shell.Monitor.Rows.Select(r => (r.Host, r.Comment))
                 .Concat(_shell.Tcp.Rows.Select(r => (r.Host, r.Comment))))
        {
            if (host.Length == 0) continue;

            if (!unique.TryGetValue(host, out string? existing) || existing.Length == 0)
                unique[host] = memo;
        }

        return [.. unique.Select(p => (p.Key, p.Value))];
    }

    private void ImportTargetsIntoCollect()
    {
        // いまの宛先だけでなく、名前を付けて残したリスト全部から選べるようにする
        // (差分比較の「宛先から」と同じ窓)。ログ採取こそ「残しておいた機器の一覧から
        // 選んで取りに行く」画面なのに、こちらだけリストのメニューが無かった
        IReadOnlyList<TargetListSource> sources = AllTargetLists();

        if (sources.All(s => s.Targets.Count == 0))
        {
            ConfirmDialog.Show(this, "宛先がありません",
                "Ping と TCP のタブに宛先が無く、名前を付けて残したリストもありません。先に宛先を登録してください。");
            return;
        }

        IReadOnlyList<(string Host, string Memo)> picked = TargetPickerDialog.Pick(this, sources);

        if (picked.Count > 0)
            _shell.Collect.Import(picked);
    }

    /// <summary>
    /// 収集が終わったら画面の伏せ字欄も空にする
    /// （VM 側の値を消しても <see cref="PasswordBox"/> の中身は残るため）。
    /// </summary>
    /// <summary>
    /// 行 VM のパスワードを伏せ字欄へ映す。PasswordBox はバインドを持たないので、
    /// CSV から取り込んだ直後はこちらから押し込むしかない（欄が空のままだと
    /// 「入っていない」と誤読して打ち直してしまう）。
    /// </summary>
    private void FillCollectPasswordBoxes()
    {
        foreach (PasswordBox box in FindPasswordBoxes(this))
        {
            if (box.DataContext is not CollectRowViewModel row) continue;

            string wanted = Equals(box.Tag, "enable") ? row.EnablePassword : row.Password;

            // 同じ値の入れ直しでも PasswordChanged は飛ぶが、書き戻る値も同じなので害はない
            if (box.Tag is "login" or "enable" && box.Password != wanted)
                box.Password = wanted;
        }
    }

    private void ClearCollectPasswordBoxes()
    {
        // 起点をウィンドウ全体にする。収集タブを束ねたとき、親が選ばれていないと
        // タブの中身は実体化しておらず、そこを起点にすると 1 つも見つからない。
        // 収集の欄は Tag が login / enable（FTP と SFTP は ftp / sftp）
        foreach (PasswordBox box in FindPasswordBoxes(this))
        {
            if (box.Tag is "login" or "enable")
                box.Clear();
        }
    }

    /// <summary>
    /// 見出しでタブを探す。<b>内側の TabControl までたどる</b>。
    /// 昇格して起動し直したときに、開いていたタブへ戻すのに使う。
    /// </summary>
    private static TabItem? FindTabByHeader(TabControl tabs, string? header)
    {
        if (string.IsNullOrEmpty(header)) return null;

        foreach (object? item in tabs.Items)
        {
            if (item is not TabItem tab) continue;

            if (Equals(tab.Header, header)) return tab;

            // 中身がまだ実体化していないと子は見つからない。
            // それでも既定のタブで起動するだけなので、探せる範囲で探す
            foreach (TabControl inner in FindDescendants<TabControl>(tab))
            {
                if (FindTabByHeader(inner, header) is { } found) return found;
            }
        }

        return null;
    }

    private static IEnumerable<T> FindDescendants<T>(DependencyObject node) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(node);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);

            if (child is T match) yield return match;

            foreach (T found in FindDescendants<T>(child))
                yield return found;
        }
    }

    private static IEnumerable<PasswordBox> FindPasswordBoxes(DependencyObject node)
    {
        int count = VisualTreeHelper.GetChildrenCount(node);

        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(node, i);

            if (child is PasswordBox box)
            {
                yield return box;
                continue;
            }

            foreach (PasswordBox found in FindPasswordBoxes(child))
                yield return found;
        }
    }

    /// <summary>右クリックした宛先へ Tera Term で SSH 接続する。</summary>
    private void OnSshFromRow(object sender, RoutedEventArgs e) => ConnectWithTeraTerm(sender, ssh: true);

    /// <summary>右クリックした宛先へ Tera Term で Telnet 接続する。</summary>
    private void OnTelnetFromRow(object sender, RoutedEventArgs e) => ConnectWithTeraTerm(sender, ssh: false);

    /// <summary>
    /// Tera Term でつなぐ（ユーザー指示。現場で使い慣れているものを開く）。
    ///
    /// 場所は既定の導入先から探し、見つからなければ 1 度だけ選んでもらって覚える。
    /// TCP 画面の宛先はポートを持っているので、それがあれば優先する。
    /// </summary>
    private void ConnectWithTeraTerm(object sender, bool ssh)
    {
        if (RowOf(sender) is not { } row || row.Host.Length == 0) return;

        string? exePath = TeraTerm.Locate() ?? AskForTeraTerm();
        if (exePath is null) return;

        int port = row.Target.Kind == NetworkToys.Core.Models.ProbeKind.Tcp ? row.Target.Port : 0;

        if (TeraTerm.Connect(exePath, row.Host, port, ssh) is { } error)
        {
            CrashLog.Write(new InvalidOperationException(error), "MainWindow.ConnectWithTeraTerm");
            ConfirmDialog.Show(this, "Tera Term を起動できません", error);
        }
    }

    /// <summary>見つからないときに場所を選んでもらう。選ばれたら次回から覚えている。</summary>
    private string? AskForTeraTerm()
    {
        ConfirmDialog.Show(
            this,
            "Tera Term が見つかりません",
            "Tera Term (ttermpro.exe) を自動で見つけられませんでした。\n" +
            "次の画面で ttermpro.exe を選んでください（次回からは覚えています）。");

        var dialog = new OpenFileDialog
        {
            Title = "ttermpro.exe を選ぶ",
            FileName = "ttermpro.exe",
            Filter = "Tera Term (ttermpro.exe)|ttermpro.exe|プログラム (*.exe)|*.exe",
            CheckFileExists = true,
        };

        if (dialog.ShowDialog(this) != true) return null;

        TeraTerm.Remember(dialog.FileName);
        return dialog.FileName;
    }

    private void OnTraceFromRow(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        _shell.Trace.Host = row.Host;
        Show(TraceTab);

        if (_shell.Trace.TraceCommand.CanExecute(null))
            _shell.Trace.TraceCommand.Execute(null);
    }

    private void OnResolveFromRow(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        _shell.Dns.Name = row.Host;
        Show(DnsTab);

        if (_shell.Dns.QueryCommand.CanExecute(null))
            _shell.Dns.QueryCommand.Execute(null);
    }

    /// <summary>
    /// その行の<b>宛先の列</b>をそのまま写す（2026-08-18 ユーザー指示）。
    ///
    /// 以前は引けた IP だけを写し、名前で登録した宛先は「まだ引けていません」と断っていた。
    /// <b>断られる方が困る</b> — 押した人は見えている文字が写ると思っている。
    /// 名前を引いたアドレスは応答の列に出ているので、写したい人はそちらを見る。
    /// </summary>
    private void OnCopyAddressFromRow(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        CopyText(row.Host);
    }

    // ===== 一覧から次の道具へ送る（スキャン・接続・遮断・Meraki・syslog・Trap） =====
    //
    // 橋が架かっていたのは Ping/TCP の一覧だけで、他のタブで見つけた IP は
    // 手で打ち直すしかなかった。行の型ごとに「どこがアドレスか」を
    // AddressOf 1 か所で決め、送り先は既存の 3 本と同じ形で書く。

    /// <summary>
    /// 右クリックされた行からアドレスを取り出す。
    ///
    /// 行の型は一覧ごとに違うので、ここでだけ振り分ける。<b>新しい一覧に
    /// メニューを付けたらここに 1 行足す</b>（足し忘れると黙って何も起きない）。
    /// </summary>
    internal static string AddressOf(object sender)
    {
        object? context = (sender as FrameworkElement)?.DataContext;

        string raw = context switch
        {
            ScanRowViewModel scan => scan.Address,
            NetworkToys.Core.Net.ConnectionDetailRow conn => conn.Remote,
            NetworkToys.Core.Net.WfpBlockedRow wfp => wfp.Remote,
            NetworkToys.Core.Cloud.MerakiDeviceRow device => device.LanIp,
            NetworkToys.Core.Cloud.MerakiClientRow client => client.Ip,
            NetworkToys.Core.Cloud.MerakiUplinkRow uplink => uplink.Ip,
            NetworkToys.Core.Fabric.AciEndpointRow endpoint => endpoint.Ip,
            FileServerLogRow log => log.Remote,

            // 応答の無いホップは「* * *」。宛先にはできない
            TraceHopViewModel hop => hop.Responded ? hop.Address : string.Empty,

            // 値がアドレスかホスト名のレコードだけ。TXT や SOA の値は宛先にならず、
            // MX は「10 mail.example.com」のように優先度が混ざる
            Services.DnsRecordLine dns when dns.Type is "A" or "AAAA" or "CNAME" or "NS" or "PTR"
                => dns.Value,

            _ => string.Empty,
        };

        return HostOnly(raw);
    }

    /// <summary>
    /// <c>1.2.3.4:443</c> や <c>[::1]:443</c> からホストだけ取り出す。
    ///
    /// 素の IPv6 を切らないよう、<b>コロンが 1 つのときだけ</b>後ろを落とす
    /// （<see cref="NetworkToys.Core.Storage.TargetListParser"/> と同じ規則）。
    /// 角かっこ付きは中身をそのまま使う。
    /// </summary>
    internal static string HostOnly(string value)
    {
        string text = value.Trim();

        // 「—」はリモートが無い行（LISTEN など）。宛先にはできない
        if (text.Length == 0 || text == "—") return string.Empty;

        if (text.StartsWith('['))
        {
            int close = text.IndexOf(']');
            return close > 1 ? text[1..close] : string.Empty;
        }

        int colon = text.LastIndexOf(':');
        return colon > 0 && text.IndexOf(':') == colon ? text[..colon] : text;
    }

    private void OnSendToPing(object sender, RoutedEventArgs e)
    {
        if (AddressOf(sender) is not { Length: > 0 } address) return;

        _shell.Monitor.AppendToTargetList([address]);
        Show(PingTab);
    }

    private void OnSendToTcp(object sender, RoutedEventArgs e)
    {
        if (AddressOf(sender) is not { Length: > 0 } address) return;

        // ポートは TCP タブの既定値を使う（後から書き換えられる）
        string port = _shell.Tcp.TcpPort.Trim();

        _shell.Tcp.AppendToTargetList([port.Length > 0 ? $"{address}:{port}" : address]);
        Show(TcpTab);
    }

    private void OnSendToCollect(object sender, RoutedEventArgs e)
    {
        if (AddressOf(sender) is not { Length: > 0 } address) return;

        _shell.Collect.Import([(address, string.Empty)]);
        Show(CollectTab);
    }

    private void OnSendToTrace(object sender, RoutedEventArgs e)
    {
        if (AddressOf(sender) is not { Length: > 0 } address) return;

        _shell.Trace.Host = address;
        Show(TraceTab);

        if (_shell.Trace.TraceCommand.CanExecute(null))
            _shell.Trace.TraceCommand.Execute(null);
    }

    private void OnSendToDns(object sender, RoutedEventArgs e)
    {
        if (AddressOf(sender) is not { Length: > 0 } address) return;

        _shell.Dns.Name = address;
        _shell.Dns.SelectedType = "PTR";   // IP を渡すので、名前を知りたいなら逆引き
        Show(DnsTab);

        if (_shell.Dns.QueryCommand.CanExecute(null))
            _shell.Dns.QueryCommand.Execute(null);
    }

    /// <summary>
    /// SNMP は<b>実行まではしない</b>。コミュニティ名を入れてもらう必要があるので、
    /// 宛先だけ入れて画面を出す（勝手に投げると public で外れ続ける）。
    /// </summary>
    private void OnSendToSnmp(object sender, RoutedEventArgs e)
    {
        if (AddressOf(sender) is not { Length: > 0 } address) return;

        _shell.SnmpGet.Host = address;
        Show(SnmpTab);
    }

    private void OnCopyRowAddress(object sender, RoutedEventArgs e) => CopyText(AddressOf(sender));

    /// <summary>
    /// 行の見えている値をタブ区切りでコピーする（2026-08-20 の UX 改善。チケットへ 1 行貼る用）。
    /// <b>行の型ごとの文字列化は書かない</b> — メニューが載っていた行の視覚ツリーを
    /// 文書順に下り、TextBlock の文字を集める。TextBox / PasswordBox は拾わない
    /// （編集欄と秘密を写さないため）。
    /// </summary>
    private void OnCopyRow(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Parent: ContextMenu { PlacementTarget: DependencyObject row } }) return;

        CopyText(RowTextOf(row));
    }

    /// <summary>行の中の TextBlock の文字を文書順にタブ区切りで並べる。internal は自己診断のため。</summary>
    internal static string RowTextOf(DependencyObject row)
    {
        var cells = new List<string>();

        void Walk(DependencyObject node)
        {
            int count = VisualTreeHelper.GetChildrenCount(node);

            for (int i = 0; i < count; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(node, i);

                if (child is TextBlock { Text.Length: > 0 } cell)
                {
                    cells.Add(cell.Text);
                    continue;   // TextBlock の中（Run）は Text が拾っている
                }

                // 編集欄と伏せ字は写さない
                if (child is TextBox or PasswordBox) continue;

                Walk(child);
            }
        }

        Walk(row);

        return string.Join("\t", cells);
    }

    private void CopyText(string text)
    {
        // 押しても無反応だと写ったのか分からない（2026-08-20 ユーザー指示）
        if (Services.ClipboardText.Copy(text))
            ShowNotice($"✓ コピーしました: {text}");
        else
            ShowNotice("コピーできませんでした", isProblem: true);
    }

    /// <summary>
    /// 無線画面は開かれたときに初めて API を叩く。
    /// Windows 11 24H2 以降はスキャンに位置情報の同意が要るので、
    /// 起動時に呼ぶと脈絡のないタイミングで許可を求めることになる。
    /// </summary>
    /// <summary>
    /// 自己診断が無線タブの画面を実体化して検査するときに true。
    /// タブ選択に反応して WLAN API へ触れるのを止める（位置情報の同意を求めないため）。
    /// </summary>
    internal bool SuppressWifiActivation;

    /// <summary>
    /// 「その他」の見出しに、いま開いているタブの名前を続ける。
    ///
    /// 束ねた中の帯は出さない（1 枚のために行を 1 つ食う）。代わりにここへ出すことで、
    /// 主タブの右側の空きを使う（2026-08-17 ユーザー指示）。
    ///
    /// <b>件数は数えて入れる。</b>直書きだと中身を増減したときに黙ってずれる。
    /// </summary>
    private void UpdateOtherHeader()
    {
        int count = OtherInnerTabs.Items.Count;
        string name = (OtherInnerTabs.SelectedItem as TabItem)?.Header?.ToString() ?? "";

        // 開いていないときに中身の名前を出すと、そのタブが開いているように見えてしまう
        OtherTab.Header = OtherTab.IsSelected && name.Length > 0
            ? $"その他　{count} ▾　│　{name}"
            : $"その他　{count} ▾";
    }

    private void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 内側の ListBox などの選択変更が浮上してくるので、TabControl 由来だけを扱う。
        // 内側の TabControl(サブタブ)の変更もここへ通す — 弾くと、束ねたタブを
        // 切り替えても OnActivated / OnDeactivated が走らなくなる
        if (e.OriginalSource is not TabControl) return;

        UpdateOtherHeader();

        if (SuppressWifiActivation) return;

        if (IsShowing(WifiTab))
            _shell.Wifi.OnActivated();
        else
            _shell.Wifi.OnDeactivated();

        // 接続一覧もタブが見えている間だけ OS を叩く
        if (IsShowing(ConnectionsTab))
            _shell.Connections.OnActivated();
        else
            _shell.Connections.OnDeactivated();

        // 遮断一覧も見えている間だけ WFP のエンジンを開く
        if (IsShowing(WfpTab))
            _shell.Wfp.OnActivated();
        else
            _shell.Wfp.OnDeactivated();

        // 経路の見張り(60 秒ごと)も、見えていないタブで裏を走らせない
        if (IsShowing(TraceTab))
            _shell.Trace.OnActivated();
        else
            _shell.Trace.OnDeactivated();

        // IP設定はタブを開いたときにアダプタを列挙し直す
        if (IsShowing(IpConfigTab))
            _shell.IpConfig.OnActivated();

    }

    /// <summary>
    /// そのタブが<b>実際に見えているか</b>。
    ///
    /// <b><see cref="TabItem.IsSelected"/> だけを見てはいけない。</b>これは
    /// 「その TabControl の中で選ばれているか」しか表さず、内側の TabControl は
    /// 生成時に先頭の子を自動で選ぶので、<b>親タブを一度も開いていなくても
    /// 内側の 1 枚目は true になる</b>。
    ///
    /// そのまま <c>OnActivated()</c> を呼ぶと、見えていないタブが OS を叩き始める
    /// （無線は位置情報の同意を求め、遮断は WFP のエンジンを開き、接続は ETW を回す）。
    /// 先祖の TabItem をすべてたどって確かめる。
    /// </summary>
    /// <summary>
    /// テキスト欄にファイルを重ねたとき。<b>ファイルのときだけ受ける。</b>
    ///
    /// <see cref="TextBox"/> は素で「文字のドラッグ」を扱うので、
    /// Preview の段で自分の答えを返さないと、既定の動きに先を越される。
    /// </summary>
    private void OnTextDragOver(object sender, DragEventArgs e)
    {
        e.Effects = Services.DroppedText.FilesOf(e).Length > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    /// <summary>
    /// 放り込まれたファイルを欄に読み込む。
    ///
    /// <b>置き換える</b>（足すのではない）。作業前後の貼り付けも設定の貼り付けも、
    /// 前のものが混ざると気づかないまま誤った差分を見ることになる。
    /// 複数まとめて放られたら 1 つ目だけを使い、その旨を知らせる。
    /// </summary>
    private void OnTextDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        if (sender is not TextBox box) return;

        string[] files = Services.DroppedText.FilesOf(e);
        if (files.Length == 0) return;

        if (Services.DroppedText.TryRead(files[0], out string problem) is not { } text)
        {
            ShowNotice(problem, isProblem: true);
            return;
        }

        box.Text = text;
        box.Focus();

        ShowNotice(files.Length > 1
            ? $"{System.IO.Path.GetFileName(files[0])} を読み込みました（1 つ目だけ）。"
            : $"{System.IO.Path.GetFileName(files[0])} を読み込みました。");
    }

    /// <summary>
    /// 管理者として動いているとき、ドロップを受ける欄のツールチップに断りを書き足す。
    ///
    /// 昇格したプロセスへは、エクスプローラー（非管理者）からのドラッグ&ドロップが
    /// Windows の保護（UIPI）で<b>届かない</b> — DragOver すら来ず、カーソルが 🚫 のままになる。
    /// アプリでは直せないので、黙って効かない代わりにここで断る（2026-08-21 ユーザー報告）。
    /// 試験項目の行の並べ替えは同一プロセス内のドラッグなので影響を受けない
    /// （行テンプレートの中は論理ツリーに載らず、この走査にも掛からない）。
    /// </summary>
    internal const string DropBlockedNote =
        "管理者として実行中は、エクスプローラーからのファイルの放り込みを Windows が遮ります（貼り付けは使えます）。";

    private void MarkBlockedDropTargets()
    {
        if (!Services.NetTraceSession.IsAdministrator) return;

        foreach (FrameworkElement target in DropTargets(this))
            target.ToolTip = target.ToolTip is string { Length: > 0 } tip
                ? tip + "\n" + DropBlockedNote
                : DropBlockedNote;
    }

    /// <summary>ドロップを受ける要素（AllowDrop 付き）を論理ツリーから集める。</summary>
    internal static List<FrameworkElement> DropTargets(DependencyObject node)
    {
        List<FrameworkElement> found = [];
        Collect(node, found);
        return found;

        static void Collect(DependencyObject node, List<FrameworkElement> found)
        {
            if (node is FrameworkElement { AllowDrop: true } element) found.Add(element);

            foreach (object? child in LogicalTreeHelper.GetChildren(node))
            {
                if (child is DependencyObject deeper) Collect(deeper, found);
            }
        }
    }

    /// <summary>試験の項目一覧にファイルを放り込んだとき。書式の解釈は VM に任せる。</summary>
    private void OnVerifyItemsDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        string[] files = Services.DroppedText.FilesOf(e);
        if (files.Length == 0) return;

        if (Services.DroppedText.TryRead(files[0], out string problem) is not { } text)
        {
            ShowNotice(problem, isProblem: true);
            return;
        }

        _shell.Verify.LoadItemsFrom(text, System.IO.Path.GetFileName(files[0]));
    }

    /// <summary>ログ採取の機器一覧に放り込まれた CSV を、「CSV から取り込む」と同じ経路で読む。</summary>
    private void OnCollectCsvDrop(object sender, DragEventArgs e)
    {
        e.Handled = true;

        string[] files = Services.DroppedText.FilesOf(e);
        if (files.Length == 0) return;

        if (Services.DroppedText.TryRead(files[0], out string problem) is not { } text)
        {
            ShowNotice(problem, isProblem: true);
            return;
        }

        _shell.Collect.ImportCsvText(text);
    }

    /// <summary>掴んでいる試験項目の行。掴んでいなければ null。</summary>
    private ViewModels.VerifyRowViewModel? _draggingRow;

    /// <summary>掴んだ場所。ここから少し動いて初めてドラッグと見なす。</summary>
    private Point _dragFrom;

    /// <summary>
    /// 試験項目の並べ替え。<b>つまみ（⋮）からだけ掴める</b> —
    /// 行のほとんどは入力欄で、どこからでも掴めるようにすると文字の選択ができなくなる。
    /// </summary>
    private void OnVerifyRowGrab(object sender, MouseButtonEventArgs e)
    {
        _draggingRow = (sender as FrameworkElement)?.DataContext as ViewModels.VerifyRowViewModel;
        _dragFrom = e.GetPosition(this);
    }

    /// <summary>
    /// つまみを掴んだまま動かしたらドラッグを始める。<b>動きの検出は一覧側</b>で拾う —
    /// つまみの上だけを見ていると、素早く動かしたときに始まらない。
    /// </summary>
    private void OnVerifyRowDrag(object sender, MouseEventArgs e)
    {
        if (_draggingRow is not { } row) return;

        if (e.LeftButton != MouseButtonState.Pressed)
        {
            _draggingRow = null;   // 掴んだまま離されていた
            return;
        }

        Vector moved = e.GetPosition(this) - _dragFrom;

        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance
            && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _draggingRow = null;

        DragDrop.DoDragDrop(VerifyItems, new DataObject(typeof(ViewModels.VerifyRowViewModel), row),
                            DragDropEffects.Move);
    }

    /// <summary>
    /// 行を重ねている間。<b>ファイルの投げ込みには触らない</b>（外側の枠が受け持つ）ので、
    /// 行のドラッグのときだけ答えて止める。
    /// </summary>
    private void OnVerifyRowDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(typeof(ViewModels.VerifyRowViewModel))) return;

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
    }

    private void OnVerifyRowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(typeof(ViewModels.VerifyRowViewModel)) is not ViewModels.VerifyRowViewModel moving)
            return;

        e.Handled = true;

        _shell.Verify.MoveRow(moving, RowUnder<ViewModels.VerifyRowViewModel>(e));
    }

    /// <summary>落とした場所にある行。行の外（一覧の余白）なら null。</summary>
    private static T? RowUnder<T>(DragEventArgs e) where T : class
    {
        for (DependencyObject? node = e.OriginalSource as DependencyObject;
             node is not null;
             node = ClickedParentOf(node))
        {
            if (node is ListBoxItem { DataContext: T row }) return row;
        }

        return null;
    }

    /// <summary>一覧に重ねたとき。こちらは既定の動きが無いので DragOver で足りる。</summary>
    private void OnListDragOver(object sender, DragEventArgs e)
    {
        e.Effects = Services.DroppedText.FilesOf(e).Length > 0
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
    }

    /// <summary>
    /// まとめたタブ（見出しに ▾ が付いているもの）を押したら、中身を縦並びのメニューで出す。
    ///
    /// 切り替えの帯を横にも縦にも置かない代わりに、<b>押したときだけメニューを降ろす</b>。
    /// 項目は内側の <see cref="TabControl"/> から組み立てるので、
    /// 中身を足しても<b>ここは直さなくてよい</b>（見出しの件数だけ直す）。
    /// </summary>
    private void OnMainTabsMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source) return;

        // 押されたのが見出しかどうか。中身の上のクリックまで拾わない
        TabItem? tab = null;
        for (DependencyObject? node = source; node is not null; node = ClickedParentOf(node))
        {
            if (node is TabItem item) { tab = item; break; }
            if (node is TabControl) return;   // 見出しの外（中身の側）だった
        }

        if (tab is null || !ReferenceEquals(ItemsControl.ItemsControlFromItemContainer(tab), MainTabs))
            return;

        // メニューを出すのは「その他」だけ。サブタブを持つ主タブ（Meraki）は
        // 帯をそのまま見せるので、ここで捕まえると押しても切り替わらなくなる
        // （2026-08-17 ユーザー指摘）
        if (!ReferenceEquals(tab, OtherTab)) return;

        OpenOtherTabsMenu();

        // タブそのものは選ばない。メニューを閉じただけで画面が変わると、
        // 「見に行っただけ」のつもりが測定中の画面から飛ばされる
        e.Handled = true;
    }

    /// <summary>
    /// 「その他」のドロップダウンを降ろす（見出しクリックから）。
    /// 表示メニューにも同じ動線を置いたことがあるが、不要とのユーザー指示で
    /// 2026-08-20 に削除した。再提案しないこと。
    /// </summary>
    private void OpenOtherTabsMenu()
    {
        TabItem tab = OtherTab;

        if (InnerTabsOf(tab) is not { } inner || inner.Items.Count == 0) return;

        var menu = new ContextMenu
        {
            PlacementTarget = tab,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom,
        };

        // 中身は Tag で分類してある（調べる / NW機器 / 受ける）。
        // 16 枚を素で並べると探せないので、変わり目に見出しを挟む
        string group = "";

        foreach (object? item in inner.Items)
        {
            if (item is not TabItem child) continue;

            if (child.Tag is string tagged && tagged != group)
            {
                group = tagged;

                if (menu.Items.Count > 0) menu.Items.Add(new Separator());

                // 押せない見出し。分類そのものは画面ではないので選ばせない
                menu.Items.Add(new MenuItem { Header = group, IsEnabled = false });
            }

            var entry = new MenuItem
            {
                Header = child.Header,
                IsCheckable = true,
                IsChecked = child.IsSelected && tab.IsSelected,
            };

            // 選んで初めて画面が変わる。開いただけで飛ばさない（ユーザー指示）。
            // 中身を先に選んでから親を開く順にすると、親の切り替えで戻されない
            entry.Click += (_, _) =>
            {
                child.IsSelected = true;
                tab.IsSelected = true;
            };

            menu.Items.Add(entry);
        }

        menu.IsOpen = true;
    }

    /// <summary>
    /// 切り詰められた長文のセルをクリックしたら、全文を別窓で出す（選択もコピーもできる）。
    /// TextTrimming は見た目だけで、Text プロパティは全文を持っている。
    /// </summary>
    private void OnExpandableCellClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { Text.Length: > 0 } cell) return;

        TextViewDialog.Show(this, "詳細", cell.Text);
        e.Handled = true;
    }

    /// <summary>
    /// ⓘ をクリックしたら、ホバーの説明と同じ文面を別窓で出す（選択もコピーもできる）。
    /// 本文 1 行目がタブ名という TabHelp の決まりがあるので、タイトルは固定でよい。
    /// </summary>
    private void OnTabInfoClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not TextBlock { ToolTip: string text } || text.Length == 0) return;

        // TabHelp は全角スペースで頭を揃えた箇条書き。桁を揃えた文は折り返さない決まり
        // （ライセンスと同じ扱い。2026-08-20 のレビュー指摘）
        TextViewDialog.Show(this, "この画面について", text, wrap: false);
        e.Handled = true;
    }

    // ===== 分割の仕切り（GridSplitter の代わり） =====
    //
    // GridSplitter は星サイズ＋Min の組で端までドラッグすると比率が跳ね戻る
    // （2026-08-20 に実機で報告された）。列幅ドラッグと同じ流儀
    // （掴んでからの総移動量＋明示クランプ）で、Min にぴったり止める。

    private Grid? _paneGrid;
    private int _paneIndex;
    private bool _paneCols;
    private double _paneStartPrev;
    private double _paneStartNext;
    private double _paneStartPos;

    /// <summary>宛先リスト(編集欄)を開いていたときの行の高さ。閉じている間だけ覚える(プロセス内)。
    /// 既定は結果の行と同じ 1*(=ほぼ半々)。Auto にすると宛先が多いとき中身なりに伸びて、
    /// 仕切りが画面の外まで行ってしまう(2026-08-21 ユーザー報告)。</summary>
    private GridLength _pingEditorHeight = new(1, GridUnitType.Star);

    /// <summary>
    /// 宛先リストの開閉。仕切りのドラッグで星になった行は、閉じても空のまま場所を
    /// 取り続けるので、行ごと畳んで(Auto + Min 0)、開いたら前回の高さへ戻す。
    /// 比率を保存しない決まりはそのまま(覚えるのはプロセスの中だけ)。
    /// </summary>
    private void OnTargetListToggle(object sender, RoutedEventArgs e)
    {
        bool open = TargetListToggle.IsChecked == true;

        if (!open)
            _pingEditorHeight = PingEditorRow.Height;

        PingEditorRow.Height = open ? _pingEditorHeight : GridLength.Auto;
        PingEditorRow.MinHeight = open ? 90 : 0;
    }

    private void OnPaneGrab(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e)
    {
        // 掴み損ねたときに前回の仕切りが残らないよう、先に必ず捨てる
        _paneGrid = null;

        if (sender is not System.Windows.Controls.Primitives.Thumb thumb) return;

        // 仕切りの親 Grid。テンプレートの Border から上がることは無い（Thumb 自身が子）
        DependencyObject? node = VisualTreeHelper.GetParent(thumb);
        while (node is not null && node is not Grid) node = VisualTreeHelper.GetParent(node);
        if (node is not Grid grid) return;

        _paneGrid = grid;
        _paneCols = Equals(thumb.Tag, "cols");
        _paneIndex = _paneCols ? Grid.GetColumn(thumb) : Grid.GetRow(thumb);

        if (_paneCols)
        {
            _paneStartPrev = grid.ColumnDefinitions[_paneIndex - 1].ActualWidth;
            _paneStartNext = grid.ColumnDefinitions[_paneIndex + 1].ActualWidth;
            _paneStartPos = Mouse.GetPosition(this).X;
        }
        else
        {
            _paneStartPrev = grid.RowDefinitions[_paneIndex - 1].ActualHeight;
            _paneStartNext = grid.RowDefinitions[_paneIndex + 1].ActualHeight;
            _paneStartPos = Mouse.GetPosition(this).Y;
        }
    }

    private void OnPaneDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        if (_paneGrid is not { } grid) return;

        // Thumb は自分も動くので、1 回ぶんの差分は足し込まない（列幅ドラッグと同じ理由）。
        // 総移動量を Min の範囲でクランプするので、端まで引いても跳ね戻らない
        double total = (_paneCols ? Mouse.GetPosition(this).X : Mouse.GetPosition(this).Y) - _paneStartPos;

        if (_paneCols)
        {
            ColumnDefinition prev = grid.ColumnDefinitions[_paneIndex - 1];
            ColumnDefinition next = grid.ColumnDefinitions[_paneIndex + 1];
            double d = PaneClamp(total, _paneStartPrev, prev.MinWidth, _paneStartNext, next.MinWidth);

            // 星の重みをピクセル比で入れ直す。見た目はいまの寸法のままで、
            // 窓を伸縮したときは今までどおり比率で追随する
            prev.Width = new GridLength(_paneStartPrev + d, GridUnitType.Star);
            next.Width = new GridLength(_paneStartNext - d, GridUnitType.Star);
        }
        else
        {
            RowDefinition prev = grid.RowDefinitions[_paneIndex - 1];
            RowDefinition next = grid.RowDefinitions[_paneIndex + 1];
            double d = PaneClamp(total, _paneStartPrev, prev.MinHeight, _paneStartNext, next.MinHeight);

            prev.Height = new GridLength(_paneStartPrev + d, GridUnitType.Star);
            next.Height = new GridLength(_paneStartNext - d, GridUnitType.Star);
        }
    }

    /// <summary>
    /// 仕切りの移動量を、両側が Min を割らない範囲に丸める。
    /// 窓が縮んでいて既に Min を割っているときは、その向きへは動かさない（0 で止める）。
    /// internal は自己診断のため。
    /// </summary>
    internal static double PaneClamp(
        double total, double startPrev, double minPrev, double startNext, double minNext)
    {
        double lower = -Math.Max(0, startPrev - minPrev);
        double upper = Math.Max(0, startNext - minNext);

        return Math.Clamp(total, lower, upper);
    }

    /// <summary>そのタブが中に持っている切り替え。無ければ null。</summary>
    /// <summary>
    /// その要素が載っているタブ。<b>論理ツリーでたどる</b> —
    /// タブの中身は視覚ツリーでは <c>TabItem</c> の下に無い（親 <c>TabControl</c> の
    /// <c>ContentPresenter</c> の下に置かれる）ので、視覚ツリーでは素通りする。
    /// </summary>
    private static TabItem? TabOf(object? source)
    {
        for (DependencyObject? node = source as DependencyObject;
             node is not null;
             node = LogicalTreeHelper.GetParent(node))
        {
            if (node is TabItem tab) return tab;
        }

        return null;
    }

    /// <summary>いま見えているタブ。束ねたタブの中を選んでいるなら、その中身の方。</summary>
    private TabItem? ShownTab()
    {
        TabItem? tab = MainTabs.SelectedItem as TabItem;

        while (tab is not null && InnerTabsOf(tab) is { SelectedItem: TabItem child })
            tab = child;

        return tab;
    }

    private static TabControl? InnerTabsOf(TabItem tab)
    {
        if (tab.Content is not DependencyObject node) return null;

        foreach (object? child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is TabControl inner) return inner;
            if (child is DependencyObject deeper && FindInner(deeper) is { } found) return found;
        }

        return null;
    }

    private static TabControl? FindInner(DependencyObject node)
    {
        foreach (object? child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is TabControl inner) return inner;
            if (child is DependencyObject deeper && FindInner(deeper) is { } found) return found;
        }

        return null;
    }

    internal static bool IsShowing(TabItem tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        foreach (TabItem item in SelfAndAncestorTabs(tab))
        {
            if (!item.IsSelected) return false;
        }

        return true;
    }

    /// <summary>
    /// そのタブ自身と、それを包んでいるタブを内側から順に返す。
    ///
    /// <b>視覚ツリーでは遡れない。</b>タブの中身は <see cref="TabItem"/> の下ではなく、
    /// 親 <see cref="TabControl"/> の <c>ContentPresenter</c> の下に置かれるので、
    /// 内側のタブから視覚的な親をたどっても外側の <see cref="TabItem"/> を素通りする。
    /// 論理ツリー（XAML に書いたとおりの入れ子）でたどること。
    ///
    /// これを間違えると、<b>右クリックからの遷移で親タブが開かず画面が変わらない</b>し、
    /// <b>見えていないタブが「見えている」ことになって OS を叩き始める</b>。
    /// </summary>
    private static IEnumerable<TabItem> SelfAndAncestorTabs(TabItem tab)
    {
        for (DependencyObject? node = tab; node is not null; node = ParentOf(node))
        {
            if (node is TabItem item) yield return item;
        }
    }

    /// <summary>論理の親。テンプレートの中の要素では切れるので、そのときだけ視覚の親を使う。</summary>
    private static DependencyObject? ParentOf(DependencyObject node)
        => LogicalTreeHelper.GetParent(node) ?? VisualTreeHelper.GetParent(node);

    /// <summary>
    /// そのタブを開く。<b>束ねられていれば先祖もたどって開く</b>。
    /// <c>IsSelected = true</c> だけでは、親タブが選ばれていないと画面が変わらない。
    /// </summary>
    internal static void Show(TabItem tab)
    {
        ArgumentNullException.ThrowIfNull(tab);

        // 外側から順に選ぶ。先に内側を選んでも親の切り替えで戻されることがある
        TabItem[] chain = [.. SelfAndAncestorTabs(tab)];

        for (int i = chain.Length - 1; i >= 0; i--)
            chain[i].IsSelected = true;
    }

    /// <summary>
    /// 閉じるときは測定に停止を伝えるだけで、<b>完了は待たない</b>。
    ///
    /// 以前は完了を待ってから閉じていたが、名前解決中やタイムアウト待ちの宛先が
    /// あると終了が数秒固まっていた。設定と宛先リストは編集のたびに保存しているので、
    /// ここで待って守るものは無い。ソケットはプロセス終了時に OS が片付ける。
    /// </summary>
    protected override void OnClosing(CancelEventArgs e)
    {
        base.OnClosing(e);

        try
        {
            _shell.Monitor.BeginStop();
            _shell.Tcp.BeginStop();
            _shell.Wifi.OnDeactivated();
            _shell.Connections.OnDeactivated();
            _shell.Wfp.OnDeactivated();

            // 自分で立てた WFP の記録設定を戻す(システム全体に効く設定なので置き去りにしない)
            _shell.Wfp.RestoreCollectionSetting();

            _shell.Ftp.Reset();
            _shell.Tftp.Reset();
            _shell.Sftp.Reset();
            _shell.Syslog.Reset();
            _shell.SnmpTrap.Reset();

            // 収集タブは機器の一覧とコマンドだけ覚える(パスワードは覚えない)
            _shell.Collect.Save();

            // 試験タブは項目とプロキシの定義だけ覚える(次に開いたとき続きから)
            _shell.Verify.SaveSettings();

            // 目視の途中で閉じられても、切り替えた Windows のプロキシ設定は必ず戻す
            // （WFP の記録設定と同じ考え方。置き去りにしない）
            _shell.Verify.RestoreProxyIfChanged();
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "MainWindow.OnClosing");
        }
    }
}
