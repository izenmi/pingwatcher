using System.IO;
using System.Net;
using System.Net.Sockets;
using NetworkToys.Core.Ftp;
using NetworkToys.Core.Tftp;

namespace NetworkToys.App.Services;

/// <summary>
/// 使い捨ての TFTP サーバ（RFC1350 + blksize/tsize/timeout 交渉）。UDP/69。
/// 機器の <c>copy running-config tftp:</c> を受ける。外部ライブラリなし。
///
/// TFTP は認証が無い。公開は 1 フォルダに閉じ込め（<see cref="FtpVirtualPath"/>）、
/// 使うときだけ立てる運用。RRQ/WRQ ごとに専用ソケットで転送セッションを回す。
/// </summary>
internal sealed class TftpServer : IFileServer
{
    private const int MaxSessions = 8;

    private readonly string _rootDirectory;
    private UdpClient? _listener;
    private CancellationTokenSource? _cts;
    private readonly SemaphoreSlim _sessions = new(MaxSessions, MaxSessions);

    public TftpServer(string rootDirectory) => _rootDirectory = rootDirectory;

    public event Action<FileServerEvent>? Event;

    public bool IsRunning => _listener is not null;

    public void Start(int port)
    {
        if (IsRunning) return;

        Directory.CreateDirectory(_rootDirectory);

        // SocketException（ポート使用中など）はそのまま呼び出し側へ
        var listener = new UdpClient(new IPEndPoint(IPAddress.Any, port));

        _listener = listener;
        _cts = new CancellationTokenSource();
        _ = ReceiveLoopAsync(listener, _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Dispose();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
    }

    private async Task ReceiveLoopAsync(UdpClient listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            UdpReceiveResult received;
            try
            {
                received = await listener.ReceiveAsync(token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            TftpRequest? request = TftpPacket.ReadRequest(received.Buffer);
            TftpOpcode opcode = TftpPacket.OpcodeOf(received.Buffer);

            if (request is null)
                continue;   // RRQ/WRQ 以外が最初に来ることはない。無視

            // 溢れたら断る（相手には ERROR を返す）。
            // 0 ミリ秒待ちは決して待たないので、トークンは渡さない — 渡すと停止と競った
            // ときに OperationCanceledException がこのループの外へ出る。ループ自身が
            // 捨てられたタスク（Start の `_ =`）なので誰も観測せず、crash.log を汚す
            if (!_sessions.Wait(0))
            {
                await SendBusyAsync(received.RemoteEndPoint).ConfigureAwait(false);
                continue;
            }

            // 枠の返却はセッション側の finally に任せる。ContinueWith で外から返すと、
            // その継続もまた捨てられたタスクになり、例外の行き場が無くなる
            bool isRead = opcode == TftpOpcode.ReadRequest;
            _ = RunSessionAsync(request.Value, isRead, received.RemoteEndPoint, token);
        }
    }

    private async Task RunSessionAsync(TftpRequest request, bool isRead, IPEndPoint remote, CancellationToken token)
    {
        // ソケットを作るところも try の中に入れる。捨てられたタスクで走るので、
        // ここで投げると誰も観測できないうえ、枠も返らないまま減り続ける
        try
        {
            // 転送は要求元とは別の（エフェメラル）ソケットで行う（TFTP の作法）
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            socket.Connect(remote);

            var path = new FtpVirtualPath(_rootDirectory);
            string? local = path.Resolve(request.Filename);

            string remoteText = remote.Address.ToString();

            if (local is null)
            {
                await SendErrorAsync(socket, TftpError.AccessViolation, "パスが不正です。").ConfigureAwait(false);
                return;
            }

            if (isRead)
                await SendFileAsync(socket, local, request, remoteText, token).ConfigureAwait(false);
            else
                await ReceiveFileAsync(socket, local, request, remoteText, token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException)
        {
            // 相手が消えた・書けないなどは想定内
        }
        catch (Exception ex)
        {
            CrashLog.Write(ex, "TftpServer.Session");
        }
        finally
        {
            _sessions.Release();   // 上限の枠を返す（取ったのは ReceiveLoopAsync）
        }
    }

    private async Task SendFileAsync(UdpClient socket, string local, TftpRequest request, string remote, CancellationToken token)
    {
        if (!File.Exists(local))
        {
            await SendErrorAsync(socket, TftpError.FileNotFound, "ありません。").ConfigureAwait(false);
            return;
        }

        byte[] content = await File.ReadAllBytesAsync(local, token).ConfigureAwait(false);
        TftpTransferOptions options = TftpNegotiation.Negotiate(request.Options, content.Length);
        int blockSize = options.BlockSize;

        // オプション交渉ありなら OACK→ACK(block 0) から始める
        if (options.Accepted.Count > 0)
        {
            if (!await SendAndWaitAckAsync(socket, TftpPacket.OptionAck(options.Accepted), 0, options.TimeoutSeconds, token).ConfigureAwait(false))
                return;
        }

        ushort block = 1;
        for (int offset = 0; ; offset += blockSize, block++)
        {
            int length = Math.Min(blockSize, content.Length - offset);
            byte[] data = TftpPacket.Data(block, content.AsSpan(offset, length));

            if (!await SendAndWaitAckAsync(socket, data, block, options.TimeoutSeconds, token).ConfigureAwait(false))
                return;

            // 最後のブロック（ブロックサイズ未満）で終わり
            if (length < blockSize)
                break;
        }

        Raise(remote, $"↓ 取得 {request.Filename}（{content.Length:N0} バイト）");
    }

    private async Task ReceiveFileAsync(UdpClient socket, string local, TftpRequest request, string remote, CancellationToken token)
    {
        TftpTransferOptions options = TftpNegotiation.Negotiate(request.Options, transferSize: -1);
        int blockSize = options.BlockSize;

        // WRQ は OACK か ACK(block 0) で「送ってよい」と返す
        byte[] ready = options.Accepted.Count > 0 ? TftpPacket.OptionAck(options.Accepted) : TftpPacket.Ack(0);

        await using var file = File.Create(local);
        long total = 0;
        ushort expected = 1;

        await SendAsync(socket, ready).ConfigureAwait(false);

        while (true)
        {
            byte[]? packet = await ReceiveWithTimeoutAsync(socket, options.TimeoutSeconds, token).ConfigureAwait(false);
            if (packet is null)
            {
                await SendAsync(socket, ready).ConfigureAwait(false);   // 再送を促す
                continue;
            }

            ushort? block = TftpPacket.ReadDataBlock(packet);
            if (block is null) continue;

            if (block.Value == expected)
            {
                // Span は await を跨げないので、先にバイト配列にしておく
                byte[] payload = TftpPacket.ReadDataPayload(packet).ToArray();
                await file.WriteAsync(payload, token).ConfigureAwait(false);
                total += payload.Length;

                await SendAsync(socket, TftpPacket.Ack(block.Value)).ConfigureAwait(false);
                expected++;

                if (payload.Length < blockSize)
                    break;   // 最後のブロック
            }
            else
            {
                // 重複や取りこぼし。直前の ACK をもう一度返して整合させる
                await SendAsync(socket, TftpPacket.Ack((ushort)(expected - 1))).ConfigureAwait(false);
            }
        }

        Raise(remote, $"↑ 保存 {request.Filename}（{total:N0} バイト）");
    }

    /// <summary>データを送り、期待するブロックの ACK が来るまで再送（3 回）。成功なら true。</summary>
    private static async Task<bool> SendAndWaitAckAsync(UdpClient socket, byte[] packet, ushort expectAck, int timeout, CancellationToken token)
    {
        for (int attempt = 0; attempt < 4; attempt++)
        {
            await SendAsync(socket, packet).ConfigureAwait(false);

            byte[]? reply = await ReceiveWithTimeoutAsync(socket, timeout, token).ConfigureAwait(false);
            if (reply is null) continue;   // タイムアウト → 再送

            if (TftpPacket.ReadAckBlock(reply) == expectAck)
                return true;
        }

        return false;
    }

    private static async Task<byte[]?> ReceiveWithTimeoutAsync(UdpClient socket, int timeoutSeconds, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, timeoutSeconds)));

        try
        {
            UdpReceiveResult result = await socket.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            return result.Buffer;
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            return null;   // このセッションのタイムアウト
        }
    }

    private static Task SendAsync(UdpClient socket, byte[] packet)
        => socket.SendAsync(packet, packet.Length);

    private static Task SendErrorAsync(UdpClient socket, TftpError code, string message)
    {
        byte[] error = TftpPacket.Error(code, message);
        return socket.SendAsync(error, error.Length);
    }

    private async Task SendBusyAsync(IPEndPoint remote)
    {
        try
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            socket.Connect(remote);
            await SendErrorAsync(socket, TftpError.NotDefined, "混んでいます。").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
    }

    private void Raise(string remote, string text)
        => Event?.Invoke(new FileServerEvent(DateTime.Now, remote, text));

    // _sessions は破棄しない（理由は FtpServer.Dispose と同じ）。Stop() は待受を畳むだけで、
    // 進行中の転送はその後も動いており、終わったときに枠を返しに来る
    public void Dispose() => Stop();
}
