// Foilwright.Core.Tests — PpmImage.Crop の検証。
//
// Ghostscript が描く用紙全面から、プリンタが刷れる印字可能領域を切り出す
// 幾何操作(DOMAIN §3.6 / §4.1)。用紙・余白の意味は Crop 自身は知らない
// ので、ここでは純粋にドット値だけで検証する。

using Foilwright.Core;

namespace Foilwright.Core.Tests;

public class PpmCropTests
{
    // 5x4 の合成画像を作る。画素値は (row, col) がそのまま R に、G は 0、
    // B は 255 - row*10 - col に入るようにして、切り出し後の位置検証を
    // しやすくする。
    private static PpmImage MakeTestImage(int width, int height)
    {
        byte[] pixels = new byte[width * height * 3];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int off = (y * width + x) * 3;
                pixels[off] = (byte)y;
                pixels[off + 1] = (byte)x;
                pixels[off + 2] = 0xFF;
            }
        }
        return new PpmImage(width, height, pixels);
    }

    [Fact]
    public void Crop_NormalCase_ReturnsCorrectCornerPixels()
    {
        // 10x8 の画像から (2, 1) を左上に 5x4 を切り出す。
        var image = MakeTestImage(10, 8);
        var cropped = image.Crop(x: 2, y: 1, width: 5, height: 4);

        Assert.Equal(5, cropped.Width);
        Assert.Equal(4, cropped.Height);

        // 左上画素は元画像の (x=2, y=1) と一致するはず: R=1(元の y), G=2(元の x)
        Assert.Equal(1, cropped.Pixels[0]);
        Assert.Equal(2, cropped.Pixels[1]);
        Assert.Equal(0xFF, cropped.Pixels[2]);

        // 右下画素は元画像の (x=2+5-1=6, y=1+4-1=4) と一致するはず
        // (切り出し後の座標では右下は (col=4, row=3))
        int lastOff = (3 * 5 + 4) * 3;
        Assert.Equal(4, cropped.Pixels[lastOff]);     // R = 元の y = 4
        Assert.Equal(6, cropped.Pixels[lastOff + 1]); // G = 元の x = 6
        Assert.Equal(0xFF, cropped.Pixels[lastOff + 2]);
    }

    [Fact]
    public void Crop_RequestLargerThanImage_TruncatesToAvailableRange()
    {
        // 元画像が要求矩形より小さい場合、利用可能な範囲へ切り詰める
        // (例外にしない — 小さい用紙で刷る場合がある)。
        var image = MakeTestImage(6, 5);
        var cropped = image.Crop(x: 2, y: 2, width: 100, height: 100);

        // 利用可能なのは x: 2..5 (幅4), y: 2..4 (高さ3)
        Assert.Equal(4, cropped.Width);
        Assert.Equal(3, cropped.Height);

        // 左上画素は元画像の (2, 2)
        Assert.Equal(2, cropped.Pixels[0]);
        Assert.Equal(2, cropped.Pixels[1]);

        // 右下画素は元画像の (5, 4)
        int lastOff = (2 * 4 + 3) * 3;
        Assert.Equal(4, cropped.Pixels[lastOff]);
        Assert.Equal(5, cropped.Pixels[lastOff + 1]);
    }

    [Fact]
    public void Crop_OriginOutsideImageOnBothAxes_ReturnsEmptyImage()
    {
        // x も y も画像の外側なら、幅・高さとも 0 に潰れる。
        var image = MakeTestImage(6, 5);
        var cropped = image.Crop(x: 6, y: 5, width: 5, height: 5);

        Assert.Equal(0, cropped.Width);
        Assert.Equal(0, cropped.Height);
        Assert.Empty(cropped.Pixels);
    }

    [Fact]
    public void Crop_OriginOutsideImageOnOneAxis_CollapsesThatDimensionOnly()
    {
        // x だけが画像の外側なら、幅は 0 に潰れるが高さは要求どおり
        // (画素配列は 0 x height x 3 = 0 バイトで内部無矛盾)。
        var image = MakeTestImage(6, 5);
        var cropped = image.Crop(x: 6, y: 0, width: 5, height: 5);

        Assert.Equal(0, cropped.Width);
        Assert.Equal(5, cropped.Height);
        Assert.Empty(cropped.Pixels);
    }

    [Fact]
    public void Crop_DimensionsMatchRequestWhenWithinBounds()
    {
        // 要求矩形が元画像に収まる典型ケースで、切り出し後の寸法が
        // width x height とちょうど一致することを確認する。
        var image = MakeTestImage(4958, 7017); // A4/600dpi の Ghostscript 出力寸法
        var cropped = image.Crop(x: 80, y: 284, width: 4800, height: 6372); // papers/5000-series.yaml の a4

        Assert.Equal(4800, cropped.Width);
        Assert.Equal(6372, cropped.Height);
    }

    [Fact]
    public void Crop_NegativeOriginThrows()
    {
        var image = MakeTestImage(4, 4);
        Assert.Throws<ArgumentException>(() => image.Crop(-1, 0, 2, 2));
        Assert.Throws<ArgumentException>(() => image.Crop(0, -1, 2, 2));
    }

    [Fact]
    public void Crop_NegativeSizeThrows()
    {
        var image = MakeTestImage(4, 4);
        Assert.Throws<ArgumentException>(() => image.Crop(0, 0, -1, 2));
        Assert.Throws<ArgumentException>(() => image.Crop(0, 0, 2, -1));
    }

    [Fact]
    public void Crop_DoesNotMutateSourceImage()
    {
        var image = MakeTestImage(6, 5);
        byte[] before = (byte[])image.Pixels.Clone();
        _ = image.Crop(1, 1, 2, 2);
        Assert.Equal(before, image.Pixels);
    }
}
