using System.IO;
using System.Text;

namespace NetworkToys.App;

/// <summary>
/// 未処理例外をファイルに残す。
///
/// 開発環境で exe を実行できないため、落ちたときに手掛かりが残らないと原因に辿り着けない。
/// CI では実行ディレクトリに出るので、ワークフローから内容を表示できる。
/// </summary>
internal static class CrashLog
{
    /// <summary>これを超えたら追記をやめて書き直す。例外の嵐で無限に育てない。</summary>
    private const long MaxBytes = 1024 * 1024;

    private static readonly object Gate = new();

    private static int _count;
    private static string _lastSource = string.Empty;

    /// <summary>
    /// これまでに記録した件数。<b>自己診断がこれを見て失敗を判定する。</b>
    ///
    /// プロセスを落とさない不具合（握り潰した例外・捨てたタスクからの
    /// UnobservedTaskException）は終了コードに一切現れないので、
    /// ファイルの有無だけを CI で見ていた頃は緑のまま見逃していた。
    /// ファイルを書けなかった場合も数える — 書けないことを理由に検出漏れにしない。
    /// </summary>
    public static int Count => Volatile.Read(ref _count);

    /// <summary>直近に記録した出どころ。どの経路で出たかを検査の失敗文面に添える。</summary>
    public static string LastSource
    {
        get { lock (Gate) return _lastSource; }
    }

    // Environment.CurrentDirectory は使わない。保存ダイアログを一度使うと
    // CWD が選択先フォルダに変わり、以降のクラッシュログが行方不明になる
    public static string Path => System.IO.Path.Combine(AppData.DirectoryForLogs, "crash.log");

    public static void Write(Exception? exception, string source)
    {
        lock (Gate)
        {
            _count++;
            _lastSource = source;
        }

        var text = new StringBuilder();
        text.AppendLine($"[{DateTime.Now:yyyy/MM/dd HH:mm:ss}] {source}");
        text.AppendLine($"  ランタイム: {Environment.Version} / OS: {Environment.OSVersion.VersionString}");
        text.AppendLine($"  対話セッション: {Environment.UserInteractive}");

        for (Exception? ex = exception; ex is not null; ex = ex.InnerException)
        {
            text.AppendLine($"  {ex.GetType().FullName}: {ex.Message}");
            if (!string.IsNullOrEmpty(ex.StackTrace))
                text.AppendLine(ex.StackTrace);
        }

        if (exception is null)
            text.AppendLine("  例外オブジェクトを取得できませんでした。");

        text.AppendLine();

        string rendered = text.ToString();
        Console.Error.WriteLine(rendered);

        try
        {
            lock (Gate)
            {
                string path = Path;
                var info = new FileInfo(path);

                if (info.Exists && info.Length > MaxBytes)
                    File.WriteAllText(path, rendered, Encoding.UTF8);
                else
                    File.AppendAllText(path, rendered, Encoding.UTF8);
            }
        }
        catch
        {
            // ログを書けないこと自体で落とさない
        }
    }
}
