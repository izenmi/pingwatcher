using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Text;
using Microsoft.Win32;
using NetworkToys.App.Mvvm;
using NetworkToys.App.Services;
using NetworkToys.Core.Addressing;
using NetworkToys.Core.Work;

namespace NetworkToys.App.ViewModels;

/// <summary>スキャンで見つかった 1 台。</summary>
public sealed class ScanRowViewModel
{
    internal ScanRowViewModel(ScanHit hit)
    {
        Address = hit.Address.ToString();
        HostName = hit.HostName ?? string.Empty;
        Mac = hit.Mac ?? string.Empty;
        Vendor = hit.Vendor ?? string.Empty;
        Rtt = TargetRowViewModel.FormatMilliseconds(hit.RttMs);
        Ports = hit.OpenPorts.Count > 0 ? string.Join(", ", hit.OpenPorts) : string.Empty;

        // 宛先リストへ移すときの 1 行。備考には分かる範囲で素性を入れる
        string note = HostName.Length > 0 ? HostName
            : Vendor.Length > 0 ? Vendor
            : string.Empty;

        TargetLine = note.Length > 0 ? $"{Address} {note}" : Address;
    }

    public string Address { get; }
    public string HostName { get; }
    public string Mac { get; }
    public string Vendor { get; }
    public string Rtt { get; }
    public string Ports { get; }

    internal string TargetLine { get; }
}

/// <summary>
/// IP スキャン画面。範囲を指定してサブネットを掃き、応答したホストの素性を調べる。
/// </summary>
public sealed class ScanViewModel : ObservableObject
{
    private const int Limit = IpRangeParser.DefaultLimit;

    private readonly ScanService _service = new();
    private readonly Action<IEnumerable<string>> _addToTargets;

    /// <summary>起動時のスキャン範囲。クリアで戻すために覚えておく。</summary>
    private readonly string _defaultRange;

    private string _rangeText;
    private string _preview = string.Empty;
    /// <summary>何もしていないときの案内。<b>初期値と Reset の戻し先を空文字にしない</b>
    /// （何をすれば動くかが見えない画面になる。2026-08-20 の UI 改善）。</summary>
    private const string IdleHint = "範囲を入れて「スキャン」を押すと、応答した機器が一覧に出ます。";

    private string _status = IdleHint;
    private bool _isBusy;
    // 逆引きは引けない相手の方が多く、待ちのぶんスキャンが延びるので既定は OFF
    private bool _resolveNames;
    private bool _scanPorts;
    private double _progress;
    private CancellationTokenSource? _cts;

    public ScanViewModel(string? defaultRange, Action<IEnumerable<string>> addToTargets)
    {
        _addToTargets = addToTargets;
        _rangeText = _defaultRange = defaultRange ?? "192.168.1.0/24";

        ScanCommand = new RelayCommand(() => _ = RunAsync(), () => !IsBusy);
        CancelCommand = new RelayCommand(() => _cts?.Cancel(), () => IsBusy);
        AddToTargetsCommand = new RelayCommand(AddToTargets, () => Results.Count > 0);
        SaveCommand = new RelayCommand(Save, () => Results.Count > 0);

        UpdatePreview();
    }

    public ObservableCollection<ScanRowViewModel> Results { get; } = [];

    /// <summary>
    /// 結果の絞り込み。数百台の現場で目当ての 1 台を探すための欄。
    /// 行を collection から抜かずに <see cref="System.ComponentModel.ICollectionView"/> で
    /// 隠す（Ping の絞り込みと同じやり方）。
    /// </summary>
    public string Filter
    {
        get => _filter;
        set
        {
            if (!SetProperty(ref _filter, value)) return;

            System.ComponentModel.ICollectionView view =
                System.Windows.Data.CollectionViewSource.GetDefaultView(Results);

            // 絞り込みが空のときは述語ごと外す（毎行の判定を走らせない）
            view.Filter = _filter.Length == 0 ? null : o => Matches((ScanRowViewModel)o);
        }
    }

    private string _filter = string.Empty;

    private bool Matches(ScanRowViewModel row)
        => row.Address.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        || row.HostName.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        || row.Mac.Contains(_filter, StringComparison.OrdinalIgnoreCase)
        || row.Vendor.Contains(_filter, StringComparison.OrdinalIgnoreCase);

    public RelayCommand ScanCommand { get; }
    public RelayCommand CancelCommand { get; }
    public RelayCommand AddToTargetsCommand { get; }
    public RelayCommand SaveCommand { get; }

    public string RangeText
    {
        get => _rangeText;
        set
        {
            if (SetProperty(ref _rangeText, value))
                UpdatePreview();
        }
    }

    /// <summary>実行前に何件になるか見せる。/16 を誤って指定する事故を防ぐ。</summary>
    public string Preview
    {
        get => _preview;
        private set => SetProperty(ref _preview, value);
    }

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public bool ResolveNames
    {
        get => _resolveNames;
        set => SetProperty(ref _resolveNames, value);
    }

    public bool ScanPorts
    {
        get => _scanPorts;
        set => SetProperty(ref _scanPorts, value);
    }

    public double Progress
    {
        get => _progress;
        private set => SetProperty(ref _progress, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value)) return;

            ScanCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    private void UpdatePreview()
    {
        ScanRangeResult parsed = ScanRangeParser.Parse(RangeText, Limit);

        if (parsed.HasErrors)
        {
            Preview = $"{parsed.Count:N0} ホスト ／ {parsed.Errors[0]}";
            return;
        }

        // ping 1 回あたり最大 800ms、128 並列としたときの目安
        int seconds = Math.Max(1, (int)Math.Ceiling(parsed.Count / 128.0 * 0.9));
        Preview = parsed.Count == 0
            ? "対象がありません"
            : $"{parsed.Count:N0} ホスト ／ 推定 {seconds} 秒";
    }

    private async Task RunAsync()
    {
        ScanRangeResult parsed = ScanRangeParser.Parse(RangeText, Limit);

        if (parsed.Count == 0)
        {
            Status = parsed.HasErrors ? parsed.Errors[0] : "スキャンする範囲を指定してください。";
            return;
        }

        IsBusy = true;
        Progress = 0;
        Results.Clear();
        AddToTargetsCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();

        _cts = new CancellationTokenSource();
        CancellationToken token = _cts.Token;

        var options = new ScanOptions
        {
            ResolveNames = ResolveNames,
            ScanPorts = ScanPorts,
        };

        // IProgress は UI スレッドの文脈を捕まえるので、そのままプロパティを触れる
        var progress = new Progress<ScanProgress>(report =>
        {
            Progress = report.Total == 0 ? 0 : report.Done * 100.0 / report.Total;
            Status = $"{report.Phase} … {report.Done:N0} / {report.Total:N0}（{report.Found} 台）";
        });

        try
        {
            IReadOnlyList<ScanHit> hits = await _service.ScanAsync(parsed.Addresses, options, progress, token);

            foreach (ScanHit hit in hits)
                Results.Add(new ScanRowViewModel(hit));

            Progress = 100;
            Status = $"{parsed.Count:N0} 件中 {hits.Count} 台が応答しました。";
        }
        catch (OperationCanceledException)
        {
            Status = "中断しました。";
        }
        catch (Exception ex)
        {
            Status = $"スキャンに失敗しました: {ex.Message}";
            CrashLog.Write(ex, "ScanViewModel.RunAsync");
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            IsBusy = false;
            AddToTargetsCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
        }
    }

    private void Save()
    {
        var table = new CsvTable(
            ["IP", "RTT", "ホスト名", "MAC", "ベンダー", "開放ポート"],
            [.. Results.Select(r => new[] { r.Address, r.Rtt, r.HostName, r.Mac, r.Vendor, r.Ports })]);

        if (Services.CsvExport.Save("scan", table) is { } message)
            Status = message;
    }

    private void AddToTargets()
    {
        if (Results.Count == 0) return;

        _addToTargets(Results.Select(r => r.TargetLine));
        Status = $"{Results.Count} 台を宛先リストへ書き出しました。「宛先」タブで内容を確かめて反映してください。";
    }

    /// <summary>結果と入力を起動時の状態へ戻す。走っていれば止める。</summary>
    public void Reset()
    {
        _cts?.Cancel();

        Results.Clear();
        AddToTargetsCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();

        RangeText = _defaultRange;
        ResolveNames = false;
        ScanPorts = false;
        Progress = 0;
        Status = IdleHint;
    }

}
