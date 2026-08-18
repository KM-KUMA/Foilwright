// Foilwright.Core.Tests — golden バイト一致検証。
//
// tests/golden/*.bin(ppmtomd 1.6 で採取)に対する ref/tests/test_golden.py
// の各テストに対応する。golden はこのテストで一切変更しない — 不一致は
// C# 側の実装が誤っていることを意味する(D-006 の三段検証、第 3 段)。

using Foilwright.Core;

namespace Foilwright.Core.Tests;

public class GoldenTests
{
    private static readonly string RepoRoot = FindRepoRoot();
    private static readonly string CasesDir = Path.Combine(RepoRoot, "tests", "cases");
    private static readonly string GoldenDir = Path.Combine(RepoRoot, "tests", "golden");
    private static readonly string ProfilesDir = Path.Combine(RepoRoot, "profiles");
    private static readonly string PapersDir = Path.Combine(RepoRoot, "papers");
    private static readonly string MediaYaml = Path.Combine(RepoRoot, "media.yaml");

    private static string FindRepoRoot()
    {
        // src/Foilwright.Core.Tests/bin/Debug/net10.0 から 5 階層上がる。
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 5; i++)
        {
            if (dir.Parent is null)
            {
                break;
            }
            dir = dir.Parent;
        }
        return dir.FullName;
    }

    // 既定(-colours なし)のジョブ: ppmtomd は常にこの順で CMYK 4 成分すべてを
    // 駆動する。一部が全面ブランクになる場合も含む
    // (ppmtomd.c:1469-1490 既定 comp_colours; :84-86 comp_print_order)。
    private static readonly Dictionary<string, string> DefaultPalette = new()
    {
        ["cyan"] = "C",
        ["magenta"] = "M",
        ["yellow"] = "Y",
        ["black"] = "K",
    };

    private static readonly List<JobInk> DefaultInks = new()
    {
        new JobInk { Name = "cyan", PrinterCode = 0x01 },
        new JobInk { Name = "magenta", PrinterCode = 0x02 },
        new JobInk { Name = "yellow", PrinterCode = 0x03 },
        new JobInk { Name = "black", PrinterCode = 0x00 },
    };

    // `-colours K=White`: K のみアクティブで、Black の色選択バイトではなく
    // White の色選択バイト(mddata.h colWhite = 0x0B)で駆動される
    // (ppmtomd.c:1517-1525 K=... のパース、mddata.c の色 enum)。
    private static readonly Dictionary<string, string> WhitePalette = new() { ["white"] = "K" };
    private static readonly List<JobInk> WhiteInks = new() { new JobInk { Name = "white", PrinterCode = 0x0B } };

    // `-colours C=MetallicCyan,M=MetallicMagenta,Y=MetallicGold,K=MetallicSilver`。
    // 色選択バイトは mddata.c の色 enum 順(mddata.c:12-15):
    // Gold=0x04, Magenta=0x05, Cyan=0x06, Silver=0x07。
    private static readonly Dictionary<string, string> Metallic4Palette = new()
    {
        ["metallic_cyan"] = "C",
        ["metallic_magenta"] = "M",
        ["metallic_gold"] = "Y",
        ["metallic_silver"] = "K",
    };

    private static readonly List<JobInk> Metallic4Inks = new()
    {
        new JobInk { Name = "metallic_cyan", PrinterCode = 0x06 },
        new JobInk { Name = "metallic_magenta", PrinterCode = 0x05 },
        new JobInk { Name = "metallic_gold", PrinterCode = 0x04 },
        new JobInk { Name = "metallic_silver", PrinterCode = 0x07 },
    };

    private static readonly Dictionary<string, string> WhiteMultilayerPalette = new()
    {
        ["white"] = "C",
        ["metallic_gold"] = "M",
        ["metallic_silver"] = "Y",
        ["black"] = "K",
    };

    private static readonly List<JobInk> WhiteMultilayerInks = new()
    {
        new JobInk { Name = "white", PrinterCode = 0x0B },
        new JobInk { Name = "metallic_gold", PrinterCode = 0x04 },
        new JobInk { Name = "metallic_silver", PrinterCode = 0x07 },
        new JobInk { Name = "black", PrinterCode = 0x00 },
    };

    // `-colours C=White,M=Finish,Y=MetallicGold,K=Black`。白 → コーティング(Finish)
    // → 色、という作者の実作業手順を反映した並び(DOMAIN §4.11 / §10.7)。golden
    // (g11)は 2026-07-28 の採取時から存在したが、対応するテストが ref/ 側にも
    // 長らく無く未検証のまま残っていた穴で、2026-08-04 に ref/ 側へ追加してから
    // こちらへ移植した。`finish` は palette/default.yaml に存在しないインクなので
    // metallic4 と同様にテストファイル内で定義する。printer_code 0x0E は golden
    // のバイト列からの実測値。
    private static readonly Dictionary<string, string> WhiteFinishPalette = new()
    {
        ["white"] = "C",
        ["finish"] = "M",
        ["metallic_gold"] = "Y",
        ["black"] = "K",
    };

    private static readonly List<JobInk> WhiteFinishInks = new()
    {
        new JobInk { Name = "white", PrinterCode = 0x0B },
        new JobInk { Name = "finish", PrinterCode = 0x0E },
        new JobInk { Name = "metallic_gold", PrinterCode = 0x04 },
        new JobInk { Name = "black", PrinterCode = 0x00 },
    };

    private static (PaperSpec Paper, MediaSpec Media) BuildJobBasics(int resolution, string model, out ProfileSpec profile)
    {
        profile = ConfigLoader.LoadProfile(Path.Combine(ProfilesDir, model + ".yaml"));
        var paperTable = ConfigLoader.ResolvePaperTable(profile, PapersDir);
        var paper = paperTable["a4"];
        // golden はすべて ppmtomd の既定(普通紙)で採取されている
        var media = ConfigLoader.LoadMediaTable(MediaYaml)["plain_paper"];
        return (paper, media);
    }

    private static PrintJob BuildJob(
        int resolution, string model, IReadOnlyList<JobInk> inks, int width, int height,
        int xShift = 0, int yShift = 0, bool noCurlCorrection = false,
        MediaSpec? mediaOverride = null, string transferMode = "colour_plane")
    {
        var (paper, media) = BuildJobBasics(resolution, model, out _);
        return new PrintJob
        {
            Resolution = resolution,
            Paper = paper,
            Media = mediaOverride ?? media,
            Inks = inks,
            Width = width,
            Height = height,
            XShift = xShift,
            YShift = yShift,
            NoCurlCorrection = noCurlCorrection,
            TransferMode = transferMode,
        };
    }

    private static byte[] Render(
        string ppmFileName, int resolution, string model, IReadOnlyList<JobInk> inks,
        IReadOnlyDictionary<string, string> palette, string halftone = "none",
        int xShift = 0, int yShift = 0, MediaSpec? mediaOverride = null,
        bool noCurlCorrection = false, string transferMode = "colour_plane")
    {
        var image = PpmImage.Read(Path.Combine(CasesDir, ppmFileName));
        var planes = Raster.ToPlanes(image, palette, halftone);
        var job = BuildJob(
            resolution, model, inks, image.Width, image.Height, xShift, yShift,
            noCurlCorrection: noCurlCorrection, mediaOverride: mediaOverride,
            transferMode: transferMode);
        return Emitter.EmitJob(planes, job);
    }

    private static void AssertGoldenMatch(byte[] actual, string goldenFileName)
    {
        string goldenPath = Path.Combine(GoldenDir, goldenFileName);
        byte[] expected = File.ReadAllBytes(goldenPath);
        if (actual.AsSpan().SequenceEqual(expected))
        {
            return;
        }
        int limit = Math.Min(actual.Length, expected.Length);
        int firstDiff = limit;
        for (int i = 0; i < limit; i++)
        {
            if (actual[i] != expected[i])
            {
                firstDiff = i;
                break;
            }
        }
        int ctxStart = Math.Max(0, firstDiff - 16);
        string expCtx = Convert.ToHexStringLower(expected, ctxStart, Math.Min(expected.Length, firstDiff + 16) - ctxStart);
        string actCtx = Convert.ToHexStringLower(actual, ctxStart, Math.Min(actual.Length, firstDiff + 16) - ctxStart);
        Assert.Fail(
            $"byte mismatch vs {goldenFileName} at offset {firstDiff} " +
            $"(expected len={expected.Length}, actual len={actual.Length})\n" +
            $"expected[{ctxStart}:]: {expCtx}\n" +
            $"actual[{ctxStart}:]:   {actCtx}");
    }

    [Fact]
    public void G1BlackMd5000_600()
    {
        var actual = Render("c1_black_120x120.ppm", 600, "md-5000", DefaultInks, DefaultPalette);
        AssertGoldenMatch(actual, "g1_c1_black_md5000_600.bin");
    }

    [Fact]
    public void G5BlackMd5500_600_ProfileSwapOnly()
    {
        // MD-5000 と MD-5500 は同じプロファイル値から(機種名という情報だけが
        // 違う状態で)バイト完全一致した出力を出すはず — emitter に機種依存の
        // 分岐は存在しない(DOMAIN §4.4)。
        var actual = Render("c1_black_120x120.ppm", 600, "md-5500", DefaultInks, DefaultPalette);
        AssertGoldenMatch(actual, "g5_c1_black_md5500_600.bin");
    }

    [Fact]
    public void G4BlackMd5000_1200()
    {
        var actual = Render("c1_black_120x120.ppm", 1200, "md-5000", DefaultInks, DefaultPalette);
        AssertGoldenMatch(actual, "g4_c1_black_md5000_1200.bin");
    }

    [Fact]
    public void G2BlackCyanMd5000_600()
    {
        var actual = Render("c2_blackcyan_240x120.ppm", 600, "md-5000", DefaultInks, DefaultPalette);
        AssertGoldenMatch(actual, "g2_c2_blackcyan_md5000_600.bin");
    }

    [Fact]
    public void G3WhiteMd5000_600()
    {
        var actual = Render("c3_black_for_white_120x120.ppm", 600, "md-5000", WhiteInks, WhitePalette);
        AssertGoldenMatch(actual, "g3_c3_white_md5000_600.bin");
    }

    [Fact]
    public void G6SquareOnWhiteMd5000_600()
    {
        // 白背景なので、ブランク行スキップ(ESC * b {n} Y)・行内の末尾ゼロ
        // トリミング・ページ末尾のブランク行の連続、をすべて行使する唯一の
        // ケース。ベタ塗りのケースはこれらの経路に到達しない。
        var actual = Render("c4_square_on_white_120x120.ppm", 600, "md-5000", DefaultInks, DefaultPalette);
        AssertGoldenMatch(actual, "g6_c4_square_md5000_600.bin");
    }

    [Fact]
    public void G10WhiteMultilayerMd5000_600()
    {
        var actual = Render("c5_metallic4_240x120.ppm", 600, "md-5000", WhiteMultilayerInks, WhiteMultilayerPalette);
        AssertGoldenMatch(actual, "g10_c5_white_multilayer_md5000_600.bin");
    }

    [Fact]
    public void G11WhiteFinishColourMd5000_600()
    {
        // g10 と同じ c5_metallic4 入力だが、白の直後に "finish"(コーティング)
        // インクを挟む — 白 → コーティング → 色という作者の実作業手順そのもの
        // (DOMAIN §4.11 / §10.7)。
        var actual = Render("c5_metallic4_240x120.ppm", 600, "md-5000", WhiteFinishInks, WhiteFinishPalette);
        AssertGoldenMatch(actual, "g11_c5_white_finish_colour_md5000_600.bin");
    }

    [Fact]
    public void G15CardboardMediaMd5000_600()
    {
        // 厚紙(メディア 0x05 0x00)。アンダーコート使用時のインクリボン
        // 切れを防ぐ安全設定(DOMAIN §5.5.2 / §10.8.2)。
        var media = ConfigLoader.LoadMediaTable(MediaYaml)["cardboard"];
        var actual = Render("c1_black_120x120.ppm", 600, "md-5000", DefaultInks, DefaultPalette, mediaOverride: media);
        AssertGoldenMatch(actual, "g15_c1_cardboard_md5000_600.bin");
    }

    [Fact]
    public void G12FullColourMd5000_600()
    {
        var actual = Render("c6_fullcolour_240x120.ppm", 600, "md-5000", DefaultInks, DefaultPalette);
        AssertGoldenMatch(actual, "g12_c6_fullcolour_md5000_600.bin");
    }

    [Fact]
    public void G8PositiveShiftMd5000_600()
    {
        // 明示的な -xshift 100 -yshift 200。正のシフトだけが ppmtomd では
        // コマンド(ESC & a {x} L / ESC & l {y} E)として表現される。
        var actual = Render("c1_black_120x120.ppm", 600, "md-5000", DefaultInks, DefaultPalette, xShift: 100, yShift: 200);
        AssertGoldenMatch(actual, "g8_c1_shift_md5000_600.bin");
    }

    [Fact]
    public void G9AutoshiftMd5000_600()
    {
        // ppmtomd の -autoshift は要求シフトから用紙の印字不能マージンを
        // 差し引く。ここでは -xshift 200 -yshift 400 と A4 の left=80 top=284
        // から 120 と 116 になる。
        var (paper, _) = BuildJobBasics(600, "md-5000", out _);
        var actual = Render(
            "c1_black_120x120.ppm", 600, "md-5000", DefaultInks, DefaultPalette,
            xShift: 200 - paper.LeftMargin, yShift: 400 - paper.TopMargin);
        AssertGoldenMatch(actual, "g9_c1_autoshift_md5000_600.bin");
    }

    [Fact]
    public void NegativeShiftIsRejected()
    {
        // 負のシフトは ppmtomd ではコマンドではなくラスタのトリミングを
        // 意味する。その経路は未実装なので、誤った位置に印字する代わりに
        // 明確に失敗しなければならない。
        var image = PpmImage.Read(Path.Combine(CasesDir, "c1_black_120x120.ppm"));
        var planes = Raster.ToPlanes(image, DefaultPalette);
        var job = BuildJob(600, "md-5000", DefaultInks, image.Width, image.Height, xShift: -10);
        Assert.Throws<EmitterNotImplementedException>(() => Emitter.EmitJob(planes, job));
    }

    [Fact]
    public void G13HalftoneMd5000_600()
    {
        var actual = Render("c6_fullcolour_240x120.ppm", 600, "md-5000", DefaultInks, DefaultPalette, halftone: "halftone");
        AssertGoldenMatch(actual, "g13_c6_halftone_md5000_600.bin");
    }

    [Fact]
    public void G14CoarseHalftoneMd5000_600()
    {
        var actual = Render("c6_fullcolour_240x120.ppm", 600, "md-5000", DefaultInks, DefaultPalette, halftone: "coarse_halftone");
        AssertGoldenMatch(actual, "g14_c6_coarsehalftone_md5000_600.bin");
    }

    [Fact]
    public void G7Metallic4Md5000_600()
    {
        // メタリック 4 色はすべて order が同値。パス順はインク一覧そのものの
        // 記述順で決まる(DOMAIN §4.3 の tie-break、§4.9 の安定ソート)。
        var actual = Render("c5_metallic4_240x120.ppm", 600, "md-5000", Metallic4Inks, Metallic4Palette);
        AssertGoldenMatch(actual, "g7_c5_metallic4_md5000_600.bin");
    }

    private static int CountEjects(byte[] data)
    {
        int i = 0;
        int ejects = 0;
        while (i < data.Length)
        {
            byte b = data[i];
            if (b == 0x0C)
            {
                ejects += 1;
                i += 1;
                continue;
            }
            if (b != 0x1B)
            {
                throw new InvalidOperationException($"unexpected byte {b:x2} at offset {i}");
            }
            byte kind = data[i + 1];
            if (kind == 0x25)
            {
                i += 4;
            }
            else if (kind == 0x65)
            {
                i += 2;
            }
            else if (kind == 0x1A)
            {
                i += 5;
            }
            else if (kind == 0x26)
            {
                i += 6;
            }
            else if (kind == 0x2A)
            {
                byte sub = data[i + 2];
                if (sub == 0x74)
                {
                    i += 6;
                }
                else if (sub == 0x72)
                {
                    i += data[i + 3] == 0x43 ? 4 : 5;
                }
                else if (sub == 0x62)
                {
                    int length = data[i + 3] + data[i + 4] * 256;
                    byte cmd = data[i + 5];
                    i += 6 + (cmd == 0x56 || cmd == 0x57 ? length : 0);
                }
                else
                {
                    throw new InvalidOperationException($"unknown ESC * {sub:x2} at offset {i}");
                }
            }
            else
            {
                throw new InvalidOperationException($"unknown ESC {kind:x2} at offset {i}");
            }
        }
        return ejects;
    }

    [Fact]
    public void G17_NoCurl_Md5000_600()
    {
        // -nocurlcorrection: デカールシートは平らなまま送らなければならない
        // ため、カール補正バイトを抑制する(DOMAIN §10.10.4)。本プロジェクトの
        // 主用途そのものなので、g1 との 1 バイト差に golden を割いている。
        //
        // g17 が g1 と違うのはオフセット 0x24 の 1 バイトだけで、
        // `1b 1a 00 00 43` が `1b 1a 01 00 43` になる。
        var actual = Render(
            "c1_black_120x120.ppm", 600, "md-5000", DefaultInks, DefaultPalette,
            noCurlCorrection: true);
        AssertGoldenMatch(actual, "g17_c1_nocurl_md5000_600.bin");
    }

    [Fact]
    public void G16_BlackRaster_Md5000_600()
    {
        // -black: 単一プレーンの転送モード。モードバイト自身がどのリボンを
        // 使うかを表すため、色選択コマンドもパス間のバックフィードも持たない。
        // colourPlane の g1 より 35 バイト短い(1026 対 1061)。DOMAIN §11.1.1。
        var inks = new[] { new JobInk { Name = "black", PrinterCode = 0x00 } };
        var palette = new Dictionary<string, string> { ["black"] = "K" };
        var actual = Render(
            "c1_black_120x120.ppm", 600, "md-5000", inks, palette,
            transferMode: "black_raster");
        AssertGoldenMatch(actual, "g16_c1_blackraster_md5000_600.bin");
    }

    [Fact]
    public void SingleEjectAcrossAllGolden()
    {
        // DOMAIN §4.10: 用紙は最終パスの後に一度だけ排出しなければならない。
        // パス間で排出すると位置合わせが取り返しなく壊れる(§10.6)。
        foreach (var path in Directory.GetFiles(GoldenDir, "*.bin").OrderBy(p => p, StringComparer.Ordinal))
        {
            int ejects = CountEjects(File.ReadAllBytes(path));
            Assert.True(ejects == 1, $"{Path.GetFileName(path)}: expected 1 eject, found {ejects}");
        }
    }
}
