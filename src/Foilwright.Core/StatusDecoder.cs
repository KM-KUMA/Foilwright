// Foilwright.Core — CassetteStatus(38 バイトの状態応答)を人が読める日本語へ変換する
// デコーダ(DOMAIN §7.2 のプリンタ状態表示に対応)。
//
// パケット構造・エラーバイトの解読は一次情報(ppmtomd 付属 getstat.pl の
// parse_status)に基づく。中身はリポジトリにコピーしない
// (https://ppmtomd.julianbradfield.org/getstat.pl 。参照は URL のみ。DOMAIN §11.4 参照)。
//
//   - 状態バイトはビットフラグの集合。0xc9 = 0xc0(エラー) | 0x09(印刷実行中) の
//     重ね合わせに実測の裏付けがある(DOMAIN §11.4)。
//   - エラーバイト(CassetteStatus.ErrorBytes、offset 32-36 = e[0..4])は
//     getstat.pl の parse_status の論理どおりに解読する。旧実装は
//     「3 バイト x 11 レコード」という誤ったパケット解釈のもとで
//     このエラーバイトを「rec[9]/rec[10]」と呼び、ETX(offset 37)まで
//     データとして読んでいた。その誤解釈のもとでは種別を特定できなかった
//     (2026-08-04 の結論を 2026-08-08 に撤回)が、正しい構造で読み直すと
//     機種をまたいで矛盾なく種別が分かれる(2026-08-20 確認)。
//   - エラー中はカセット情報が更新されない(§11.4)。呼び出し側が鵜呑みにしないよう
//     IsError / CassetteInfoMayBeStale を明示する。

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

    /// <summary>エントリの stat バイト上位 2 ビットが示す状態
    /// (一次情報: getstat.pl の parse_status。DOMAIN §11.4 参照)。</summary>
    private enum RibbonState
    {
        /// <summary>正常装着。</summary>
        Normal = 0,

        /// <summary>リボン逆装着(マニュアルでは reserved だが実測でこの意味と判明)。</summary>
        Reversed = 1,

        /// <summary>リボン終端。</summary>
        End = 2,

        /// <summary>カセット無し。</summary>
        NoCassette = 3,
    }

    /// <summary>バーコード番号を日本語名に変換する(stat バイトそのものを受け取る)。
    /// 上位 2 ビットで状態(正常/リボン逆装着/リボン終端/カセット無し)を判定し、
    /// 下位 6 ビットでインク名を引く。カセット無しは「未装着」、対応表に無いバーコードは
    /// 「不明なカセット(0xNN)」と正直に表示する。</summary>
    public static string GetName(byte statByte)
    {
        var state = (RibbonState)(statByte >> 6);
        if (state == RibbonState.NoCassette)
        {
            return "未装着";
        }

        byte barcode = (byte)(statByte & 0x3F);
        string baseName = Names.TryGetValue(barcode, out var name)
            ? name
            : $"不明なカセット(0x{barcode:x2})";

        return state switch
        {
            RibbonState.Reversed => $"{baseName}・リボン逆装着",
            RibbonState.End => $"{baseName}・リボン終端",
            _ => baseName,
        };
    }
}

/// <summary>CassetteStatus を §7.2 のプリンタ状態表示向けに解釈するデコーダ。</summary>
public static class StatusDecoder
{
    /// <summary>Motor Error 詳細(ErrorBytes[4] のビット → 文言)。
    /// 一次情報: getstat.pl の parse_status(289-364 行)。DOMAIN §11.4 参照。</summary>
    private static readonly (byte Bit, string Label)[] MotorErrorDetails =
    {
        (0x80, "カセットチェンジャ"),
        (0x40, "CR"),
        (0x20, "ベイルアーム"),
        (0x10, "LF"),
        (0x08, "給紙"),
        (0x04, "アンチカール"),
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

    /// <summary>ErrorBytes(e[0..4])を getstat.pl の parse_status の論理どおりに解読する。
    /// どの既知ビットにも該当しない場合は生の値付きで「未知のエラー」を返す。</summary>
    private static string DescribeError(CassetteStatus status)
    {
        byte e0 = status.ErrorBytes[0];
        byte e1 = status.ErrorBytes[1];
        byte e2 = status.ErrorBytes[2];
        byte e3 = status.ErrorBytes[3];
        byte e4 = status.ErrorBytes[4];

        var messages = new List<string>();

        if ((e0 & 0x80) != 0)
        {
            var details = new List<string>();
            foreach (var (bit, label) in MotorErrorDetails)
            {
                if ((e4 & bit) != 0)
                {
                    details.Add(label);
                }
            }
            messages.Add(details.Count > 0
                ? $"モータエラー({string.Join("・", details)})"
                : "モータエラー");
        }

        if ((e0 & 0x40) != 0)
        {
            // getstat.pl の parse_status(DOMAIN §11.4.0)。2026-08-20 に実機で確認 —
            // カバーを開けると 0xC0 / e[0]=0x40 になり、閉じると消えた。
            messages.Add("カバーが開いています");
        }

        if ((e0 & 0x01) != 0)
        {
            messages.Add("EEPROM エラー");
        }

        if ((e1 & 0x80) != 0)
        {
            string kind = (e2 & 0x80) != 0 ? "用紙サイズ違い" : "用紙なし";
            string source = (e2 & 0x40) != 0 ? "手差し" : "トレイ";
            messages.Add($"{kind}({source})");
        }

        if ((e1 & 0x40) != 0)
        {
            string kind = (e2 & 0x08) != 0 ? "排紙エラー" : "給紙ミスフィード";
            string source = (e2 & 0x04) != 0 ? "手差し" : "トレイ";
            messages.Add($"紙詰まり({kind}・{source})");
        }

        if ((e1 & 0x22) != 0)
        {
            messages.Add((e1 & 0x20) != 0 ? "リボン終端" : "リボン破断");
        }

        int cassetteCode = (e3 & 0x07) >> 1;
        if (cassetteCode == 2)
        {
            messages.Add("カセット占有(Cassette Occupied)");
        }
        else if (cassetteCode == 3)
        {
            messages.Add("リボン不一致(Ribbon Mismatch)");
        }

        if (messages.Count == 0)
        {
            byte[] raw = { e0, e1, e2, e3, e4 };
            return $"未知のエラー(状態=0x{status.StatusByte:x2}, e={Convert.ToHexString(raw)})";
        }

        return string.Join("+", messages);
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
