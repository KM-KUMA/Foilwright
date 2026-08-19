// Foilwright.Core — CassetteStatus(38 バイトの状態応答)を人が読める日本語へ変換する
// デコーダ(DOMAIN §7.2 のプリンタ状態表示に対応)。
//
// 実測で確定していることだけを扱う(DOMAIN §11.4 / §13.7.5 / §13.8.3)。
//   - 状態バイトはビットフラグの集合。0xc9 = 0xc0(エラー) | 0x09(印刷実行中) の
//     重ね合わせに実測の裏付けがある。
//   - 確定しているエラー種別は「状態バイト 0xc0 かつ rec[9] 先頭バイト 0x80」の
//     1 件のみ。ただし **種別までは識別できない**(DOMAIN §11.4 の訂正)。
    //
    //     2026-08-04 に「0x80 = ペーパーフィードメカニズム異常」と結論したが、
    //     2026-08-08 に同一機体で純正が「キャリッジメカニズムに異常」を表示して
    //     いる最中の応答が **1 バイトも違わなかった**ため撤回した。§13.8.3 の
    //     とおり機構エラーは 8 種類あるが、05 01 の応答はそれらを区別しない。
    //     **特定の機構名を出してはならない** — 利用者が誤った箇所を点検する。
//   - エラー中はカセット情報が更新されない(§11.4)。呼び出し側が鵜呑みにしないよう
//     IsError / CassetteInfoMayBeStale を明示する。
//
// 語彙は純正ドライバのエラー文言カタログ(DOMAIN §13.8.3)に揃える。独自の言い回しは作らない。

using System.Collections.Generic;

namespace Foilwright.Core;

/// <summary>状態バイトのビットフラグ(DOMAIN §11.4「状態バイトはビットフラグ + カセット消失の観測」)。</summary>
[Flags]
public enum PrinterStatusFlags : byte
{
    None = 0x00,

    /// <summary>エラー発生中。状態バイトの 0xc0 ビット(実測: 0xc0 単独 / 0xc9 = 0xc0|0x09)。</summary>
    Error = 0xC0,

    /// <summary>印刷実行中。状態バイトの 0x09 ビット(実測: 0x09 単独 / 0xc9 = 0xc0|0x09)。</summary>
    Printing = 0x09,
}

/// <summary>カセットスロット 1 件の日本語表示情報。</summary>
public sealed record CassetteSlotInfo(int SlotNumber, byte Barcode, string Name);

/// <summary>CassetteStatus から得られる、人が読める状態の全体像。</summary>
public sealed record PrinterStatusReport(
    PrinterStatusFlags Flags,
    string StatusSummary,
    string? ErrorDetail,
    string HeadCassetteName,
    IReadOnlyList<CassetteSlotInfo> HolderSlots)
{
    /// <summary>エラー発生中か(DOMAIN §11.4)。</summary>
    public bool IsError => Flags.HasFlag(PrinterStatusFlags.Error);

    /// <summary>印刷実行中か(DOMAIN §11.4)。</summary>
    public bool IsPrinting => Flags.HasFlag(PrinterStatusFlags.Printing);

    /// <summary>true の間はカセット情報(HeadCassetteName / HolderSlots)が現物と
    /// 一致しない可能性がある。エラー中は状態応答が更新されないため(DOMAIN §11.4)。</summary>
    public bool CassetteInfoMayBeStale => IsError;
}

/// <summary>バーコード番号 → カセット名の対応表(DOMAIN §6.5 / §13.7.5。1 箇所に集約)。
/// カセットバーコード ≠ 色選択コード(printer_code)。パレット定義(palette/*.yaml)には
/// 無い情報のため、ここに持つ。</summary>
public static class CassetteCatalog
{
    // §6.5(mddata.h 由来)と §13.7.5(aldv63ln.dll 文字列リソース、ppmtomd barCode enum − 4 と対応)
    // を突き合わせた表。8-11 番の国内名は両節で表記が食い違う(§6.5: 金/マゼンタ/シアン/銀、
    // §13.7.5: ゴールド/レッド/ブルー/シルバー)。このデコーダは実装依頼で名指しされた
    // §13.7.5(aldv63ln.dll の国内名文字列)を採用する【推測に近い選択。実測による裏付けなし】。
    private static readonly IReadOnlyDictionary<byte, string> Names = new Dictionary<byte, string>
    {
        [0x00] = "紙用ブラック",
        [0x01] = "紙用イエロー",
        [0x02] = "紙用マゼンタ",
        [0x03] = "紙用シアン",
        [0x04] = "紙用マルチカラー",
        [0x05] = "フラッシュゴールド",
        [0x06] = "フラッシュシルバー",
        [0x07] = "ベースドホワイト",
        [0x08] = "メタリックゴールド",
        [0x09] = "メタリックレッド",
        [0x0A] = "メタリックブルー",
        [0x0B] = "メタリックシルバー",
        [0x0D] = "OHP用イエロー",
        [0x0E] = "OHP用マゼンタ",
        [0x0F] = "OHP用シアン",
        [0x10] = "紙用特色ホワイト",
        [0x11] = "紙用光沢仕上げ",
        [0x12] = "紙用MFインク",
        [0x13] = "紙用光沢仕上げ2",
        [0x14] = "ラベカブラック",
        [0x15] = "ラベカレッド",
        [0x16] = "ラベカブルー",
        [0x17] = "紙用エコブラック",
        [0x19] = "フォト用イエロー",
        [0x1A] = "フォト用マゼンタ",
        [0x1B] = "フォト用シアン",
        [0x1C] = "オーバーコート",
        [0x1E] = "オーバーコート",
        [0x20] = "ポップ用赤",
        [0x21] = "ポップ用緑",
        [0x22] = "ポップ用青",
    };

    /// <summary>上位ビットが立っているとき、そのカセットは使えない状態にある
    /// (2026-08-19 実測)。シアンのリボンが終端まで来た機体が `0x83` を返し、
    /// 利用者が現物を見てシアンが切れていることを確認した。
    /// `0x83 = 0x80 | 0x03` で、下位 7 ビットは紙用シアンのバーコードである。
    ///
    /// **理由までは断定しない。** §11.4 の教訓のとおり、1 例では「その値が
    /// その意味を持つ」ことしか示せず「その意味だけを表す」ことは示せない。
    /// リボン切れ以外(異常検出など)でも立つ可能性が残る。したがって
    /// 「使い切り」ではなく「使用不可」と表示する。</summary>
    public const byte UnusableFlag = 0x80;

    /// <summary>バーコード番号を日本語名に変換する。未装着(0xff)は「未装着」、
    /// 上位ビットが立っていればインク名 + 「使用不可」、対応表に無い値は
    /// 「不明なカセット(0xNN)」と正直に表示する。</summary>
    public static string GetName(byte barcode)
    {
        if (barcode == CassetteStatus.NotLoaded)
        {
            return "未装着";
        }

        if (Names.TryGetValue(barcode, out var name))
        {
            return name;
        }

        // 上位ビットを外すと既知のインクになるなら、名指ししたうえで
        // 使用不可であることを添える。「不明なカセット」よりも役に立つ。
        byte cleared = (byte)(barcode & ~UnusableFlag);
        if ((barcode & UnusableFlag) != 0 && Names.TryGetValue(cleared, out var baseName))
        {
            return $"{baseName}(使用不可。リボン切れの可能性)";
        }

        return $"不明なカセット(0x{barcode:x2})";
    }
}

/// <summary>CassetteStatus を §7.2 のプリンタ状態表示向けに解釈するデコーダ。</summary>
public static class StatusDecoder
{
    /// <summary>確定している機構エラー(rec[9] 先頭バイト → 文言)。純正ドライバの
    /// エラー文言カタログ(DOMAIN §13.8.3)と語彙を揃える。機構エラーは 8 種類あるが、
    /// 実測で対応づけられているのはこの 1 件のみ。残りを推測で埋めない。</summary>
    private static readonly IReadOnlyDictionary<byte, string> KnownMechanismErrors = new Dictionary<byte, string>
    {
        [0x80] = "機構に異常が発生しました(種別は特定できません)",
    };

    /// <summary>状態バイトが確定していて、かつエラー/印刷実行中のいずれのビットも
    /// 立っていない場合の文言(DOMAIN §11.4 の観測表)。</summary>
    private static readonly IReadOnlyDictionary<byte, string> KnownIdleStates = new Dictionary<byte, string>
    {
        [0x00] = "待機",
        [0x01] = "ジョブ完了直後",
        [0x10] = "待機",
    };

    /// <summary>状態バイトをビットフラグへ分解する(DOMAIN §11.4)。</summary>
    public static PrinterStatusFlags GetFlags(byte statusByte)
    {
        var flags = PrinterStatusFlags.None;
        if ((statusByte & (byte)PrinterStatusFlags.Error) == (byte)PrinterStatusFlags.Error)
        {
            flags |= PrinterStatusFlags.Error;
        }
        if ((statusByte & (byte)PrinterStatusFlags.Printing) == (byte)PrinterStatusFlags.Printing)
        {
            flags |= PrinterStatusFlags.Printing;
        }
        return flags;
    }

    /// <summary>rec[9] 先頭バイト(CassetteStatus.SlotBarcodes[9])からエラー文言を得る。
    /// 未知の値は「未知のエラー(状態=0xNN, rec9=0xNN)」と生の値付きで表示する。</summary>
    private static string DescribeError(CassetteStatus status)
    {
        byte rec9 = status.SlotBarcodes[9];
        if (KnownMechanismErrors.TryGetValue(rec9, out var message))
        {
            return message;
        }
        return $"未知のエラー(状態=0x{status.StatusByte:x2}, rec9=0x{rec9:x2})";
    }

    /// <summary>CassetteStatus を人が読める報告に変換する(DOMAIN §7.2)。</summary>
    public static PrinterStatusReport Describe(CassetteStatus status)
    {
        var flags = GetFlags(status.StatusByte);
        string summary;
        string? errorDetail = null;

        if (flags.HasFlag(PrinterStatusFlags.Error))
        {
            errorDetail = DescribeError(status);
            summary = $"エラー — {errorDetail}";
        }
        else if (flags.HasFlag(PrinterStatusFlags.Printing))
        {
            summary = "印刷実行中";
        }
        else if (KnownIdleStates.TryGetValue(status.StatusByte, out var idleText))
        {
            summary = idleText;
        }
        else
        {
            summary = $"未知の状態(0x{status.StatusByte:x2})";
        }

        var slots = new List<CassetteSlotInfo>(8);
        for (int i = 0; i < 8; i++)
        {
            byte barcode = status.SlotBarcodes[i];
            slots.Add(new CassetteSlotInfo(i + 1, barcode, CassetteCatalog.GetName(barcode)));
        }

        return new PrinterStatusReport(
            flags,
            summary,
            errorDetail,
            CassetteCatalog.GetName(status.HeadCassette),
            slots);
    }
}
