using System.Collections.ObjectModel;
using System.Windows;
using System.IO;
using System.Text;
using Microsoft.Win32;
using NetworkToys.App.Mvvm;
using NetworkToys.App.Services;
using NetworkToys.Core.Cloud;
using NetworkToys.Core.Design;
using NetworkToys.Core.Reporting;
using NetworkToys.Core.Terminal;
using NetworkToys.Core.Verify;
using NetworkToys.Core.Work;

namespace NetworkToys.App.ViewModels;

/// <summary>組織の選択肢。</summary>
public sealed record MerakiOrganizationItem(string Id, string Name);

/// <summary>クライアント一覧をどこまでさかのぼるか。</summary>
public sealed record MerakiTimespan(string Name, int Seconds);


/// <summary>
/// Meraki ダッシュボードから組織配下の情報を引く画面。
///
/// API キーは保存しない（毎回入力）。settings.json にも書かないし、
/// 画面の文言やログにも載せない — このアプリは画面を PNG で保存できるので、
/// 入力欄も伏せ字（PasswordBox）にしてある。
/// </summary>
public sealed class MerakiViewModel : ObservableObject, IDisposable
{
    private readonly MerakiDashboard _dashboard = new();

    private string _apiKey = "";
    private MerakiOrganizationItem? _selectedOrganization;
    private MerakiNetworkRow? _selectedNetwork;
    private MerakiTimespan _selectedTimespan;
    /// <summary>何もしていないときの案内。<b>初期値と Reset の戻し先を空文字にしない</b>
    /// （何をすれば動くかが見えない画面になる。2026-08-20 の UI 改善）。</summary>
    private const string IdleHint = "API キーを入れて「取得」を押すと一覧が出ます。";

    private string _status = IdleHint;
    private string _notice = "";
    private string _globalIpText = "—";
    private bool _showConnection = true;
    private bool _allNetworks;


    /// <summary>組織の一覧を作り直している最中か。選び直しの合図と区別する。</summary>
    private bool _syncingOrganizations;
    private bool _isBusy;
    private CancellationTokenSource? _cts;

    public MerakiViewModel()
    {
        _selectedTimespan = Timespans[1];

        FetchCommand = new RelayCommand(() => _ = FetchAsync(), () => !IsBusy && ApiKey.Length > 0);
        FetchDevicesCommand = new RelayCommand(() => _ = FetchDevicesAsync(),
            () => !IsBusy && ApiKey.Length > 0 && SelectedOrganization is not null);
        FetchUplinksCommand = new RelayCommand(() => _ = FetchUplinksAsync(),
            () => !IsBusy && ApiKey.Length > 0 && SelectedOrganization is not null);
        FetchClientsCommand = new RelayCommand(
            () => _ = FetchClientsAsync(),
            () => !IsBusy && ApiKey.Length > 0 && (AllNetworks || SelectedNetwork is not null));
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        ToggleConnectionCommand = new RelayCommand(() => ShowConnection = !ShowConnection);

        // 機器や回線をまだ取っていなくても押せる（要るものはこの中で取りに行く）。
        // 「拠点は選んだのに押せない」と分からないので（2026-08-18 報告）
        FetchInstallCheckCommand = new RelayCommand(
            () => _ = FetchInstallCheckAsync(),
            () => !IsBusy && ApiKey.Length > 0 && SelectedNetwork is not null);
        SaveInstallCheckCommand = new RelayCommand(
            () => Save("check", MerakiInstallCheck.ToCsv([.. CheckRows])), () => CheckRows.Count > 0);
        MarkCheckPassCommand = new RelayCommand<MerakiCheckRow>(row => MarkCheck(row, CheckVerdict.Pass));
        MarkCheckFailCommand = new RelayCommand<MerakiCheckRow>(row => MarkCheck(row, CheckVerdict.Fail));

        FetchSitesCommand = new RelayCommand(() => _ = FetchSitesAsync(),
            () => !IsBusy && ApiKey.Length > 0 && NetworkRows.Count > 0);
        FetchDhcpCommand = new RelayCommand(() => _ = FetchDhcpAsync(),
            () => !IsBusy && ApiKey.Length > 0 && DeviceRows.Count > 0);
        FetchAlertsCommand = new RelayCommand(() => _ = FetchAlertsAsync(),
            () => !IsBusy && ApiKey.Length > 0 && SelectedOrganization is not null);

        SaveSitesCommand = new RelayCommand(() => Save("sites", MerakiCatalog.ToCsv([.. SiteRows])),
            () => SiteRows.Count > 0);
        SaveDhcpCommand = new RelayCommand(() => Save("dhcp", MerakiCatalog.ToCsv([.. DhcpRows])),
            () => DhcpRows.Count > 0);
        SaveAlertsCommand = new RelayCommand(() => Save("alerts", MerakiCatalog.ToCsv([.. AlertRows])),
            () => AlertRows.Count > 0);


        SaveXlsxCommand = new RelayCommand<string>(SaveXlsx, key => TableOf(key) is not null);

        SaveDevicesCommand = new RelayCommand(
            () => Save("devices", MerakiCatalog.ToCsv(DeviceRows)), () => DeviceRows.Count > 0);
        SaveUplinksCommand = new RelayCommand(
            () => Save("uplinks", MerakiCatalog.ToCsv(UplinkRows)), () => UplinkRows.Count > 0);
        SaveClientsCommand = new RelayCommand(
            () => Save("clients", MerakiCatalog.ToCsv(ClientRows)), () => ClientRows.Count > 0);

        Notice = "ダッシュボードの [My profile] で発行した API キーを入れて「取得」を押してください。"
               + "キーは保存しません。";
    }

    /// <summary>消去のときに画面の伏せ字欄も空にしてもらう（PasswordBox はバインドできない）。</summary>
    public event EventHandler? ApiKeyCleared;

    public ObservableCollection<MerakiOrganizationItem> Organizations { get; } = [];
    public ObservableCollection<MerakiNetworkRow> NetworkRows { get; } = [];
    public ObservableCollection<MerakiDeviceRow> DeviceRows { get; } = [];
    public ObservableCollection<MerakiUplinkRow> UplinkRows { get; } = [];
    public ObservableCollection<MerakiClientRow> ClientRows { get; } = [];

    /// <summary>
    /// クライアント一覧の絞り込み。数百台になる画面なので、目当ての 1 台を探せるように。
    /// 行を collection から抜かずに <see cref="System.ComponentModel.ICollectionView"/> で
    /// 隠す（Ping の絞り込みと同じやり方）。CSV 書き出しは絞る前の全行のまま。
    /// </summary>
    public string ClientFilter
    {
        get => _clientFilter;
        set
        {
            if (!SetProperty(ref _clientFilter, value)) return;

            System.ComponentModel.ICollectionView view =
                System.Windows.Data.CollectionViewSource.GetDefaultView(ClientRows);

            // 絞り込みが空のときは述語ごと外す（毎行の判定を走らせない）
            view.Filter = _clientFilter.Length == 0 ? null : o => MatchesClient((MerakiClientRow)o);
        }
    }

    private string _clientFilter = string.Empty;

    private bool MatchesClient(MerakiClientRow row)
        => row.Network.Contains(_clientFilter, StringComparison.OrdinalIgnoreCase)
        || row.Description.Contains(_clientFilter, StringComparison.OrdinalIgnoreCase)
        || row.Ip.Contains(_clientFilter, StringComparison.OrdinalIgnoreCase)
        || row.Mac.Contains(_clientFilter, StringComparison.OrdinalIgnoreCase)
        || row.Vlan.Contains(_clientFilter, StringComparison.OrdinalIgnoreCase)
        || row.Manufacturer.Contains(_clientFilter, StringComparison.OrdinalIgnoreCase);

    /// <summary>導入時確認の判定。</summary>
    public ObservableCollection<MerakiCheckRow> CheckRows { get; } = [];

    /// <summary>拠点ごとの内訳（クライアント数とセグメント）。</summary>
    public ObservableCollection<MerakiSiteRow> SiteRows { get; } = [];

    /// <summary>DHCP の払い出し状況。</summary>
    public ObservableCollection<MerakiDhcpRow> DhcpRows { get; } = [];

    /// <summary>アラート。</summary>
    public ObservableCollection<MerakiAlertRow> AlertRows { get; } = [];

    public IReadOnlyList<MerakiTimespan> Timespans { get; } =
    [
        new("1 時間", 3600),
        new("1 日", 86400),
        new("7 日", 604800),
        new("30 日", 2592000),
    ];

    public RelayCommand FetchCommand { get; }

    /// <summary>機器タブの取得（機器・状態・ファーム・MX の LAN IP）。</summary>
    public RelayCommand FetchDevicesCommand { get; }

    /// <summary>アップリンクタブの取得。</summary>
    public RelayCommand FetchUplinksCommand { get; }

    public RelayCommand FetchClientsCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand SaveDevicesCommand { get; }
    public RelayCommand SaveUplinksCommand { get; }
    public RelayCommand SaveClientsCommand { get; }

    /// <summary>選んだ拠点の導入時確認を一通り走らせる。</summary>
    public RelayCommand FetchInstallCheckCommand { get; }

    /// <summary>Excel ブックで保存する（どの一覧かは CommandParameter で渡す）。</summary>
    public RelayCommand<string> SaveXlsxCommand { get; }

    public RelayCommand SaveInstallCheckCommand { get; }

    /// <summary>目視の項目に人が合否を付ける（API から判定できないもの）。</summary>
    public RelayCommand<MerakiCheckRow> MarkCheckPassCommand { get; }

    public RelayCommand<MerakiCheckRow> MarkCheckFailCommand { get; }

    /// <summary>拠点ごとの内訳を取る。クライアント数の期間は 30 日で固定。</summary>
    public RelayCommand FetchSitesCommand { get; }

    /// <summary>MX の DHCP 払い出し状況を取る。</summary>
    public RelayCommand FetchDhcpCommand { get; }

    /// <summary>組織のアラートを取る。</summary>
    public RelayCommand FetchAlertsCommand { get; }

    /// <summary>MX ごとの利用率を取る（device utilization）。</summary>




    public RelayCommand SaveSitesCommand { get; }
    public RelayCommand SaveDhcpCommand { get; }
    public RelayCommand SaveAlertsCommand { get; }

    /// <summary>伏せ字欄から流し込まれる。ここ以外のどこにも書き出さない。</summary>
    public string ApiKey
    {
        get => _apiKey;
        set
        {
            if (!SetProperty(ref _apiKey, value)) return;

            FetchCommand.RaiseCanExecuteChanged();
            FetchClientsCommand.RaiseCanExecuteChanged();
            FetchInstallCheckCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// 接続の欄を開いているか。<b>取れたら畳む</b> — 一覧に画面を使いたいので。
    /// もう一度出したいときは「接続先」を押す。
    /// </summary>
    public bool ShowConnection
    {
        get => _showConnection;
        private set
        {
            if (SetProperty(ref _showConnection, value)) OnPropertyChanged(nameof(ConnectionToggleText));
        }
    }

    /// <summary>畳んでいる間も、どの組織を見ているかは 1 行で見えるようにしておく。</summary>
    public string ConnectionSummary => SelectedOrganization is { } organization
        ? $"{organization.Name}（キーは入力済み）"
        : "組織は未選択";

    public string ConnectionToggleText => ShowConnection ? "接続先 ▴" : "接続先 ▾";

    public RelayCommand ToggleConnectionCommand { get; }

    /// <summary>取れたら接続の欄を畳む。押せば戻る。</summary>
    private void Collapse()
    {
        ShowConnection = false;
        OnPropertyChanged(nameof(ConnectionSummary));
    }

    /// <summary>
    /// 見に行く組織。
    ///
    /// <b>選び直したら、そのまま取りに行く</b>（2026-08-18 ユーザー指示）。
    /// 「選んでからもう一度取得を押す」は見落とされる — 選んだ時点で用は決まっている。
    /// 一覧を作っている最中（<see cref="SyncOrganizations"/>）は動かさない。
    /// </summary>
    public MerakiOrganizationItem? SelectedOrganization
    {
        get => _selectedOrganization;
        set
        {
            if (!SetProperty(ref _selectedOrganization, value)) return;

            OnPropertyChanged(nameof(ConnectionSummary));
            OnPropertyChanged(nameof(NeedsOrganization));

            FetchDevicesCommand.RaiseCanExecuteChanged();
            FetchUplinksCommand.RaiseCanExecuteChanged();
            FetchAlertsCommand.RaiseCanExecuteChanged();

            if (_syncingOrganizations || value is null || IsBusy || ApiKey.Length == 0) return;

            _ = FetchAsync();
        }
    }

    /// <summary>
    /// 組織を選ぶのを待っているか。<b>案内は目立つ場所（⚠ の行）に出す</b> —
    /// 状態の行に書いていたので見落とされた（2026-08-18 報告）。
    /// </summary>
    public bool NeedsOrganization => Organizations.Count > 1 && SelectedOrganization is null;

    /// <summary>
    /// 見る拠点。<b>どのサブタブでも同じ選択を使う</b>（2026-08-18 ユーザー指示。
    /// クライアントタブと同じ形にした）。機器・回線・DHCP・アラートは組織まとめで
    /// 取ってあるので、<b>選び直しても取り直さず、出す行を絞るだけ</b>。
    /// </summary>
    public MerakiNetworkRow? SelectedNetwork
    {
        get => _selectedNetwork;
        set
        {
            if (!SetProperty(ref _selectedNetwork, value)) return;

            FetchClientsCommand.RaiseCanExecuteChanged();
            FetchInstallCheckCommand.RaiseCanExecuteChanged();

            ShowByNetwork();
        }
    }

    public MerakiTimespan SelectedTimespan
    {
        get => _selectedTimespan;
        set => SetProperty(ref _selectedTimespan, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    /// <summary>恒常的な案内（キー未入力など）。Status と違って上書きで消えない。</summary>
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

    /// <summary>
    /// クライアントを全拠点から取るか。<b>拠点の数だけ呼び出しが増える</b>ので、
    /// 既定は選んだ拠点だけ。
    /// </summary>
    /// <summary>
    /// 拠点で絞らずに見るか。<b>どのサブタブでも同じ</b>（クライアントの取得先もこれで決まる）。
    /// </summary>
    public bool AllNetworks
    {
        get => _allNetworks;
        set
        {
            if (!SetProperty(ref _allNetworks, value)) return;

            FetchClientsCommand.RaiseCanExecuteChanged();
            ShowByNetwork();
        }
    }

    /// <summary>
    /// 選んだ拠点のぶんだけ一覧に出す。
    /// <b>取ってきたものは捨てない</b>（絞り直すたびに取り直すと待たされる）。
    /// </summary>
    private void ShowByNetwork()
    {
        string? only = AllNetworks ? null : SelectedNetwork?.Name;

        Replace(DeviceRows, Only(_allDevices, only, d => d.Network));
        Replace(UplinkRows, Only(
            MerakiCatalog.WithoutOfflineDevices(_allUplinks, _allDevices), only, u => u.Network));
        Replace(DhcpRows, Only(_allDhcp, only, d => d.Network));
        Replace(AlertRows, Only(_allAlerts, only, a => a.Network));

        GlobalIpText = MerakiCatalog.GlobalIpSummary(UplinkRows);

        RefreshSaveCommands();
        FetchDhcpCommand.RaiseCanExecuteChanged();
        FetchInstallCheckCommand.RaiseCanExecuteChanged();
    }

    private static IReadOnlyList<T> Only<T>(
        IReadOnlyList<T> rows, string? network, Func<T, string> networkOf)
        => network is null ? rows : [.. rows.Where(r => networkOf(r) == network)];

    /// <summary>利用率で見るか、通信量で見るか。</summary>
    /// <summary>アップリンクのグローバル IP をまとめた 1 行。</summary>
    public string GlobalIpText
    {
        get => _globalIpText;
        private set => SetProperty(ref _globalIpText, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;

            FetchCommand.RaiseCanExecuteChanged();
            FetchClientsCommand.RaiseCanExecuteChanged();
            FetchInstallCheckCommand.RaiseCanExecuteChanged();
            FetchDevicesCommand.RaiseCanExecuteChanged();
            FetchUplinksCommand.RaiseCanExecuteChanged();
            FetchSitesCommand.RaiseCanExecuteChanged();
            FetchDhcpCommand.RaiseCanExecuteChanged();
            FetchAlertsCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// <b>最初の「取得」は組織と拠点の一覧だけ</b>（2026-08-18 ユーザー指示）。
    ///
    /// 以前はここで機器・回線・MX の LAN まで取っていたので、拠点の多い組織では
    /// 待ち時間が長すぎた。<b>細かいものは、そのタブの「取得」で取りに行く。</b>
    /// 組織が 1 つならそのまま続け、複数あるときは選んでもらってからもう一度押してもらう。
    /// </summary>
    private async Task FetchAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        _dashboard.ResetTruncation();

        try
        {
            CancellationToken token = _cts.Token;

            Status = "組織を確認しています…";
            IReadOnlyList<(string Id, string Name)> organizations =
                MerakiCatalog.ParseOrganizations(await _dashboard.OrganizationsAsync(ApiKey, token));

            SyncOrganizations(organizations);

            if (Organizations.Count == 0)
            {
                Status = "参照できる組織がありませんでした。";
                return;
            }

            if (Organizations.Count == 1)
                SelectedOrganization = Organizations[0];

            if (SelectedOrganization is null)
            {
                Status = "";
                Notice = $"⚠ この API キーでは組織が {Organizations.Count} 件見えます。"
                       + "上の「組織」から 1 つ選んでください（選べばそのまま取りに行きます）。";

                OnPropertyChanged(nameof(NeedsOrganization));

                // 選ぶまで待つ。畳んでいると選べないので、必ず開けておく
                ShowConnection = true;
                return;
            }

            string organizationId = SelectedOrganization.Id;
            Notice = "";

            Status = "ネットワーク一覧を取得しています…";
            IReadOnlyList<string> networkPages = await _dashboard.NetworksAsync(ApiKey, organizationId, token);
            IReadOnlyList<MerakiNetworkRow> networks = MerakiCatalog.ParseNetworks(networkPages);
            // 拠点は名前順に並べる。API が返す順はダッシュボード任せで、
            // 選ぶ欄に何十件も並ぶと目当ての拠点を探せない（2026-08-17 ユーザー指示）
            Replace(NetworkRows, [.. networks.OrderBy(n => n.Name, StringComparer.CurrentCulture)]);
            SelectedNetwork ??= NetworkRows.FirstOrDefault();

            Status = $"ネットワーク {NetworkRows.Count} 件（{DateTime.Now:HH:mm:ss} 時点）。"
                   + "各タブの「取得」で、その中身を取ってきます。";

            if (_dashboard.WasTruncated)
                Notice = "⚠ 件数が多いため途中までしか取得できていません。ダッシュボードで全体を確認してください。";

            Collapse();
        }
        catch (MerakiApiException ex)
        {
            Status = ex.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = $"取得に失敗しました: {ex.Message}";
            CrashLog.Write(ex, "MerakiViewModel.FetchAsync");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;

            // 途中で失敗しても、取れたところまでは保存できるようにする
            RefreshSaveCommands();
        }
    }

    /// <summary>
    /// 機器タブの「取得」。機器の一覧・稼働状況・ファームの版と、MX の LAN アドレス。
    /// <b>ここがいちばん重い</b>（MX のある拠点の数だけ LAN 設定を引く）ので、
    /// 最初の「取得」からは切り離してある。
    /// </summary>
    private async Task FetchDevicesAsync()
    {
        if (SelectedOrganization is not { } organization) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        _dashboard.ResetTruncation();

        try
        {
            CancellationToken token = _cts.Token;

            Status = "機器一覧を取得しています…";

            IReadOnlyList<string> devicePages = await _dashboard.DevicesAsync(ApiKey, organization.Id, token);
            IReadOnlyList<string> statusPages =
                await _dashboard.DeviceStatusesAsync(ApiKey, organization.Id, token);

            // 設定と違う版で動いている機器は firmware に版が入らないので、更新の記録から補う。
            // 記録が引けない組織（権限）もあるので、取れなくても機器一覧は出す
            IReadOnlyDictionary<string, string> versions =
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            try
            {
                Status = "ファームの版を確認しています…";
                versions = MerakiCatalog.RunningVersions(
                    await _dashboard.FirmwareUpgradesAsync(ApiKey, organization.Id, token));
            }
            catch (MerakiApiException)
            {
                // 版が補えないだけ。一覧は「⚠ 設定と違う版」で出る
            }

            _allDevices = MerakiCatalog.JoinDevices(devicePages, statusPages, [.. NetworkRows], versions);
            ShowByNetwork();

            await FillApplianceIpsAsync(token);

            Status = $"機器 {DeviceRows.Count} 台"
                   + (AllNetworks || SelectedNetwork is null ? "" : $"（{SelectedNetwork.Name}）")
                   + $"（{DateTime.Now:HH:mm:ss} 時点）。";

            if (_dashboard.WasTruncated)
                Notice = "⚠ 件数が多いため途中までしか取得できていません。ダッシュボードで全体を確認してください。";
        }
        catch (MerakiApiException ex)
        {
            Status = ex.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = $"取得に失敗しました: {ex.Message}";
            CrashLog.Write(ex, "MerakiViewModel.FetchDevicesAsync");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshSaveCommands();
        }
    }

    /// <summary>
    /// アップリンクタブの「取得」。<b>止まっている機器と未接続の回線は一覧に出さない</b>
    /// （導入時確認は絞る前のものを見る）。
    ///
    /// 機器の一覧をまだ取っていないときは、状態の突き合わせができないぶん
    /// 回線の状態だけで絞る（機器タブを先に取っておくと、より正確になる）。
    /// </summary>
    private async Task FetchUplinksAsync()
    {
        if (SelectedOrganization is not { } organization) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        _dashboard.ResetTruncation();

        try
        {
            Status = "アップリンクの状態を取得しています…";

            IReadOnlyList<MerakiUplinkRow> uplinks = MerakiCatalog.ParseUplinks(
                await _dashboard.UplinksAsync(ApiKey, organization.Id, _cts.Token),
                [.. NetworkRows]);

            // 導入時確認だけは絞る前のものを見る（「取れなかった」と
            // 「切れている」を区別するため）。一覧と CSV には出さない
            _allUplinks = uplinks;

            ShowByNetwork();

            int hidden = uplinks.Count - MerakiCatalog.WithoutOfflineDevices(uplinks, _allDevices).Count;

            Status = $"回線 {UplinkRows.Count} 本"
                   + (hidden > 0 ? $"（つながっていない {hidden} 本は伏せています）" : "")
                   + $"（{DateTime.Now:HH:mm:ss} 時点）。";

            if (DeviceRows.Count == 0)
                Notice = "⚠ 機器タブを先に取得しておくと、止まっている機器の回線をより確かに省けます。";
        }
        catch (MerakiApiException ex)
        {
            Status = ex.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = $"取得に失敗しました: {ex.Message}";
            CrashLog.Write(ex, "MerakiViewModel.FetchUplinksAsync");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshSaveCommands();
        }
    }

    /// <summary>
    /// MX の LAN 側アドレスを埋める。
    ///
    /// <b>MX の LAN IP は組織まとめの応答に入っていない</b>ので、MX のある拠点ごとに
    /// LAN の設定を引く（VLAN を使っていない拠点は singleLan へ落とす）。
    /// 拠点の数だけ呼ぶことになるが、<b>途中で打ち切らない</b>（2026-08-17 ユーザー指示）。
    /// 引けない拠点は空欄のままにする — 分からないものを埋めない。
    /// </summary>
    private async Task FillApplianceIpsAsync(CancellationToken token)
    {
        string[] networks =
        [
            .. DeviceRows.Where(d => d.Model.StartsWith("MX", StringComparison.OrdinalIgnoreCase))
                .Select(d => d.Network)
                .Where(n => n.Length > 0)
                .Distinct(StringComparer.Ordinal),
        ];

        if (networks.Length == 0) return;

        if (networks.Length > 1) Notice = SlowNotice(networks.Length);

        var byNetwork = new Dictionary<string, string>(StringComparer.Ordinal);

        for (int i = 0; i < networks.Length; i++)
        {
            string name = networks[i];

            if (NetworkRows.FirstOrDefault(n => n.Name == name) is not { } network) continue;

            Status = $"MX の LAN アドレスを取得しています… {i + 1}/{networks.Length}（{name}）";

            IReadOnlyList<string> ips = [];

            try
            {
                ips = MerakiCatalog.ApplianceIps(
                    await _dashboard.ApplianceVlansAsync(ApiKey, network.Id, token));
            }
            catch (MerakiApiException)
            {
                // VLAN を使っていない拠点。LAN は 1 つだけなので、そちらを引く
            }

            if (ips.Count == 0)
            {
                try
                {
                    ips = MerakiCatalog.ApplianceIps(
                        await _dashboard.ApplianceSingleLanAsync(ApiKey, network.Id, token));
                }
                catch (MerakiApiException)
                {
                    // その拠点は空欄のまま
                }
            }

            if (ips.Count > 0) byNetwork[name] = string.Join(" / ", ips);
        }

        _allDevices = MerakiCatalog.WithApplianceIps(_allDevices, byNetwork);
        ShowByNetwork();
        ClearSlowNotice();
    }

    /// <summary>
    /// その拠点にあるセグメント。<b>VLAN（無ければ単一 LAN）とスタティックルートの和</b>。
    /// どれも引けない拠点（MX が無い）は空で返す — 取得そのものは止めない。
    /// </summary>
    private async Task<IReadOnlyList<string>> SubnetsOfAsync(
        MerakiNetworkRow network, CancellationToken token)
    {
        var found = new List<string>();

        try
        {
            found.AddRange(MerakiCatalog.ApplianceSubnets(
                await _dashboard.ApplianceVlansAsync(ApiKey, network.Id, token)));
        }
        catch (MerakiApiException)
        {
            // VLAN を使っていない拠点。LAN は 1 つだけなので、そちらを引く
        }

        if (found.Count == 0)
        {
            try
            {
                found.AddRange(MerakiCatalog.ApplianceSubnets(
                    await _dashboard.ApplianceSingleLanAsync(ApiKey, network.Id, token)));
            }
            catch (MerakiApiException)
            {
                // その拠点に MX が無い、というだけ
            }
        }

        try
        {
            found.AddRange(MerakiCatalog.ParseStaticRouteSubnets(
                await _dashboard.StaticRoutesAsync(ApiKey, network.Id, token)));
        }
        catch (MerakiApiException)
        {
            // スタティックルートを持たない拠点の方が多い
        }

        return [.. found.Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    private async Task FetchClientsAsync()
    {
        if (!AllNetworks && SelectedNetwork is null) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        _dashboard.ResetTruncation();

        try
        {
            MerakiTimespan timespan = SelectedTimespan;

            // 全拠点は拠点の数だけ呼び出しが増える。それでも最後まで取る
            // （途中で打ち切ると「その拠点には端末が居ない」と誤読される。2026-08-17 ユーザー指示）
            MerakiNetworkRow[] targets = AllNetworks
                ? [.. NetworkRows]
                : [SelectedNetwork!];

            if (targets.Length > 1)
                Notice = SlowNotice(targets.Length);

            var clients = new List<MerakiClientRow>();

            for (int i = 0; i < targets.Length; i++)
            {
                MerakiNetworkRow network = targets[i];

                Status = targets.Length == 1
                    ? $"「{network.Name}」のクライアントを取得しています…"
                    : $"クライアントを取得しています… {i + 1}/{targets.Length}（{network.Name}）";

                clients.AddRange(MerakiCatalog.ParseClients(
                    await _dashboard.ClientsAsync(ApiKey, network.Id, timespan.Seconds, _cts.Token),
                    network.Name));
            }

            Replace(ClientRows, clients);
            ClearSlowNotice();

            Status = targets.Length == 1
                ? $"「{targets[0].Name}」で {ClientRows.Count} 台（直近 {timespan.Name}）。"
                : $"{targets.Length} 拠点で {ClientRows.Count} 台（直近 {timespan.Name}）。";

            if (_dashboard.WasTruncated)
                Notice = "⚠ 台数が多いため途中までしか取得できていません。期間を短くして試してください。";

        }
        catch (MerakiApiException ex)
        {
            Status = ex.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = $"クライアントを取得できませんでした: {ex.Message}";
            CrashLog.Write(ex, "MerakiViewModel.FetchClientsAsync");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshSaveCommands();
        }
    }

    /// <summary>選択中の組織はできるだけ保つ（取り直しのたびに選び直させない）。</summary>
    private void SyncOrganizations(IReadOnlyList<(string Id, string Name)> organizations)
    {
        string? keep = SelectedOrganization?.Id;

        // 一覧を作り直している間は「選び直した」ことにしない（取得が二重に走る）
        _syncingOrganizations = true;

        try
        {
            Organizations.Clear();
            foreach ((string id, string name) in organizations)
                Organizations.Add(new MerakiOrganizationItem(id, name));

            SelectedOrganization = Organizations.FirstOrDefault(o => o.Id == keep);
        }
        finally
        {
            _syncingOrganizations = false;
        }

        OnPropertyChanged(nameof(NeedsOrganization));
    }

    /// <summary>
    /// 停止している機器のぶんも含めた回線。<b>一覧（<see cref="UplinkRows"/>）は
    /// 停止中のぶんを落としてある</b>ので、導入時確認はこちらを見る。
    /// </summary>
    private IReadOnlyList<MerakiUplinkRow> _allUplinks = [];

    /// <summary>取ってきたもの全部。画面には <see cref="ShowByNetwork"/> が絞って出す。</summary>
    private IReadOnlyList<MerakiDeviceRow> _allDevices = [];

    private IReadOnlyList<MerakiDhcpRow> _allDhcp = [];

    private IReadOnlyList<MerakiAlertRow> _allAlerts = [];

    private static void Replace<T>(ObservableCollection<T> target, IReadOnlyList<T> rows)
    {
        target.Clear();
        foreach (T row in rows)
            target.Add(row);
    }

    /// <summary>
    /// 全拠点ぶんを取る前の断り書き。<b>拠点 1 件につき数回の呼び出し</b>になるので、
    /// 数十拠点あると分単位で待つことになる（打ち切らない代わりに、待つことを先に伝える）。
    /// </summary>
    private static string SlowNotice(int count, string unit = "拠点")
        => $"⚠ 全 {count} {unit}ぶんを取ります。数が多いと数分かかります（「中断」で止められます）。";

    /// <summary>
    /// 待ち時間の断りを引っ込める。<b>取り終わったら消す</b> —
    /// 残したままだと、次に画面を見たときに「まだ取っている」ように読める。
    /// ほかの警告（打ち切りや失敗）は消さない。
    /// </summary>
    private void ClearSlowNotice()
    {
        if (Notice.StartsWith("⚠ 全 ", StringComparison.Ordinal)) Notice = "";
    }

    /// <summary>ロスと遅延をさかのぼる長さ。<b>この API は 5 分までしか受けない。</b></summary>
    private const int QualitySeconds = 300;

    /// <summary>導入時確認で走らせる項目の数（画面に「3/7」と出すため）。</summary>
    private const int CheckSteps = 7;

    /// <summary>
    /// 選んだ拠点の導入時確認。<b>項目ごとに失敗を捕まえて次へ進む</b> —
    /// 1 本が取れないだけで残りが分からなくなる方が、現場では困る。
    /// 取れなかった項目は「確認できず」として理由ごと残す（合格に丸めない）。
    /// </summary>
    private async Task FetchInstallCheckAsync()
    {
        if (SelectedNetwork is not { } network) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();
        _dashboard.ResetTruncation();

        var rows = new List<MerakiCheckRow>();

        try
        {
            CancellationToken token = _cts.Token;
            MerakiTimespan timespan = SelectedTimespan;
            string organizationId = SelectedOrganization?.Id ?? "";

            // 機器も回線も、まだ取っていないことがある（最初の「取得」では取らなくなった）。
            // どちらも組織まとめで 1 回引くだけなので、ここで取りに行く
            if (_allDevices.Count == 0 && organizationId.Length > 0)
            {
                Status = "機器一覧を取得しています…";

                _allDevices = MerakiCatalog.JoinDevices(
                    await _dashboard.DevicesAsync(ApiKey, organizationId, token),
                    await _dashboard.DeviceStatusesAsync(ApiKey, organizationId, token),
                    [.. NetworkRows]);

                ShowByNetwork();
            }

            if (_allUplinks.Count == 0 && organizationId.Length > 0)
            {
                Status = "アップリンクの状態を取得しています…";

                _allUplinks = MerakiCatalog.ParseUplinks(
                    await _dashboard.UplinksAsync(ApiKey, organizationId, token), [.. NetworkRows]);

                ShowByNetwork();
            }

            MerakiDeviceRow[] devices = [.. _allDevices.Where(d => d.Network == network.Name)];
            MerakiDeviceRow[] switches = [.. devices.Where(d => IsModel(d, "MS"))];

            // 冗長構成の待機側（スペア）は確かめない（2026-08-18 ユーザー指示）。
            // 待機側は回線が「待機」で正常なので、そのまま見ると不合格に見える
            string spare = "";

            try
            {
                Status = "冗長構成を確認しています…";
                spare = MerakiCatalog.SpareSerial(
                    await _dashboard.WarmSpareAsync(ApiKey, network.Id, token));
            }
            catch (MerakiApiException)
            {
                // 組んでいない拠点や、答えない版。両方見ることになるだけ
            }

            MerakiDeviceRow[] appliances =
            [
                .. devices.Where(d => IsModel(d, "MX")
                                      && !string.Equals(d.Serial, spare, StringComparison.OrdinalIgnoreCase)),
            ];

            if (spare.Length > 0)
                Notice = "⚠ 冗長構成の待機側（スペア）の MX は確かめていません（待機が正常な状態のため）。";

            // 1. 機器が正常に稼働しているか（取得済みの一覧だけで判る）
            Step(1, "機器の稼働", network);
            rows.Add(MerakiInstallCheck.Devices(devices));

            // 2. 有効にしてある WAN が全部リンクアップしているか
            Step(2, "インターネット回線", network);

            if (appliances.Length == 0)
            {
                rows.Add(MerakiInstallCheck.Unavailable(
                    MerakiInstallCheck.WanName, network.Name, "この拠点に MX がありません"));
            }

            foreach (MerakiDeviceRow appliance in appliances)
            {
                MerakiUplinkRow[] uplinks =
                [
                    .. _allUplinks.Where(u => string.Equals(
                        u.Serial, appliance.Serial, StringComparison.OrdinalIgnoreCase)),
                ];

                IReadOnlyList<string> settings = [];

                try
                {
                    settings = await _dashboard.UplinkSettingsAsync(ApiKey, appliance.Serial, token);
                }
                catch (MerakiApiException)
                {
                    // 設定が取れないぶんは「状態だけで見た」注意になる
                }

                rows.Add(MerakiInstallCheck.Wan(NameOf(appliance), settings, uplinks));
            }

            // 3. 回線のロスと遅延（実測値）
            Step(3, "回線の品質", network);

            if (organizationId.Length == 0 || appliances.Length == 0)
            {
                rows.Add(MerakiInstallCheck.Unavailable(
                    MerakiInstallCheck.QualityName,
                    network.Name,
                    appliances.Length == 0 ? "この拠点に MX がありません" : "組織が選ばれていません"));
            }
            else
            {
                try
                {
                    rows.AddRange(MerakiInstallCheck.Quality(
                        await _dashboard.LossAndLatencyAsync(ApiKey, organizationId, QualitySeconds, token),
                        appliances));
                }
                catch (MerakiApiException ex)
                {
                    rows.Add(MerakiInstallCheck.Unavailable(
                        MerakiInstallCheck.QualityName, network.Name, ex.Message));
                }
            }

            // 4. ポートの速度と全二重（MX は API に無いので目視に回す）
            Step(4, "ポートの速度・全二重", network);

            foreach (MerakiDeviceRow device in switches)
            {
                try
                {
                    rows.Add(MerakiInstallCheck.SwitchPorts(
                        NameOf(device),
                        await _dashboard.SwitchPortStatusesAsync(ApiKey, device.Serial, token)));
                }
                catch (MerakiApiException ex)
                {
                    rows.Add(MerakiInstallCheck.Unavailable(
                        MerakiInstallCheck.PortsName, NameOf(device), ex.Message));
                }
            }

            foreach (MerakiDeviceRow appliance in appliances)
                rows.Add(MerakiInstallCheck.AppliancePortsByPerson(NameOf(appliance), appliance.Model));

            if (switches.Length == 0 && appliances.Length == 0)
            {
                rows.Add(MerakiInstallCheck.Unavailable(
                    MerakiInstallCheck.PortsName, network.Name, "この拠点にスイッチも MX もありません"));
            }

            // 5. トンネルが張れているか
            Step(5, "VPN", network);

            if (organizationId.Length == 0)
            {
                rows.Add(MerakiInstallCheck.Unavailable(
                    MerakiInstallCheck.VpnName, network.Name, "組織が選ばれていません"));
            }
            else
            {
                try
                {
                    rows.AddRange(MerakiInstallCheck.Vpn(
                        await _dashboard.VpnStatusesAsync(ApiKey, organizationId, token), network.Id));
                }
                catch (MerakiApiException ex)
                {
                    rows.Add(MerakiInstallCheck.Unavailable(
                        MerakiInstallCheck.VpnName, network.Name, ex.Message));
                }
            }

            // 6. アドレスが配れているか
            Step(6, "DHCP", network);

            var subnets = new List<MerakiDhcpRow>();

            foreach (MerakiDeviceRow appliance in appliances)
            {
                try
                {
                    subnets.AddRange(MerakiCatalog.ParseDhcp(
                        await _dashboard.DhcpSubnetsAsync(ApiKey, appliance.Serial, token),
                        network.Name,
                        NameOf(appliance)));
                }
                catch (MerakiApiException)
                {
                    // DHCP を持たせていない MX もある。その 1 台で全体を止めない
                }
            }

            rows.AddRange(MerakiInstallCheck.Dhcp(subnets));

            // 7. 端末が実際に繋がっているか
            Step(7, "クライアント", network);

            try
            {
                rows.Add(MerakiInstallCheck.Clients(
                    MerakiCatalog.ParseClients(
                        await _dashboard.ClientsAsync(ApiKey, network.Id, timespan.Seconds, token),
                        network.Name),
                    timespan.Name));
            }
            catch (MerakiApiException ex)
            {
                rows.Add(MerakiInstallCheck.Unavailable(
                    MerakiInstallCheck.ClientsName, network.Name, ex.Message));
            }

            Status = $"{network.Name}（{DateTime.Now:HH:mm:ss} 時点）："
                   + MerakiInstallCheck.Summarize(rows);
        }
        catch (MerakiApiException ex)
        {
            Status = ex.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。ここまでの判定だけ残しています。";
        }
        catch (Exception ex)
        {
            Status = $"導入時確認に失敗しました: {ex.Message}";
            CrashLog.Write(ex, "MerakiViewModel.FetchInstallCheckAsync");
        }
        finally
        {
            // 中断しても、済んだところまでは見せる
            Replace(CheckRows, rows);

            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshSaveCommands();
        }
    }

    /// <summary>いま何をしているかを出す。項目が多いので進み具合が見えないと待てない。</summary>
    private void Step(int step, string what, MerakiNetworkRow network)
        => Status = $"「{network.Name}」を確認しています… {step}/{CheckSteps}（{what}）";

    /// <summary>
    /// 目視の項目に人が合否を付ける。<b>行を差し替える</b>ので CSV にもそのまま載る
    /// （試験タブと同じ扱い）。
    /// </summary>
    private void MarkCheck(MerakiCheckRow? row, CheckVerdict verdict)
    {
        if (row is null) return;

        int at = CheckRows.IndexOf(row);
        if (at < 0) return;

        string mark = verdict == CheckVerdict.Pass ? "目視で確認しました" : "目視で問題を確認しました";

        CheckRows[at] = row with { Verdict = verdict, Detail = $"{mark}（{row.Detail}）" };

        Status = MerakiInstallCheck.Summarize([.. CheckRows]);
    }

    private static bool IsModel(MerakiDeviceRow device, string prefix)
        => device.Model.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && device.Serial.Length > 0;

    /// <summary>名前が無い機器はシリアルで呼ぶ（空欄だと行が誰のものか分からない）。</summary>
    private static string NameOf(MerakiDeviceRow device)
        => device.Name.Length > 0 ? device.Name : device.Serial;

    /// <summary>
    /// 拠点ごとの内訳。<b>拠点の数だけ呼び出しが増える</b>ので、
    /// 取れたところまでで打ち切り、打ち切ったことは画面に出す。
    ///
    /// ついでに機器一覧の LAN IP へ、その拠点のセグメント（クライアントが居るものだけ）を足す。
    /// </summary>
    private async Task FetchSitesAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();
        _dashboard.ResetTruncation();

        try
        {
            CancellationToken token = _cts.Token;

            // 期間は取得の途中で変えられても困るので、始めに固定する
            MerakiTimespan timespan = SelectedTimespan;

            var sites = new List<MerakiSiteRow>();
            var segments = new Dictionary<string, string>(StringComparer.Ordinal);

            MerakiNetworkRow[] targets = [.. NetworkRows];

            if (targets.Length > 1)
                Notice = SlowNotice(targets.Length);

            for (int i = 0; i < targets.Length; i++)
            {
                MerakiNetworkRow network = targets[i];

                Status = $"拠点を数えています… {i + 1}/{targets.Length}（{network.Name}）";

                IReadOnlyList<MerakiClientRow> clients = MerakiCatalog.ParseClients(
                    await _dashboard.ClientsAsync(ApiKey, network.Id, timespan.Seconds, token));

                // 拠点のセグメントは VLAN で切ってあるのが普通で、スタティックルートは
                // その先に別のルータがぶら下がっている拠点にしか無い。
                // ルートだけを見ていたので大半が空欄だった（2026-08-17 ユーザー指摘）
                IReadOnlyList<string> routes = [.. await SubnetsOfAsync(network, token)];

                IReadOnlyList<string> used = MerakiCatalog.SegmentsWithClients(
                    routes, clients.Select(c => c.Ip));

                if (used.Count > 0) segments[network.Name] = string.Join(" / ", used);

                string note = routes.Count > used.Count
                    ? $"クライアントの居ないセグメント {routes.Count - used.Count} 件は省略"
                    : "";

                sites.Add(MerakiCatalog.SiteRow(network, clients.Count, used, note));
            }

            Replace(SiteRows, sites);
            ClearSlowNotice();

            // 機器一覧の LAN IP にセグメントを足す（足すのは MX だけ）
            _allDevices = MerakiCatalog.WithSegments(_allDevices, segments);
            ShowByNetwork();

            Status = $"拠点 {SiteRows.Count} 件を数えました（{timespan.Name}・{DateTime.Now:HH:mm:ss} 時点）。";

        }
        catch (MerakiApiException ex)
        {
            Status = ex.Message;
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = $"取得に失敗しました: {ex.Message}";
            CrashLog.Write(ex, "MerakiViewModel.FetchSitesAsync");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshSaveCommands();
        }
    }

    /// <summary>MX ごとの DHCP 払い出し状況。機器一覧から MX を拾って回る。</summary>
    private async Task FetchDhcpAsync()
    {
        IsBusy = true;
        _cts = new CancellationTokenSource();

        try
        {
            CancellationToken token = _cts.Token;

            MerakiDeviceRow[] appliances =
            [
                .. DeviceRows.Where(d => d.Model.StartsWith("MX", StringComparison.OrdinalIgnoreCase)
                                         && d.Serial.Length > 0),
            ];

            if (appliances.Length > 1)
                Notice = SlowNotice(appliances.Length, "台");

            var rows = new List<MerakiDhcpRow>();

            for (int i = 0; i < appliances.Length; i++)
            {
                MerakiDeviceRow device = appliances[i];

                Status = $"DHCP を確認しています… {i + 1}/{appliances.Length}（{device.Name}）";

                try
                {
                    rows.AddRange(MerakiCatalog.ParseDhcp(
                        await _dashboard.DhcpSubnetsAsync(ApiKey, device.Serial, token),
                        device.Network,
                        device.Name.Length > 0 ? device.Name : device.Serial));
                }
                catch (MerakiApiException)
                {
                    // DHCP を持たせていない MX もある。その 1 台で全体を止めない
                }
            }

            _allDhcp = rows;
            ShowByNetwork();
            ClearSlowNotice();

            Status = appliances.Length == 0
                ? "MX が見つかりませんでした（先に「取得」を押してください）。"
                : $"DHCP {DhcpRows.Count} 件（{DateTime.Now:HH:mm:ss} 時点）。";
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = $"取得に失敗しました: {ex.Message}";
            CrashLog.Write(ex, "MerakiViewModel.FetchDhcpAsync");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshSaveCommands();
        }
    }

    /// <summary>組織のアラート。版によっては持っていないので、その旨を画面に出す。</summary>
    private async Task FetchAlertsAsync()
    {
        if (SelectedOrganization is not { } organization) return;

        IsBusy = true;
        _cts = new CancellationTokenSource();

        try
        {
            Status = "アラートを取得しています…";

            _allAlerts = MerakiCatalog.ParseAlerts(
                await _dashboard.AlertsAsync(ApiKey, organization.Id, _cts.Token));

            ShowByNetwork();

            Status = $"アラート {AlertRows.Count} 件（{DateTime.Now:HH:mm:ss} 時点）。";
        }
        catch (MerakiApiException ex)
        {
            Status = ex.Message + "（この組織ではアラートの一覧を持っていないことがあります）";
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = $"取得に失敗しました: {ex.Message}";
            CrashLog.Write(ex, "MerakiViewModel.FetchAlertsAsync");
        }
        finally
        {
            IsBusy = false;
            _cts?.Dispose();
            _cts = null;
            RefreshSaveCommands();
        }
    }

    private void Save(string kind, CsvTable table)
    {
        if (Services.CsvExport.Save($"meraki-{kind}", table) is { } message)
            Status = message;
    }

    /// <summary>
    /// 種類ごとの表。<b>Excel 保存はここ 1 か所から引く</b>
    /// （CSV は種類ごとのコマンドが持っているが、増やすたびに 2 か所直すのは避ける）。
    /// </summary>
    private CsvTable? TableOf(string? key) => key switch
    {
        "check" => CheckRows.Count > 0 ? MerakiInstallCheck.ToCsv([.. CheckRows]) : null,
        "devices" => DeviceRows.Count > 0 ? MerakiCatalog.ToCsv(DeviceRows) : null,
        "uplinks" => UplinkRows.Count > 0 ? MerakiCatalog.ToCsv(UplinkRows) : null,
        "clients" => ClientRows.Count > 0 ? MerakiCatalog.ToCsv(ClientRows) : null,
        "sites" => SiteRows.Count > 0 ? MerakiCatalog.ToCsv([.. SiteRows]) : null,
        "dhcp" => DhcpRows.Count > 0 ? MerakiCatalog.ToCsv([.. DhcpRows]) : null,
        "alerts" => AlertRows.Count > 0 ? MerakiCatalog.ToCsv([.. AlertRows]) : null,
        _ => null,
    };

    /// <summary>
    /// Excel ブックで保存する（2026-08-18 ユーザー指示）。
    /// <b>絞り込みと見出し行の固定つき</b>で、ACI の保存と同じ形。
    /// </summary>
    private void SaveXlsx(string? key)
    {
        if (TableOf(key) is not { } table) return;

        var dialog = new SaveFileDialog
        {
            FileName = $"{DeviceReport.Sanitize($"meraki-{key}")}-{DateTime.Now:yyyyMMdd-HHmm}.xlsx",
            DefaultExt = "xlsx",
            Filter = "Excel ブック (*.xlsx)|*.xlsx|すべてのファイル (*.*)|*.*",
            AddExtension = true,
        };

        if (dialog.ShowDialog() != true) return;

        try
        {
            using (FileStream file = File.Create(dialog.FileName))
            {
                XlsxWriter.Write(file, table);
            }

            Status = $"{Path.GetFileName(dialog.FileName)} に保存しました（フィルタ設定済み）。";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Status = $"保存できませんでした: {ex.Message}";
        }
    }

    /// <summary>すべて消す。キーも画面の伏せ字欄も残さない。</summary>
    public void Reset()
    {
        _cts?.Cancel();

        Organizations.Clear();
        NetworkRows.Clear();
        DeviceRows.Clear();
        UplinkRows.Clear();
        ClientRows.Clear();
        CheckRows.Clear();
        SiteRows.Clear();
        DhcpRows.Clear();
        AlertRows.Clear();

        // 絞る前の控えも捨てる（キーごと消すので、残しておく理由が無い）
        _allDevices = [];
        _allUplinks = [];
        _allDhcp = [];
        _allAlerts = [];

        SelectedOrganization = null;
        SelectedNetwork = null;
        GlobalIpText = "—";
        ShowConnection = true;
        Status = IdleHint;
        ApiKey = "";
        ApiKeyCleared?.Invoke(this, EventArgs.Empty);

        RefreshSaveCommands();

        Notice = "ダッシュボードの [My profile] で発行した API キーを入れて「取得」を押してください。"
               + "キーは保存しません。";
    }

    private void RefreshSaveCommands()
    {
        SaveDevicesCommand.RaiseCanExecuteChanged();
        SaveUplinksCommand.RaiseCanExecuteChanged();
        SaveClientsCommand.RaiseCanExecuteChanged();
        SaveInstallCheckCommand.RaiseCanExecuteChanged();
        SaveSitesCommand.RaiseCanExecuteChanged();
        SaveDhcpCommand.RaiseCanExecuteChanged();
        SaveAlertsCommand.RaiseCanExecuteChanged();
        SaveXlsxCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _dashboard.Dispose();
    }
}
