// Foilwright.Core — 機種 → 接続経路の対応表(D-025)。
//
// 機種を選ぶと、プロファイルファイル名・送出方式(TransportMode)・デバイス探索の
// VID の 3 つがまとめて決まる。DOMAIN §3.2 の経路表・§15.2.1 が正。
//
// この対応はコード内の対応表として 1 箇所に集約する(接続経路の知識であり、
// DOMAIN §4.5 が禁じる「インク・用紙・メディアのハードコード」には該当しない
// — CLI 実装 spec の Constraints で明記済み)。ただし VID は個体差がありうる
// ため、呼び出し側(CLI)で上書きできるようにしてある(Route.Vid を参照した
// うえで上書き値を渡す形)。

namespace Foilwright.Core;

/// <summary>機種の接続経路の対応表の検索に失敗したときに送出する。</summary>
public sealed class MachineRouteException : Exception
{
    public MachineRouteException(string message) : base(message) { }
}

/// <summary>機種 1 つ分の接続経路(D-025)。</summary>
public sealed record MachineRoute(string Machine, string ProfileFileName, TransportMode Mode, string Vid)
{
    /// <summary>既知の機種の一覧(DOMAIN §3.2)。
    /// キーは --machine 引数の値と一致させる。</summary>
    private static readonly IReadOnlyDictionary<string, MachineRoute> Routes =
        new Dictionary<string, MachineRoute>(StringComparer.OrdinalIgnoreCase)
        {
            ["md-5500"] = new MachineRoute("md-5500", "md-5500.yaml", TransportMode.Packet, "VID_044E"),
            ["md-5000"] = new MachineRoute("md-5000", "md-5000.yaml", TransportMode.Raw, "VID_056E"),
        };

    /// <summary>D-025 の既定の先行機。</summary>
    public const string DefaultMachine = "md-5000";

    /// <summary>--machine 引数の値から、選べる機種名を "|" 区切りで返す
    /// (使い方表示・エラーメッセージ用)。</summary>
    public static string KnownMachinesDescription => string.Join("|", Routes.Keys);

    /// <summary>機種名から接続経路を解決する。未知の機種名なら例外を投げる
    /// (黙って既定値へフォールバックしない)。</summary>
    public static MachineRoute Resolve(string machine)
    {
        if (Routes.TryGetValue(machine, out var route))
        {
            return route;
        }
        throw new MachineRouteException(
            $"不明な機種 '{machine}'。次のいずれかを指定してください: {KnownMachinesDescription}");
    }
}
