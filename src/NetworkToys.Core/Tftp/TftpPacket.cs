using System.Text;

namespace NetworkToys.Core.Tftp;

/// <summary>TFTP のオペコード（RFC1350 + RFC2347 の OACK）。</summary>
public enum TftpOpcode
{
    Unknown = 0,
    ReadRequest = 1,   // RRQ
    WriteRequest = 2,  // WRQ
    Data = 3,
    Ack = 4,
    Error = 5,
    OptionAck = 6,     // OACK
}

/// <summary>TFTP のエラーコード。</summary>
public enum TftpError
{
    NotDefined = 0,
    FileNotFound = 1,
    AccessViolation = 2,
    DiskFull = 3,
    IllegalOperation = 4,
    UnknownTransferId = 5,
    FileAlreadyExists = 6,
    NoSuchUser = 7,
}

/// <param name="Filename">対象ファイル名。RRQ/WRQ のみ。</param>
/// <param name="Mode">"netascii" / "octet"。小文字化して持つ。</param>
/// <param name="Options">blksize などの交渉オプション（キーは小文字）。</param>
public readonly record struct TftpRequest(string Filename, string Mode, IReadOnlyDictionary<string, string> Options);

/// <summary>
/// TFTP パケットの読み書き。UDP の 1 データグラム = 1 パケット。
/// オペコードとブロック番号はビッグエンディアンの 2 バイト。
/// パースは例外を投げない（壊れたデータグラムは Unknown を返す）。
/// </summary>
public static class TftpPacket
{
    public static TftpOpcode OpcodeOf(ReadOnlySpan<byte> packet)
    {
        if (packet.Length < 2) return TftpOpcode.Unknown;

        int code = (packet[0] << 8) | packet[1];
        return code is >= 1 and <= 6 ? (TftpOpcode)code : TftpOpcode.Unknown;
    }

    /// <summary>RRQ/WRQ を読む。読めなければ null。</summary>
    public static TftpRequest? ReadRequest(ReadOnlySpan<byte> packet)
    {
        TftpOpcode opcode = OpcodeOf(packet);
        if (opcode is not (TftpOpcode.ReadRequest or TftpOpcode.WriteRequest))
            return null;

        // filename NUL mode NUL (opt NUL val NUL)*
        var fields = new List<string>();
        int start = 2;
        for (int i = 2; i < packet.Length; i++)
        {
            if (packet[i] != 0) continue;

            fields.Add(Encoding.ASCII.GetString(packet[start..i]));
            start = i + 1;
        }

        if (fields.Count < 2) return null;

        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 2; i + 1 < fields.Count; i += 2)
            options[fields[i].ToLowerInvariant()] = fields[i + 1];

        return new TftpRequest(fields[0], fields[1].ToLowerInvariant(), options);
    }

    /// <summary>DATA の block 番号を読む。DATA でなければ null。</summary>
    public static ushort? ReadDataBlock(ReadOnlySpan<byte> packet)
        => OpcodeOf(packet) == TftpOpcode.Data && packet.Length >= 4
            ? (ushort)((packet[2] << 8) | packet[3])
            : null;

    /// <summary>DATA の中身（4 バイトのヘッダより後ろ）。</summary>
    public static ReadOnlySpan<byte> ReadDataPayload(ReadOnlySpan<byte> packet)
        => packet.Length >= 4 ? packet[4..] : [];

    /// <summary>ACK の block 番号を読む。ACK でなければ null。</summary>
    public static ushort? ReadAckBlock(ReadOnlySpan<byte> packet)
        => OpcodeOf(packet) == TftpOpcode.Ack && packet.Length >= 4
            ? (ushort)((packet[2] << 8) | packet[3])
            : null;

    public static byte[] Data(ushort block, ReadOnlySpan<byte> payload)
    {
        var buffer = new byte[4 + payload.Length];
        WriteHeader(buffer, TftpOpcode.Data, block);
        payload.CopyTo(buffer.AsSpan(4));
        return buffer;
    }

    public static byte[] Ack(ushort block)
    {
        var buffer = new byte[4];
        WriteHeader(buffer, TftpOpcode.Ack, block);
        return buffer;
    }

    public static byte[] Error(TftpError code, string message)
    {
        byte[] text = Encoding.ASCII.GetBytes(message);
        var buffer = new byte[4 + text.Length + 1];
        buffer[0] = 0;
        buffer[1] = (byte)TftpOpcode.Error;
        buffer[2] = 0;
        buffer[3] = (byte)code;
        text.CopyTo(buffer.AsSpan(4));
        buffer[^1] = 0;
        return buffer;
    }

    /// <summary>
    /// RRQ/WRQ を組み立てる（クライアント側）。<see cref="ReadRequest"/> の逆。
    ///
    /// 本体には要らないが、自己診断が自分のサーバへ自分で投げて往復を確かめるのに使う
    /// （偽の Cisco 機器・偽の STUN と同じ手）。読み取り側だけを持っていると、
    /// 組み立ての誤りを実機でしか踏めない。
    /// </summary>
    /// <param name="opcode"><see cref="TftpOpcode.ReadRequest"/> か <see cref="TftpOpcode.WriteRequest"/>。</param>
    public static byte[] Request(
        TftpOpcode opcode,
        string filename,
        string mode = "octet",
        IReadOnlyDictionary<string, string>? options = null)
    {
        if (opcode is not (TftpOpcode.ReadRequest or TftpOpcode.WriteRequest))
            throw new ArgumentOutOfRangeException(nameof(opcode), opcode, "RRQ か WRQ でなければならない");

        var body = new List<byte> { 0, (byte)opcode };

        void AddField(string text)
        {
            body.AddRange(Encoding.ASCII.GetBytes(text));
            body.Add(0);
        }

        AddField(filename);
        AddField(mode);

        foreach ((string key, string value) in options ?? new Dictionary<string, string>())
        {
            AddField(key);
            AddField(value);
        }

        return [.. body];
    }

    /// <summary>OACK（交渉したオプションを返す）。</summary>
    public static byte[] OptionAck(IReadOnlyDictionary<string, string> options)
    {
        var body = new List<byte> { 0, (byte)TftpOpcode.OptionAck };

        foreach ((string key, string value) in options)
        {
            body.AddRange(Encoding.ASCII.GetBytes(key));
            body.Add(0);
            body.AddRange(Encoding.ASCII.GetBytes(value));
            body.Add(0);
        }

        return [.. body];
    }

    private static void WriteHeader(Span<byte> buffer, TftpOpcode opcode, ushort block)
    {
        buffer[0] = 0;
        buffer[1] = (byte)opcode;
        buffer[2] = (byte)(block >> 8);
        buffer[3] = (byte)(block & 0xFF);
    }
}
