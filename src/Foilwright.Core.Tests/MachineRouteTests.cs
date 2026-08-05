// Foilwright.Core.Tests — 機種 → 接続経路の対応表(D-025)の単体テスト。
// 実機に触れず、Resolve() が返す (プロファイル・送出方式・VID) の組み合わせを
// 検証する(CLI の --machine / --vid を実機なしで確認するための手段)。

namespace Foilwright.Core.Tests;

public class MachineRouteTests
{
    [Fact]
    public void Resolve_Md5000_UsesRawModeAndCableVid()
    {
        var route = MachineRoute.Resolve("md-5000");

        Assert.Equal("md-5000.yaml", route.ProfileFileName);
        Assert.Equal(TransportMode.Raw, route.Mode);
        Assert.Equal("VID_056E", route.Vid);
    }

    [Fact]
    public void Resolve_Md5500_UsesPacketModeAndAlpsVid()
    {
        var route = MachineRoute.Resolve("md-5500");

        Assert.Equal("md-5500.yaml", route.ProfileFileName);
        Assert.Equal(TransportMode.Packet, route.Mode);
        Assert.Equal("VID_044E", route.Vid);
    }

    [Fact]
    public void Resolve_IsCaseInsensitive()
    {
        var route = MachineRoute.Resolve("MD-5000");

        Assert.Equal("md-5000.yaml", route.ProfileFileName);
    }

    [Fact]
    public void Resolve_UnknownMachine_ThrowsWithKnownMachinesListed()
    {
        var ex = Assert.Throws<MachineRouteException>(() => MachineRoute.Resolve("md-9999"));

        Assert.Contains("md-9999", ex.Message);
        Assert.Contains("md-5000", ex.Message);
        Assert.Contains("md-5500", ex.Message);
    }

    [Fact]
    public void DefaultMachine_IsMd5000()
    {
        // D-025: 先行機を MD-5000 へ戻した。CLI の既定はここに一致させる。
        Assert.Equal("md-5000", MachineRoute.DefaultMachine);
    }
}
