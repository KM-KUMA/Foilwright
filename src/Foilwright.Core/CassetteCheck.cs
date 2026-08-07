// Foilwright.Core — カセットの過不足判定(DOMAIN §7.3 / D-026)。
//
// ジョブが必要とするインク(JobAssembly.BuildJobPlanes が返す InkDefinition の
// 一覧)と、状態応答(CassetteStatus)が報告する装填状況を突き合わせ、
// 不足しているインクを名指しする。
//
// 満たすべき不変条件(D-026 補足 / DOMAIN §11.4):
//   - エラー中はカセット情報が更新されない実測がある。エラー中は判定せず、
//     「判定不能」を返す(「足りない」と誤って言わない)。
//   - パレットの `barcode` が未設定のインクは、そもそも突き合わせの元になる
//     値が無いため判定できない。「不足」とは区別して「判定不能」として返す。
//   - ヘッドに装着中のカセット(CassetteStatus.HeadSlotIndex = rec[8])も
//     装填済みとして数える(§7.3 の実装への含意)。

namespace Foilwright.Core;

/// <summary>過不足判定の総合結果。</summary>
public enum CassetteCheckStatus
{
    /// <summary>必要なインクがすべて装填されている。</summary>
    Sufficient,

    /// <summary>1 つ以上のインクが不足している。</summary>
    Insufficient,

    /// <summary>エラー中で状態応答のカセット情報が現物と一致しない可能性があるため、
    /// 判定できない(DOMAIN §11.4 / D-026 補足)。</summary>
    Indeterminate,
}

/// <summary>過不足判定における、利用者に見せられる形の 1 インク分の情報。</summary>
public sealed record CassetteCheckInk(string Name, string Label);

/// <summary>CassetteCheck.Evaluate の結果。</summary>
public sealed record CassetteCheckResult(
    CassetteCheckStatus Status,
    IReadOnlyList<CassetteCheckInk> MissingInks,
    IReadOnlyList<CassetteCheckInk> UndeterminableInks)
{
    /// <summary>足りているか。Indeterminate のときは「足りているとは断言できない」ことを
    /// 明確にするため false を返す — 呼び出し側が「足りている」と誤読しないようにする。</summary>
    public bool IsSufficient => Status == CassetteCheckStatus.Sufficient;
}

/// <summary>DOMAIN §7.3 のカセット過不足判定(D-026)。</summary>
public static class CassetteCheck
{
    /// <summary>ジョブが必要とするインクの一覧と、プリンタの状態応答を突き合わせる。
    ///
    /// requiredInks: JobAssembly.BuildJobPlanes(...)  が返すタプルの Ink 側など、
    ///     ジョブが実際に使うインクの一覧(重複があっても構わない — 内部で
    ///     Barcode の有無だけを見る)。
    /// status: `05 01` で読んだ状態応答のパース結果。</summary>
    public static CassetteCheckResult Evaluate(IReadOnlyList<InkDefinition> requiredInks, CassetteStatus status)
    {
        // DOMAIN §11.4: エラー中はカセット情報が更新されない。判定不能を返す。
        var flags = StatusDecoder.GetFlags(status.StatusByte);
        if (flags.HasFlag(PrinterStatusFlags.Error))
        {
            return new CassetteCheckResult(
                CassetteCheckStatus.Indeterminate,
                Array.Empty<CassetteCheckInk>(),
                Array.Empty<CassetteCheckInk>());
        }

        // ホルダ 8 スロット(index 0..7)+ ヘッドに装着中のカセット(index 8)を
        // 装填済みとして数える(§7.3 / CassetteStatus.HeadSlotIndex)。
        var loadedBarcodes = new HashSet<byte>();
        for (int i = 0; i <= CassetteStatus.HeadSlotIndex; i++)
        {
            byte barcode = status.SlotBarcodes[i];
            if (barcode != CassetteStatus.NotLoaded)
            {
                loadedBarcodes.Add(barcode);
            }
        }

        var missing = new List<CassetteCheckInk>();
        var undeterminable = new List<CassetteCheckInk>();
        foreach (var ink in requiredInks)
        {
            if (ink.Barcode is null)
            {
                undeterminable.Add(new CassetteCheckInk(ink.Name, ink.Label));
                continue;
            }
            if (!loadedBarcodes.Contains((byte)ink.Barcode.Value))
            {
                missing.Add(new CassetteCheckInk(ink.Name, ink.Label));
            }
        }

        var overall = missing.Count > 0 ? CassetteCheckStatus.Insufficient : CassetteCheckStatus.Sufficient;
        return new CassetteCheckResult(overall, missing, undeterminable);
    }
}
