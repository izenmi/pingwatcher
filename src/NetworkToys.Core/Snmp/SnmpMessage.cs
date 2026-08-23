namespace NetworkToys.Core.Snmp;

/// <param name="Oid">対象 OID。</param>
/// <param name="Value">値。要求では Null。</param>
public sealed record VarBind(Oid Oid, SnmpValue Value);

/// <summary>SNMP のバージョン。数値はプロトコル上の値。</summary>
public enum SnmpVersion
{
    V1 = 0,
    V2c = 1,
}

/// <param name="Version">バージョン。</param>
/// <param name="Community">コミュニティ文字列。</param>
/// <param name="PduTag">PDU の種別（GetResponse / Trap など）。</param>
/// <param name="RequestId">リクエスト ID（Trap v1 では未使用）。</param>
/// <param name="ErrorStatus">エラー番号（0=正常）。</param>
/// <param name="ErrorIndex">エラーの起きた varbind の位置。</param>
/// <param name="VarBinds">値の並び。</param>
/// <param name="TrapOid">v2c Trap の snmpTrapOID（あれば）。</param>
public sealed record SnmpMessage(
    SnmpVersion Version,
    string Community,
    byte PduTag,
    int RequestId,
    int ErrorStatus,
    int ErrorIndex,
    IReadOnlyList<VarBind> VarBinds,
    Oid? TrapOid = null)
{
    /// <summary>error-status の日本語名。0 は正常。</summary>
    public string ErrorText => ErrorStatus switch
    {
        0 => string.Empty,
        1 => "大きすぎます（tooBig）",
        2 => "その OID はありません（noSuchName）",
        3 => "値が不正です（badValue）",
        4 => "読み取り専用です（readOnly）",
        5 => "一般エラー（genError）",
        6 => "アクセスできません（noAccess）",
        _ => $"エラー {ErrorStatus}",
    };
}

/// <summary>SNMP メッセージの組み立てと解析。</summary>
public static class SnmpCodec
{
    /// <summary>GetRequest / GetNextRequest を組む。</summary>
    public static byte[] BuildGet(SnmpVersion version, string community, int requestId, IReadOnlyList<Oid> oids, bool next)
    {
        // varbind list
        var varbindList = new BerWriter();
        foreach (Oid oid in oids)
        {
            var vb = new BerWriter();
            vb.WriteOid(oid.SubIds);
            vb.WriteNull();
            varbindList.WriteRaw(BerTag.Sequence, InnerOf(vb.WrapAll()));
        }

        // PDU
        var pdu = new BerWriter();
        pdu.WriteInteger(requestId);
        pdu.WriteInteger(0);   // error-status
        pdu.WriteInteger(0);   // error-index
        pdu.WriteRaw(BerTag.Sequence, InnerOf(varbindList.WrapAll()));
        byte[] pduBytes = pdu.WrapAll(next ? BerTag.GetNextRequest : BerTag.GetRequest);

        // message
        var message = new BerWriter();
        message.WriteInteger((long)version);
        message.WriteOctetString(System.Text.Encoding.ASCII.GetBytes(community));
        message.WriteRaw(pduBytes[0], InnerOf(pduBytes));
        return message.WrapAll();
    }

    /// <summary>v2c Trap で必ず先頭に来る 2 つ（RFC3416）。</summary>
    private static readonly Oid SysUpTime = new([1, 3, 6, 1, 2, 1, 1, 3, 0]);
    private static readonly Oid SnmpTrapOid = new([1, 3, 6, 1, 6, 3, 1, 1, 4, 1, 0]);

    /// <summary>
    /// v2c Trap（SNMPv2-Trap-PDU）を組む。<see cref="Parse"/> の逆。
    ///
    /// 受信側しか実装が無いと、組み立ての誤りを実機でしか踏めない。
    /// 自己診断が自分の Trap 受信へ自分で投げて往復を確かめるのに使う
    /// （偽の Cisco 機器・偽の STUN と同じ手）。
    /// </summary>
    /// <param name="upTimeHundredths">sysUpTime（1/100 秒単位）。</param>
    /// <param name="trapOid">snmpTrapOID の値（linkDown など）。</param>
    /// <param name="extra">添える varbind。先頭 2 つは固定なのでここには入れない。</param>
    public static byte[] BuildTrapV2(
        string community,
        int requestId,
        uint upTimeHundredths,
        Oid trapOid,
        IReadOnlyList<VarBind>? extra = null)
    {
        ArgumentNullException.ThrowIfNull(trapOid);

        var varbindList = new BerWriter();

        // 先頭 2 つは sysUpTime → snmpTrapOID の順で固定。
        // 並べ替えると受け側が TrapOid を拾えない（Parse は 2 つ目を見る）
        AddVarBind(varbindList, SysUpTime, BerTag.TimeTicks, UnsignedBytes(upTimeHundredths));
        AddVarBind(varbindList, SnmpTrapOid, BerTag.ObjectIdentifier, OidBytes(trapOid));

        foreach (VarBind vb in extra ?? [])
            AddVarBind(varbindList, vb.Oid, vb.Value.Tag, vb.Value.Raw);

        var pdu = new BerWriter();
        pdu.WriteInteger(requestId);
        pdu.WriteInteger(0);   // error-status
        pdu.WriteInteger(0);   // error-index
        pdu.WriteRaw(BerTag.Sequence, InnerOf(varbindList.WrapAll()));
        byte[] pduBytes = pdu.WrapAll(BerTag.TrapV2);

        var message = new BerWriter();
        message.WriteInteger((long)SnmpVersion.V2c);
        message.WriteOctetString(System.Text.Encoding.ASCII.GetBytes(community));
        message.WriteRaw(pduBytes[0], InnerOf(pduBytes));
        return message.WrapAll();
    }

    private static void AddVarBind(BerWriter list, Oid oid, byte valueTag, ReadOnlySpan<byte> value)
    {
        var one = new BerWriter();
        one.WriteOid(oid.SubIds);
        one.WriteRaw(valueTag, value);
        list.WriteRaw(BerTag.Sequence, InnerOf(one.WrapAll()));
    }

    /// <summary>OID を BER の中身（V 部分）にする。</summary>
    private static byte[] OidBytes(Oid oid)
    {
        var writer = new BerWriter();
        writer.WriteOid(oid.SubIds);
        return InnerOf(writer.ToArray());
    }

    /// <summary>
    /// TimeTicks のような符号なしの値を最小バイトのビッグエンディアンにする。
    /// <see cref="BerWriter.WriteInteger"/> は符号付き（タグも INTEGER）なので使えない。
    /// </summary>
    private static byte[] UnsignedBytes(uint value)
    {
        byte[] all = [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

        int start = 0;
        while (start < 3 && all[start] == 0) start++;

        // 最上位ビットが立っていると符号付きとして読まれうるので 0x00 を足す
        return (all[start] & 0x80) != 0 ? [0, .. all[start..]] : all[start..];
    }

    /// <summary>受信したメッセージ（GetResponse / Trap）を解析する。読めなければ null。</summary>
    public static SnmpMessage? Parse(ReadOnlySpan<byte> data)
    {
        var outer = new BerReader(data);
        if (!outer.TryReadElement(out BerElement message) || message.Tag != BerTag.Sequence)
            return null;

        var body = new BerReader(message.Content);

        if (!body.TryReadElement(out BerElement versionEl) || versionEl.Tag != BerTag.Integer) return null;
        if (!BerReader.TryReadInteger(versionEl.Content, out long version)) return null;

        if (!body.TryReadElement(out BerElement communityEl) || communityEl.Tag != BerTag.OctetString) return null;
        string community = System.Text.Encoding.ASCII.GetString(communityEl.Content);

        if (!body.TryReadElement(out BerElement pdu)) return null;

        return pdu.Tag == BerTag.TrapV1
            ? ParseTrapV1((SnmpVersion)version, community, pdu.Content)
            : ParseStandardPdu((SnmpVersion)version, community, pdu.Tag, pdu.Content);
    }

    private static SnmpMessage? ParseStandardPdu(SnmpVersion version, string community, byte pduTag, ReadOnlySpan<byte> content)
    {
        var reader = new BerReader(content);

        if (!ReadInt(ref reader, out int requestId)) return null;
        if (!ReadInt(ref reader, out int errorStatus)) return null;
        if (!ReadInt(ref reader, out int errorIndex)) return null;

        if (!reader.TryReadElement(out BerElement vbList) || vbList.Tag != BerTag.Sequence) return null;

        List<VarBind> binds = ReadVarBinds(vbList.Content);

        // v2c Trap は先頭 2 つが sysUpTime と snmpTrapOID
        Oid? trapOid = pduTag == BerTag.TrapV2 && binds.Count >= 2 && binds[1].Value.Tag == BerTag.ObjectIdentifier
            ? BerReader.TryReadOid(binds[1].Value.Raw, out uint[] subs) ? new Oid(subs) : null
            : null;

        return new SnmpMessage(version, community, pduTag, requestId, errorStatus, errorIndex, binds, trapOid);
    }

    private static SnmpMessage? ParseTrapV1(SnmpVersion version, string community, ReadOnlySpan<byte> content)
    {
        var reader = new BerReader(content);

        // enterprise OID / agent-addr / generic-trap / specific-trap / time-stamp / varbinds
        if (!reader.TryReadElement(out BerElement enterprise) || enterprise.Tag != BerTag.ObjectIdentifier) return null;
        if (!reader.TryReadElement(out _)) return null;   // agent-addr
        if (!ReadInt(ref reader, out int generic)) return null;
        if (!ReadInt(ref reader, out int specific)) return null;
        if (!reader.TryReadElement(out _)) return null;   // time-stamp
        if (!reader.TryReadElement(out BerElement vbList) || vbList.Tag != BerTag.Sequence) return null;

        List<VarBind> binds = ReadVarBinds(vbList.Content);
        Oid? enterpriseOid = BerReader.TryReadOid(enterprise.Content, out uint[] subs) ? new Oid(subs) : null;

        // generic/specific を error-status/index の枠に載せて表示に使う
        return new SnmpMessage(version, community, BerTag.TrapV1, 0, generic, specific, binds, enterpriseOid);
    }

    private static List<VarBind> ReadVarBinds(ReadOnlySpan<byte> content)
    {
        var binds = new List<VarBind>();
        var reader = new BerReader(content);

        while (reader.TryReadElement(out BerElement vb))
        {
            if (vb.Tag != BerTag.Sequence) continue;

            var inner = new BerReader(vb.Content);
            if (!inner.TryReadElement(out BerElement oidEl) || oidEl.Tag != BerTag.ObjectIdentifier) continue;
            if (!inner.TryReadElement(out BerElement valueEl)) continue;
            if (!BerReader.TryReadOid(oidEl.Content, out uint[] subs)) continue;

            binds.Add(new VarBind(new Oid(subs), SnmpValue.From(valueEl.Tag, valueEl.Content)));
        }

        return binds;
    }

    private static bool ReadInt(ref BerReader reader, out int value)
    {
        value = 0;
        if (!reader.TryReadElement(out BerElement el) || el.Tag != BerTag.Integer) return false;
        if (!BerReader.TryReadInteger(el.Content, out long v)) return false;
        value = unchecked((int)v);
        return true;
    }

    /// <summary>TLV から V 部分だけを取り出す（WrapAll した結果を WriteRaw で入れ直すため）。</summary>
    private static byte[] InnerOf(byte[] tlv)
    {
        var reader = new BerReader(tlv);
        return reader.TryReadElement(out BerElement el) ? el.Content.ToArray() : [];
    }
}
