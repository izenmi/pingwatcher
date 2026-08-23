using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using NetworkToys.Core.Ftp;

namespace NetworkToys.App.Services;

/// <summary>
/// 使い捨ての FTP サーバ。現場で機器の config バックアップ
/// （<c>copy running-config ftp:</c>）を受けるための最小実装。RFC959 のうち
/// 機器クライアントが使うコマンドだけを実装する。外部ライブラリは使わない。
///
/// <b>公開範囲は 1 フォルダに閉じ込める</b>（<see cref="FtpVirtualPath"/> が守る）。
/// 平文なので、使うときだけ手動で立て、使い終わったら止める運用。
/// </summary>
internal sealed class FtpServer : IFileServer
{
    private readonly string _rootDirectory;
    private readonly string? _user;
    private readonly string? _password;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sessions = new(5, 5);

    /// <param name="rootDirectory">公開するフォルダ。</param>
    /// <param name="user">固定ユーザー名。null なら誰でも（anonymous）。</param>
    /// <param name="password">固定パスワード。null なら何でも通す。</param>
    public FtpServer(string rootDirectory, string? user = null, string? password = null)
    {
        _rootDirectory = rootDirectory;
        _user = string.IsNullOrEmpty(user) ? null : user;
        _password = password;
    }

    /// <summary>出来事の通知。UI スレッドで受けたいので、購読側で marshaling する。</summary>
    public event Action<FileServerEvent>? Event;

    public bool IsRunning => _listener is not null;

    /// <summary>待受を始める。ポートが使えなければ SocketException を投げる（呼び出し側で案内）。</summary>
    public void Start(int port)
    {
        if (IsRunning) return;

        Directory.CreateDirectory(_rootDirectory);

        var listener = new TcpListener(IPAddress.Any, port);
        listener.Start();

        _listener = listener;
        _cts = new CancellationTokenSource();

        _ = AcceptLoopAsync(listener, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;   // 停止した
            }

            // 同時接続の上限。溢れたら丁寧に断って閉じる。
            // 0 ミリ秒待ちは決して待たないので、トークンは渡さない — 渡すと停止と競った
            // ときに OperationCanceledException がこのループの外へ出る。ループ自身が
            // 捨てられたタスク（Start の `_ =`）なので誰も観測せず、crash.log を汚す
            if (!_sessions.Wait(0))
            {
                try
                {
                    await using var stream = client.GetStream();
                    await WriteLineAsync(stream, "421 接続数が上限に達しています。", token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException) { }
                client.Dispose();
                continue;
            }

            // 枠の返却はセッション側の finally に任せる。ContinueWith で外から返すと、
            // その継続もまた捨てられたタスクになり、例外の行き場が無くなる
            _ = RunSessionAsync(client, token);
        }
    }

    private async Task RunSessionAsync(TcpClient client, CancellationToken token)
    {
        // 組み立ても try の中に入れる。捨てられたタスクで走るので、ここで投げると
        // 誰も観測できないうえ、枠も返らないまま減り続ける
        FtpSession? session = null;
        try
        {
            session = new FtpSession(client, _rootDirectory, _user, _password, Raise);
            await session.RunAsync(token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException
                                         or OperationCanceledException)
        {
            // 相手が切った・こちらが止めた、などは想定内。
            // OperationCanceledException（TaskCanceledException を含む）は「停止した」
            // というだけで異常ではない — 応答を書いている最中に Stop() が来ると
            // WriteLineAsync がこれを投げる。想定内に入れていなかったため、
            // 待受を止めるたびに crash.log が汚れていた（CI で実測。TftpServer は
            // 元から入れてあったので、あちらと同じ形に揃える）
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "FtpServer.Session");
        }
        finally
        {
            // 組み立てに失敗したときは接続だけ閉じる（session が面倒を見られない）
            if (session is not null) session.Dispose(); else client.Dispose();
            _sessions.Release();   // 上限の枠を返す（取ったのは AcceptLoopAsync）
        }
    }

    private void Raise(string remote, string text)
        => Event?.Invoke(new FileServerEvent(DateTime.Now, remote, text));

    internal static async Task WriteLineAsync(Stream stream, string line, CancellationToken token)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(line + "\r\n");
        await stream.WriteAsync(bytes, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    // _sessions は破棄しない。Stop() は待受を畳むだけで、進行中のセッションはその後も
    // 動いており、終わったときに枠を返しに来る。ここで破棄すると、その Release が
    // ObjectDisposedException になり、セッションを回しているタスクは捨てられているので
    // 誰も観測せず crash.log を汚す（転送のあと停止するたびに毎回起きていた）。
    // SemaphoreSlim は AvailableWaitHandle を使わない限り解放すべき資源を持たない。
    public void Dispose() => Stop();
}

/// <summary>制御接続 1 本 = クライアント 1 台分の状態機械。</summary>
internal sealed class FtpSession : IDisposable
{
    private readonly TcpClient _control;
    private readonly FtpVirtualPath _path;
    private readonly string? _user;
    private readonly string? _password;
    private readonly Action<string, string> _log;
    private readonly string _remote;

    private bool _authenticated;
    private string _pendingUser = string.Empty;

    // RNFR で覚えた元の場所。RNTO で 1 回だけ使う
    private string? _renameFrom;

    // データ接続の準備。PASV はこちらが待受、PORT は相手が指定
    private TcpListener? _passiveListener;
    private IPEndPoint? _activeEndpoint;

    public FtpSession(TcpClient control, string root, string? user, string? password, Action<string, string> log)
    {
        _control = control;
        _path = new FtpVirtualPath(root);
        _user = user;
        _password = password;
        _log = log;
        _remote = (control.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
    }

    public async Task RunAsync(CancellationToken token)
    {
        _log(_remote, "接続しました");

        NetworkStream control = _control.GetStream();
        await FtpServer.WriteLineAsync(control, "220 NetworkToys FTP", token).ConfigureAwait(false);

        using var reader = new StreamReader(control, Encoding.UTF8, false, 1024, leaveOpen: true);

        while (!token.IsCancellationRequested)
        {
            string? line = await reader.ReadLineAsync(token).ConfigureAwait(false);
            if (line is null) break;

            FtpCommand command = FtpCommand.Parse(line);
            if (command.Verb.Length == 0) continue;

            bool keepGoing = await HandleAsync(control, command, token).ConfigureAwait(false);
            if (!keepGoing) break;
        }

        _log(_remote, "切断しました");
    }

    private async Task<bool> HandleAsync(NetworkStream control, FtpCommand command, CancellationToken token)
    {
        Task Reply(string line) => FtpServer.WriteLineAsync(control, line, token);

        // 認証前に通すのは USER/PASS/QUIT だけ
        if (!_authenticated && command.Verb is not ("USER" or "PASS" or "QUIT"))
        {
            await Reply("530 先にログインしてください。").ConfigureAwait(false);
            return true;
        }

        switch (command.Verb)
        {
            case "USER":
                _pendingUser = command.Argument;
                await Reply("331 パスワードを入力してください。").ConfigureAwait(false);
                return true;

            case "PASS":
                _authenticated = Authenticate(_pendingUser, command.Argument);
                await Reply(_authenticated ? "230 ログインしました。" : "530 ログインできません。").ConfigureAwait(false);
                return true;

            case "SYST": await Reply("215 UNIX Type: L8").ConfigureAwait(false); return true;
            case "FEAT": await Reply("211 End").ConfigureAwait(false); return true;
            case "NOOP": await Reply("200 OK").ConfigureAwait(false); return true;
            case "TYPE": await Reply("200 OK").ConfigureAwait(false); return true;   // 常にバイナリで扱う
            case "PWD":
            case "XPWD":
                await Reply($"257 {FtpFormats.QuotedPath(_path.CurrentDirectory)}").ConfigureAwait(false);
                return true;

            case "CWD":
            case "XCWD":
                await Reply(_path.ChangeDirectory(command.Argument, out _)
                    ? "250 移動しました。"
                    : "550 移動できません。").ConfigureAwait(false);
                return true;

            case "CDUP":
            case "XCUP":
                _path.GoToParent();
                await Reply("250 移動しました。").ConfigureAwait(false);
                return true;

            case "PASV": await EnterPassiveAsync(control, token).ConfigureAwait(false); return true;
            case "PORT": await EnterActiveAsync(command.Argument, Reply).ConfigureAwait(false); return true;

            case "LIST":
            case "NLST":
                await SendListingAsync(control, command.Verb == "NLST", token).ConfigureAwait(false);
                return true;

            case "RETR": await SendFileAsync(control, command.Argument, token).ConfigureAwait(false); return true;
            case "STOR": await ReceiveFileAsync(control, command.Argument, token).ConfigureAwait(false); return true;

            case "SIZE":
                {
                    string? local = _path.Resolve(command.Argument);
                    await Reply(local is not null && File.Exists(local)
                        ? string.Create(CultureInfo.InvariantCulture, $"213 {new FileInfo(local).Length}")
                        : "550 ありません。").ConfigureAwait(false);
                    return true;
                }

            case "DELE":
                {
                    string? local = _path.Resolve(command.Argument);
                    if (local is not null && File.Exists(local))
                    {
                        File.Delete(local);
                        _log(_remote, $"削除 {command.Argument}");
                        await Reply("250 削除しました。").ConfigureAwait(false);
                    }
                    else
                    {
                        await Reply("550 ありません。").ConfigureAwait(false);
                    }
                    return true;
                }

            case "MKD":
            case "XMKD":
                {
                    string? local = _path.Resolve(command.Argument);
                    if (local is null || Directory.Exists(local) || File.Exists(local))
                    {
                        await Reply("550 作れません。").ConfigureAwait(false);
                        return true;
                    }

                    Directory.CreateDirectory(local);
                    _log(_remote, $"フォルダ作成 {command.Argument}");
                    await Reply($"257 {FtpFormats.QuotedPath(command.Argument)} を作りました。").ConfigureAwait(false);
                    return true;
                }

            case "RMD":
            case "XRMD":
                {
                    string? local = _path.Resolve(command.Argument);
                    if (local is null || !Directory.Exists(local))
                    {
                        await Reply("550 ありません。").ConfigureAwait(false);
                        return true;
                    }

                    // 中身ごとは消さない。空でなければ断る（取り消せない操作なので慎重に）
                    if (Directory.EnumerateFileSystemEntries(local).Any())
                    {
                        await Reply("550 空ではありません。").ConfigureAwait(false);
                        return true;
                    }

                    Directory.Delete(local);
                    _log(_remote, $"フォルダ削除 {command.Argument}");
                    await Reply("250 削除しました。").ConfigureAwait(false);
                    return true;
                }

            case "RNFR":
                {
                    // 名前の変更は 2 段階。ここでは覚えるだけで、動かすのは RNTO
                    string? local = _path.Resolve(command.Argument);
                    _renameFrom = local is not null && (File.Exists(local) || Directory.Exists(local)) ? local : null;

                    await Reply(_renameFrom is null ? "550 ありません。" : "350 新しい名前をどうぞ。").ConfigureAwait(false);
                    return true;
                }

            case "RNTO":
                {
                    string? from = _renameFrom;
                    _renameFrom = null;   // 1 回きり。続けて RNTO を投げても効かない

                    string? to = _path.Resolve(command.Argument);

                    if (from is null || to is null || File.Exists(to) || Directory.Exists(to))
                    {
                        await Reply("550 変更できません。").ConfigureAwait(false);
                        return true;
                    }

                    if (Directory.Exists(from)) Directory.Move(from, to);
                    else File.Move(from, to);

                    _log(_remote, $"名前変更 {command.Argument}");
                    await Reply("250 変更しました。").ConfigureAwait(false);
                    return true;
                }

            case "QUIT":
                await Reply("221 さようなら。").ConfigureAwait(false);
                return false;

            default:
                await Reply($"502 未対応のコマンドです: {command.Verb}").ConfigureAwait(false);
                return true;
        }
    }

    private bool Authenticate(string user, string password)
    {
        if (_user is null) return true;   // anonymous 運用
        if (!string.Equals(user, _user, StringComparison.Ordinal)) return false;
        return _password is null || string.Equals(password, _password, StringComparison.Ordinal);
    }

    private async Task EnterPassiveAsync(NetworkStream control, CancellationToken token)
    {
        ClosePassive();

        IPAddress local = ((IPEndPoint)_control.Client.LocalEndPoint!).Address;
        var listener = new TcpListener(local, 0);   // エフェメラルポート
        listener.Start();
        _passiveListener = listener;
        _activeEndpoint = null;

        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        await FtpServer.WriteLineAsync(control, FtpFormats.PassiveModeReply(local, port), token).ConfigureAwait(false);
    }

    private async Task EnterActiveAsync(string argument, Func<string, Task> reply)
    {
        ClosePassive();

        if (FtpFormats.ParsePort(argument) is { } endpoint)
        {
            _activeEndpoint = new IPEndPoint(endpoint.Address, endpoint.Port);
            await reply("200 OK").ConfigureAwait(false);
        }
        else
        {
            await reply("501 PORT の引数が不正です。").ConfigureAwait(false);
        }
    }

    /// <summary>データ接続を開く。PASV なら待受を受理、PORT なら相手へ繋ぐ。</summary>
    private async Task<TcpClient?> OpenDataAsync(CancellationToken token)
    {
        if (_passiveListener is { } listener)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            return await listener.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
        }

        if (_activeEndpoint is { } endpoint)
        {
            var client = new TcpClient();
            await client.ConnectAsync(endpoint.Address, endpoint.Port, token).ConfigureAwait(false);
            return client;
        }

        return null;
    }

    private async Task SendListingAsync(NetworkStream control, bool namesOnly, CancellationToken token)
    {
        await FtpServer.WriteLineAsync(control, "150 一覧を送ります。", token).ConfigureAwait(false);

        TcpClient? data = null;
        try
        {
            data = await OpenDataAsync(token).ConfigureAwait(false);
            if (data is null)
            {
                await FtpServer.WriteLineAsync(control, "425 データ接続がありません。", token).ConfigureAwait(false);
                return;
            }

            string localDir = _path.Resolve(".") ?? _path.Resolve("/")!;
            var builder = new StringBuilder();

            foreach (string dir in Directory.EnumerateDirectories(localDir))
            {
                var info = new DirectoryInfo(dir);
                builder.Append(namesOnly ? info.Name : FtpFormats.ListLine(
                    new FtpListEntry(info.Name, true, 0, info.LastWriteTime))).Append("\r\n");
            }

            foreach (string file in Directory.EnumerateFiles(localDir))
            {
                var info = new FileInfo(file);
                builder.Append(namesOnly ? info.Name : FtpFormats.ListLine(
                    new FtpListEntry(info.Name, false, info.Length, info.LastWriteTime))).Append("\r\n");
            }

            byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());
            await data.GetStream().WriteAsync(bytes, token).ConfigureAwait(false);
        }
        finally
        {
            data?.Dispose();
            ClosePassive();
        }

        await FtpServer.WriteLineAsync(control, "226 一覧を送りました。", token).ConfigureAwait(false);
    }

    private async Task SendFileAsync(NetworkStream control, string argument, CancellationToken token)
    {
        string? local = _path.Resolve(argument);
        if (local is null || !File.Exists(local))
        {
            await FtpServer.WriteLineAsync(control, "550 ありません。", token).ConfigureAwait(false);
            ClosePassive();
            return;
        }

        await FtpServer.WriteLineAsync(control, "150 送信します。", token).ConfigureAwait(false);

        TcpClient? data = null;
        try
        {
            data = await OpenDataAsync(token).ConfigureAwait(false);
            if (data is null)
            {
                await FtpServer.WriteLineAsync(control, "425 データ接続がありません。", token).ConfigureAwait(false);
                return;
            }

            await using var file = File.OpenRead(local);
            await file.CopyToAsync(data.GetStream(), token).ConfigureAwait(false);

            _log(_remote, $"↓ 取得 {argument}（{new FileInfo(local).Length:N0} バイト）");
        }
        finally
        {
            data?.Dispose();
            ClosePassive();
        }

        await FtpServer.WriteLineAsync(control, "226 送信しました。", token).ConfigureAwait(false);
    }

    private async Task ReceiveFileAsync(NetworkStream control, string argument, CancellationToken token)
    {
        string? local = _path.Resolve(argument);
        if (local is null)
        {
            await FtpServer.WriteLineAsync(control, "553 その名前には保存できません。", token).ConfigureAwait(false);
            ClosePassive();
            return;
        }

        await FtpServer.WriteLineAsync(control, "150 受信します。", token).ConfigureAwait(false);

        TcpClient? data = null;
        try
        {
            data = await OpenDataAsync(token).ConfigureAwait(false);
            if (data is null)
            {
                await FtpServer.WriteLineAsync(control, "425 データ接続がありません。", token).ConfigureAwait(false);
                return;
            }

            await using (var file = File.Create(local))
                await data.GetStream().CopyToAsync(file, token).ConfigureAwait(false);

            _log(_remote, $"↑ 保存 {argument}（{new FileInfo(local).Length:N0} バイト）");
        }
        finally
        {
            data?.Dispose();
            ClosePassive();
        }

        await FtpServer.WriteLineAsync(control, "226 受信しました。", token).ConfigureAwait(false);
    }

    private void ClosePassive()
    {
        _passiveListener?.Stop();
        _passiveListener = null;
    }

    public void Dispose()
    {
        ClosePassive();
        _control.Dispose();
    }
}
