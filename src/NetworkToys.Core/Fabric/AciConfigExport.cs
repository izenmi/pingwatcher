using System.Text;

namespace NetworkToys.Core.Fabric;

/// <summary>
/// テナントの設定を、作業前後で見比べられる形の文字にする。
///
/// <b>JSON のまま差分にしない。</b>APIC は子の並び順を約束しておらず、同じ設定でも
/// 順番が入れ替わるだけで差分だらけになる。だから<b>並べ替えて字下げした行</b>に直す
/// （行の差分にそのまま乗る形。差分比較タブの「そのまま比較」で読む）。
///
/// 取ってくる側は必ず <c>rsp-prop-include=config-only</c> を付けること
/// （<see cref="AciCatalog.TenantExportPath"/>）。付けないと稼働値まで混ざり、
/// <b>設定を何も変えていなくても差分になる</b>（show ip route の経過時間と同じ問題）。
/// </summary>
public static class AciConfigExport
{
    /// <summary>
    /// 落とす属性。設定ではなく<b>そのときの状態</b>なので、比べると毎回差分になる。
    ///
    /// <c>config-only</c> を付ければ大半は返らないが、版によっては混じる。
    /// <b>実機で余計な差分が出たら、ここへ 1 行足せば直る。</b>
    /// </summary>
    public static string[] VolatileAttributes =>
        ["modTs", "uid", "lcOwn", "childAction", "status", "extMngdBy", "userdom", "rn"];

    /// <summary>
    /// 見比べるための文字にする。<b>同じ設定なら、並び順が違っても同じ文字になる</b>。
    ///
    /// 1 行目に何を書き出したかを添える（後から見て、どのテナントの控えか分かるように）。
    /// </summary>
    public static string Render(string label, IEnumerable<AciMo> mos)
    {
        var text = new StringBuilder();

        if (label.Length > 0) text.AppendLine($"# {label}");

        foreach (string block in OrderedBlocks(mos, 0))
            text.Append(block);

        return text.ToString();
    }

    /// <summary>
    /// 兄弟を並べ替えて、それぞれの枝を描いた文字にする。
    ///
    /// <b>並べ替えの鍵は「その枝を描いた文字そのもの」。</b>かつてはクラス → dn → 名前で
    /// 並べていたが、テナント配下の子は dn を持たず、name を持たないクラスも多い
    /// （ポートへの紐付け fvRsPathAtt は tDn、サブネット fvSubnet は ip が識別子）。
    /// 同クラスの兄弟が全部同じ鍵になると、安定ソートが APIC の返した順をそのまま残し、
    /// <b>作業の前後で並びが入れ替わっただけの差分が出る</b>（2026-08-23 に実機で報告）。
    /// 描いた文字で並べれば、識別子がどの属性かをクラスごとに知らなくても順序が決まる。
    /// 先頭は「クラス dn」の見出し行なので、読み手に見える並びも従来とほぼ同じ。
    /// </summary>
    private static IEnumerable<string> OrderedBlocks(IEnumerable<AciMo> mos, int depth)
        => mos.Select(m => RenderOne(m, depth)).OrderBy(b => b, StringComparer.Ordinal);

    private static string RenderOne(AciMo mo, int depth)
    {
        var text = new StringBuilder();
        string indent = new(' ', depth * 2);

        // 見出しは「クラス dn」。dn があれば、どのオブジェクトの話か 1 行で分かる
        text.Append(indent).Append(mo.ClassName);

        if (mo["dn"] is { Length: > 0 } dn) text.Append(' ').Append(dn);

        text.AppendLine();

        foreach (KeyValuePair<string, string> attribute in Order(mo.Attributes))
        {
            // dn は見出しに出した。二度書かない
            if (attribute.Key == "dn") continue;

            text.Append(indent).Append("  ").Append(attribute.Key)
                .Append(" = ").AppendLine(attribute.Value);
        }

        foreach (string block in OrderedBlocks(mo.Children, depth + 1))
            text.Append(block);

        return text.ToString();
    }

    private static IEnumerable<KeyValuePair<string, string>> Order(IReadOnlyDictionary<string, string> attributes)
        => attributes.Where(a => !VolatileAttributes.Contains(a.Key, StringComparer.Ordinal))
                     .OrderBy(a => a.Key, StringComparer.Ordinal);
}
