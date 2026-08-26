using NetworkToys.Core.Fabric;
using Xunit;

namespace NetworkToys.Core.Tests;

/// <summary>
/// テナントの設定の書き出し。
///
/// <b>ここが崩れると「設定を何も変えていないのに差分だらけ」になる。</b>
/// APIC は子の並び順を約束しておらず、稼働値も混じりうるので、
/// 「同じ設定なら同じ文字になる」ことを固定しておく。
/// </summary>
public class AciConfigExportTests
{
    /// <summary>子の並びだけを入れ替えた同じ設定。</summary>
    private const string OneOrder = """
        {"totalCount":"1","imdata":[
          {"fvTenant":{"attributes":{"dn":"uni/tn-Prod","name":"Prod","descr":"本番"},
           "children":[
             {"fvBD":{"attributes":{"dn":"uni/tn-Prod/BD-Web","name":"BD-Web","arpFlood":"no"}}},
             {"fvAp":{"attributes":{"dn":"uni/tn-Prod/ap-Shop","name":"Shop"},
              "children":[
                {"fvAEPg":{"attributes":{"dn":"uni/tn-Prod/ap-Shop/epg-Web","name":"Web","prio":"unspecified"}}},
                {"fvAEPg":{"attributes":{"dn":"uni/tn-Prod/ap-Shop/epg-Db","name":"Db","prio":"unspecified"}}}]}}]}}
        ]}
        """;

    private const string OtherOrder = """
        {"totalCount":"1","imdata":[
          {"fvTenant":{"attributes":{"name":"Prod","descr":"本番","dn":"uni/tn-Prod"},
           "children":[
             {"fvAp":{"attributes":{"name":"Shop","dn":"uni/tn-Prod/ap-Shop"},
              "children":[
                {"fvAEPg":{"attributes":{"name":"Db","prio":"unspecified","dn":"uni/tn-Prod/ap-Shop/epg-Db"}}},
                {"fvAEPg":{"attributes":{"name":"Web","prio":"unspecified","dn":"uni/tn-Prod/ap-Shop/epg-Web"}}}]}},
             {"fvBD":{"attributes":{"arpFlood":"no","name":"BD-Web","dn":"uni/tn-Prod/BD-Web"}}}]}}
        ]}
        """;

    [Fact]
    public void 並び順が違っても同じ設定なら同じ文字になる()
    {
        string one = AciConfigExport.Render("", AciMoReader.Parse(OneOrder));
        string other = AciConfigExport.Render("", AciMoReader.Parse(OtherOrder));

        Assert.Equal(one, other);
        Assert.NotEqual("", one.Trim());
    }

    /// <summary>
    /// dn も name も持たない子（fvRsPathAtt は tDn が識別子）の並びだけを入れ替えた同じ設定。
    /// テナントの枝を丸ごと取ると、子は dn を持たずに返るのが普通で、
    /// クラス → dn → 名前の鍵ではここが同点になり、APIC の返した順が漏れていた。
    /// </summary>
    [Fact]
    public void 識別子が_name_でない子の並びが入れ替わっても同じ文字になる()
    {
        const string one = """
            {"totalCount":"1","imdata":[
              {"fvAEPg":{"attributes":{"name":"Web"},
               "children":[
                 {"fvRsPathAtt":{"attributes":{"tDn":"topology/pod-1/paths-101/pathep-[eth1/1]","encap":"vlan-10"}}},
                 {"fvRsPathAtt":{"attributes":{"tDn":"topology/pod-1/paths-102/pathep-[eth1/2]","encap":"vlan-10"}}},
                 {"fvSubnet":{"attributes":{"ip":"10.0.0.1/24"}}},
                 {"fvSubnet":{"attributes":{"ip":"10.0.1.1/24"}}}]}}
            ]}
            """;

        const string other = """
            {"totalCount":"1","imdata":[
              {"fvAEPg":{"attributes":{"name":"Web"},
               "children":[
                 {"fvSubnet":{"attributes":{"ip":"10.0.1.1/24"}}},
                 {"fvRsPathAtt":{"attributes":{"encap":"vlan-10","tDn":"topology/pod-1/paths-102/pathep-[eth1/2]"}}},
                 {"fvSubnet":{"attributes":{"ip":"10.0.0.1/24"}}},
                 {"fvRsPathAtt":{"attributes":{"encap":"vlan-10","tDn":"topology/pod-1/paths-101/pathep-[eth1/1]"}}}]}}
            ]}
            """;

        Assert.Equal(
            AciConfigExport.Render("", AciMoReader.Parse(one)),
            AciConfigExport.Render("", AciMoReader.Parse(other)));
    }

    [Fact]
    public void 親子の入れ子が字下げで出る()
    {
        string text = AciConfigExport.Render("", AciMoReader.Parse(OneOrder));
        string[] lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("fvTenant uni/tn-Prod", lines[0].TrimEnd());

        // 属性は見出しの 1 段内側、子はさらに内側
        Assert.Contains(lines, l => l.TrimEnd() == "  descr = 本番");
        Assert.Contains(lines, l => l.TrimEnd() == "  fvAp uni/tn-Prod/ap-Shop");
        Assert.Contains(lines, l => l.TrimEnd() == "    fvAEPg uni/tn-Prod/ap-Shop/epg-Db");

        // dn は見出しに出したので、属性としては二度書かない
        Assert.DoesNotContain(lines, l => l.Trim().StartsWith("dn = ", StringComparison.Ordinal));
    }

    [Fact]
    public void 状態の属性は落とす()
    {
        const string json = """
            {"totalCount":"1","imdata":[
              {"fvTenant":{"attributes":{
                "dn":"uni/tn-Prod","name":"Prod","descr":"",
                "modTs":"2026-08-17T09:00:00.000+09:00","uid":"15374","lcOwn":"local",
                "status":"","childAction":"","rn":"tn-Prod"}}}
            ]}
            """;

        string text = AciConfigExport.Render("", AciMoReader.Parse(json));

        foreach (string dropped in AciConfigExport.VolatileAttributes)
            Assert.DoesNotContain($"{dropped} = ", text, StringComparison.Ordinal);

        Assert.Contains("name = Prod", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 何を書き出したかを_1_行目に添える()
    {
        string text = AciConfigExport.Render("apic.example / テナント Prod", AciMoReader.Parse(OneOrder));

        Assert.StartsWith("# apic.example / テナント Prod", text, StringComparison.Ordinal);
    }

    [Fact]
    public void 読めない応答でも例外にしない()
        => Assert.Equal("", AciConfigExport.Render("", AciMoReader.Parse("これは JSON ではない")));

    [Fact]
    public void 書き出しは設定だけを求める()
    {
        string path = AciCatalog.TenantExportPath("Prod");

        Assert.StartsWith("/api/mo/uni/tn-Prod.json", path, StringComparison.Ordinal);

        // これが抜けると稼働値まで混ざり、設定を変えていなくても差分になる
        Assert.Contains("rsp-prop-include=config-only", path, StringComparison.Ordinal);
        Assert.Contains("rsp-subtree=full", path, StringComparison.Ordinal);

        // 名前に記号が入っても壊さない
        Assert.Contains("tn-A%2FB", AciCatalog.TenantExportPath("A/B"), StringComparison.Ordinal);
    }
}
