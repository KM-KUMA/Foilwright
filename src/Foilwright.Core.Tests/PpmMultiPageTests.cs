// Foilwright.Core.Tests — PpmImage.Read の入力分類の検証。
//
// Ghostscript は -sOutputFile に %d が無いと、複数ページを 1 つの ppmraw
// ファイルへ連結して書き出す。連結された PPM を黙って 1 ページ目だけ読むと
// 利用者が気づけないため、明確なエラーで止める。ref/foilwright_ref/raster.py
// の read_ppm と同じ分類・同じ文言にしてある(D-006: ref/ と src/ は
// 突き合わせる関係。ref/tests/test_ppm_multipage_cross_language.py が
// 両者の分類の一致を検証する)。

using Foilwright.Core;

namespace Foilwright.Core.Tests;

public class PpmMultiPageTests : IDisposable
{
    private readonly string _dir;

    public PpmMultiPageTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"foilwright_ppm_{Guid.NewGuid():n}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (IOException)
        {
            // 後始末の失敗はテストの成否に影響しない。
        }
        GC.SuppressFinalize(this);
    }

    /// <summary>P6 の 1 ページ分のバイト列を作る。画素値は位置から決まる
    /// ので、読み戻した中身の検証にも使える。</summary>
    private static byte[] MakePpm(int width, int height, byte seed)
    {
        byte[] header = System.Text.Encoding.ASCII.GetBytes($"P6\n{width} {height}\n255\n");
        byte[] pixels = new byte[width * height * 3];
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = (byte)(seed + i);
        }
        return header.Concat(pixels).ToArray();
    }

    private string Write(string name, byte[] data)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void Read_SinglePage_Succeeds()
    {
        // 退行の検出器: 単一ページはこれまでどおり読めること。
        byte[] page = MakePpm(4, 3, seed: 7);
        var image = PpmImage.Read(Write("single.ppm", page));

        Assert.Equal(4, image.Width);
        Assert.Equal(3, image.Height);
        Assert.Equal(4 * 3 * 3, image.Pixels.Length);
        Assert.Equal(7, image.Pixels[0]);
        Assert.Equal((byte)(7 + image.Pixels.Length - 1), image.Pixels[^1]);
    }

    [Fact]
    public void Read_Truncated_ThrowsTruncatedAndIsNotMultiPage()
    {
        byte[] page = MakePpm(4, 3, seed: 0);
        byte[] shortened = page.Take(page.Length - 5).ToArray();

        var ex = Assert.Throws<PpmFormatException>(() => PpmImage.Read(Write("short.ppm", shortened)));
        Assert.Equal("truncated PPM data: expected 36 bytes, got 31", ex.Message);
        Assert.False(ex.IsMultiPage);
    }

    [Fact]
    public void Read_TwoConcatenatedImages_ThrowsMultiPage()
    {
        byte[] two = MakePpm(4, 3, seed: 0).Concat(MakePpm(4, 3, seed: 100)).ToArray();

        var ex = Assert.Throws<PpmFormatException>(() => PpmImage.Read(Write("two.ppm", two)));
        Assert.True(ex.IsMultiPage);
        Assert.Equal(
            "multi-page PPM: the document has more than one page; "
                + "Foilwright prints one page per job (found 2 pages)",
            ex.Message);
    }

    [Fact]
    public void Read_ThreeConcatenatedImages_CountsPagesExactly()
    {
        // ページ数はヘッダを読み進めて数える。画素データ中に "P6 " が
        // 現れても数を狂わせない(2 ページ目の画素へわざと埋め込む)。
        byte[] first = MakePpm(4, 3, seed: 0);
        byte[] second = MakePpm(4, 3, seed: 0);
        byte[] decoy = System.Text.Encoding.ASCII.GetBytes("P6 ");
        Array.Copy(decoy, 0, second, second.Length - 6, decoy.Length);
        byte[] third = MakePpm(2, 2, seed: 5);
        byte[] three = first.Concat(second).Concat(third).ToArray();

        var ex = Assert.Throws<PpmFormatException>(() => PpmImage.Read(Write("three.ppm", three)));
        Assert.True(ex.IsMultiPage);
        Assert.Contains("(found 3 pages)", ex.Message);
    }

    [Fact]
    public void Read_TrailingJunkThatIsNotAPpm_ThrowsTrailingDataAndIsNotMultiPage()
    {
        byte[] junk = MakePpm(4, 3, seed: 0).Concat(new byte[] { 0x00, 0x01, 0x02, 0x03 }).ToArray();

        var ex = Assert.Throws<PpmFormatException>(() => PpmImage.Read(Write("junk.ppm", junk)));
        Assert.False(ex.IsMultiPage);
        Assert.Equal(
            "unexpected trailing data after PPM image: expected 36 bytes, got 40",
            ex.Message);
    }

    [Fact]
    public void Read_TrailingLooksLikeMagicButHasNoValidHeader_IsNotMultiPage()
    {
        // "P6" の直後に空白が来ないので、次の PPM の始まりとは見なさない。
        byte[] junk = MakePpm(4, 3, seed: 0)
            .Concat(System.Text.Encoding.ASCII.GetBytes("P6x"))
            .ToArray();

        var ex = Assert.Throws<PpmFormatException>(() => PpmImage.Read(Write("fakemagic.ppm", junk)));
        Assert.False(ex.IsMultiPage);
        Assert.StartsWith("unexpected trailing data after PPM image:", ex.Message);
    }

    [Fact]
    public void Read_SecondPageIsItselfTruncated_ReportsPageCountAsLowerBound()
    {
        // 2 ページ目が途中で切れている場合、正確な枚数は分からない。
        // 嘘の枚数を出さず「at least」と言う。
        byte[] second = MakePpm(4, 3, seed: 0);
        byte[] two = MakePpm(4, 3, seed: 0).Concat(second.Take(second.Length - 4)).ToArray();

        var ex = Assert.Throws<PpmFormatException>(() => PpmImage.Read(Write("cut.ppm", two)));
        Assert.True(ex.IsMultiPage);
        Assert.Contains("(found at least 2 pages)", ex.Message);
    }

    [Fact]
    public void PpmFormatException_DefaultsToNotMultiPage()
    {
        // 既存の 1 引数コンストラクタを壊していないこと。
        var ex = new PpmFormatException("boom");
        Assert.False(ex.IsMultiPage);
        Assert.Equal("boom", ex.Message);
    }
}
