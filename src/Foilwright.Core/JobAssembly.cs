// Foilwright.Core — L2/L1 境界: インク指定方式(DOMAIN §6.6 / D-016)を選び、
// パレットから導出した情報だけでインク別プレーンを組み立て、ジョブに
// 含めるインクを決める。
//
// ここでの責務は 3 つ:
//   1. auto 方式で cmyk_map をパレットの channel フィールドから導出し
//      (D-019)、magic_rgb と channel の両方を持つ二役インク(黒)の
//      プレーンを 1 つに合成する(D-019 補足の後続課題)。
//   2. 中身の無い(1 ドットも立っていない)プレーンのインクを除外する。
//      パスは時間とリボンを消費し、装填していないカセットを要求すると
//      失敗するため(空パスを組む理由が無い)。
//   3. 白版モード(DOMAIN §7.1 / D-027)を適用する。設定がパレットの
//      auto_undercoat を上書きする — パレット側のフラグはあくまで既定値。
//      「白」がどのインクかは、パレットで auto_undercoat: true になっている
//      インクとして判別する(名前を決め打ちしない)。
//   4. 「塗る範囲で決まるインク」(D-048)のプレーンを作る。パレットで
//      coverage: true のインクだけが対象で、どこに塗るか(none/artwork/full)は
//      ジョブごとに coverageModes で決まる。白版モードとは別の仕組みとして
//      併存させる(D-048: 動いている白の経路を触らない)。
//
// Raster.cs の既存関数(ToPlanes* 系)のシグネチャ・挙動は一切変更しない
// (golden 検証の対象のため)。本ファイルはそれらを呼び出す側。

namespace Foilwright.Core;

public static class JobAssembly
{
    /// <summary>サポートするインク指定方式の内部識別子(DOMAIN §6.6)。</summary>
    public static readonly IReadOnlyList<string> ValidInkModes = new[] { "auto", "per_page", "spot_only" };

    /// <summary>サポートする白版モードの内部識別子(DOMAIN §7.1 / D-027、
    /// "opaque" は D-032、"silhouette" は D-034、"alpha" は D-037)。既定は "auto"。</summary>
    public static readonly IReadOnlyList<string> ValidWhiteModes = new[] { "none", "auto", "magic", "opaque", "silhouette", "alpha" };

    /// <summary>サポートするハーフトーンの内部識別子(DOMAIN §4.2.1)。既定は "none"。
    /// Raster.cs 内部の同名リストと値を揃えてある(呼び出し側の入力検証・
    /// エラーメッセージ用。Raster.cs 自体は変更しない)。</summary>
    public static readonly IReadOnlyList<string> ValidHalftones = new[] { "none", "halftone", "coarse_halftone" };

    /// <summary>「塗る範囲で決まるインク」(D-048)に、ジョブごとに指定できる
    /// 塗る範囲の識別子。"none"(既定。プレーンを作らない)/ "artwork"
    /// (純白 255,255,255 でない画素すべて)/ "full"(全画素)。
    /// 参照実装は ref/foilwright_ref/job.py の VALID_COVERAGE_MODES。</summary>
    public static readonly IReadOnlyList<string> ValidCoverageModes = new[] { "none", "artwork", "full" };

    /// <summary>画像とパレットから、実際にジョブへ含めるインクとその
    /// プレーンを、パレットの実行順(order 昇順、同値は記述順 — DOMAIN §4.3 /
    /// §4.9。ConfigLoader.LoadPalette が既にこの順で返す)で返す。
    ///
    /// inkMode: "auto" または "spot_only"。"per_page" は複数ページ入力を
    ///     要するためここでは扱わない(呼び出し側が事前に弾く)。
    ///
    /// whiteMode: "none"(白のプレーンを作らない)/ "auto"(既定。他インクが
    ///     乗る画素の和集合を白にする = auto_undercoat 相当)/ "magic"
    ///     (白の magic_rgb に一致した画素だけを白にする)/ "opaque"
    ///     (純白 255,255,255 でない画素すべてを白にする。magic_rgb への
    ///     直接一致分も auto と同じく足す)のいずれか(DOMAIN §7.1 / D-027、
    ///     "opaque" は D-032)。パレットの auto_undercoat フラグを上書きする。
    ///
    /// 中身の無いプレーン(1 ドットも立っていない)のインクは戻り値から
    /// 除外する。全インクが空なら空リストを返す(呼び出し側はこれを見て
    /// 送出をスキップする)。
    ///
    /// colourCorrection: "none"/"plain"/"photo"(Raster.ToPlanes / ToPlanesAuto
    ///     と同じ)。既定は "photo"(D-029: 実物のフルカラー原稿で colcorPlain
    ///     の完全な下色除去が紫・緑・茶を黒一色に潰した実測を受けての決定)。
    /// resolution / photoLutPath: colourCorrection == "photo" のときだけ参照
    ///     する。Raster.ToPlanes / ToPlanesAuto にそのまま渡す。
    ///
    /// alphaImage: 同じページを Ghostscript の pngalpha デバイスで別途変換した
    ///     結果(D-037)。whiteMode == "alpha" のときだけ参照する。whiteMode ==
    ///     "alpha" で null なら ArgumentException、image と幅・高さが食い違う
    ///     場合も ArgumentException(色は image の ppmraw、白は alphaImage の
    ///     アルファと役割が分かれているため、同じページ・同じ解像度でなければ
    ///     ならない)。
    ///
    /// coverageModes: インク名 → "none" / "artwork" / "full"(D-048)。パレットで
    ///     coverage: true になっているインクだけに効き、それ以外のインクは
    ///     ここに何が書かれていても一切影響を受けない。辞書に無いインク、
    ///     および "none" のインクはプレーンを作らない — したがって既定
    ///     (null)なら D-048 以前と出力バイトが完全に一致する。知らない値は
    ///     白版モードと同じく ArgumentException(黙って "none" に落とさない)。</summary>
    public static List<(InkDefinition Ink, byte[] Plane)> BuildJobPlanes(
        PpmImage image, IReadOnlyList<InkDefinition> palette, string inkMode, string halftone = "none", string whiteMode = "auto",
        string colourCorrection = "photo", int resolution = 600, string? photoLutPath = null, PngImage? alphaImage = null,
        IReadOnlyDictionary<string, string>? coverageModes = null)
    {
        if (!ValidWhiteModes.Contains(whiteMode))
        {
            throw new ArgumentException($"unknown white mode '{whiteMode}'; expected one of {string.Join(", ", ValidWhiteModes)}");
        }

        if (coverageModes is not null && coverageModes.Count > 0)
        {
            // ラスタ処理の前に、辞書の全項目を検証する。coverage でないインクを
            // 指す項目(後で無視される)も含めて弾く — 呼び出し側の書き間違いで
            // あり、黙って "none" に落とさない(D-048)。
            foreach (var (inkName, mode) in coverageModes)
            {
                if (!ValidCoverageModes.Contains(mode))
                {
                    throw new ArgumentException(
                        $"unknown coverage mode '{mode}' for ink '{inkName}'; expected one of {string.Join(", ", ValidCoverageModes)}");
                }
            }
        }

        if (whiteMode == "alpha")
        {
            // 早期に検証する(D-037): alphaImage は呼び出し側が別途 Ghostscript の
            // pngalpha で作っておく必要がある。
            if (alphaImage is null)
            {
                throw new ArgumentException("white mode 'alpha' requires alphaImage (D-037)");
            }
            if (alphaImage.Width != image.Width || alphaImage.Height != image.Height)
            {
                throw new ArgumentException(
                    $"alphaImage dimensions {alphaImage.Width}x{alphaImage.Height} do not match image dimensions {image.Width}x{image.Height}");
            }
        }

        var adjustedPalette = ApplyWhiteMode(palette, whiteMode);

        Dictionary<string, byte[]> planes = inkMode switch
        {
            "auto" => BuildAutoPlanes(image, adjustedPalette, halftone, colourCorrection, resolution, photoLutPath),
            "spot_only" => Raster.ToPlanesMagic(image, adjustedPalette),
            "per_page" => throw new ArgumentException(
                "ink mode 'per_page' needs multiple page inputs; the caller must reject it before calling BuildJobPlanes"),
            _ => throw new ArgumentException($"unknown ink mode '{inkMode}'; expected one of {string.Join(", ", ValidInkModes)}"),
        };

        if (whiteMode == "opaque")
        {
            // Raster.cs(golden 検証済み)には手を入れない。ApplyWhiteMode が
            // opaque でも white インクの AutoUndercoat を false にしているため、
            // ここまでの planes には white インクの magic_rgb 直接一致分だけが
            // 入っている(D-032: auto と同じく直接一致分も足す、を満たす基礎)。
            // 純白でない画素の分は画像から直接計算して OR で足し込む。
            planes = ApplyOpaqueWhite(image, palette, planes);
        }

        if (whiteMode == "silhouette")
        {
            // ApplyOpaqueWhite と同じ理屈だが、マスクは ComputeSilhouettePlane
            // (D-034)から得る。ComputeNonWhitePixelPlane とは別のアルゴリズム
            // (スキャンライン塗りつぶし)で計算し、突き合わせテストの網を強くする。
            planes = ApplySilhouetteWhite(image, palette, planes);
        }

        if (whiteMode == "alpha")
        {
            // ApplyOpaqueWhite/ApplySilhouetteWhite と同じ理屈だが、マスクは
            // alphaImage(image とは別のページレンダリング)の alpha チャンネル
            // から得る(D-037)。alphaImage の非 null・寸法一致は関数冒頭で検証済み。
            planes = ApplyAlphaWhite(alphaImage!, palette, planes);
        }

        if (coverageModes is not null && coverageModes.Count > 0)
        {
            // D-048: 白版モードとは別の仕組みとして併存させる(統合しない)。
            // 白版モードの補助関数と同じく、元のパレットを走査するので
            // coverage インクは下の一覧化で自分の order の位置に入る。
            planes = ApplyCoverageModes(image, palette, planes, coverageModes);
        }

        // 結果の一覧は常に元のパレット(白版モードで除外する前)を走査する。
        // "none" で除外したインクは adjustedPalette 側になく、planes 辞書にも
        // キーが無いため、下の TryGetValue が自然に false を返して除外される
        // (白版モード専用の特別扱いを増やさない)。
        var result = new List<(InkDefinition, byte[])>();
        foreach (var ink in palette)
        {
            if (!planes.TryGetValue(ink.Name, out var plane))
            {
                continue;
            }
            if (!PlaneHasContent(plane))
            {
                continue;
            }
            result.Add((ink, plane));
        }
        return result;
    }

    /// <summary>白版モード(DOMAIN §7.1 / D-027)を反映したパレットを作る。
    ///
    /// 「白」はパレットで auto_undercoat: true になっているインクとして
    /// 判別する(名前を決め打ちしない)。該当インクが 0 個または 2 個以上
    /// (2 個以上は Raster.ToPlanesMagic/ToPlanesAuto が例外にする構成)の
    /// 場合は、白版モードの適用対象が定まらないためパレットをそのまま返す。
    ///
    ///   - "none": 白インクをパレットから完全に除外する。マジックカラーの
    ///     直接一致も含め、一切プレーンを作らせない。
    ///   - "auto": 白インクの auto_undercoat を true に強制する(パレット側の
    ///     値が false でも、設定が上書きする)。
    ///   - "magic": 白インクの auto_undercoat を false に強制する。マジック
    ///     カラーへの直接一致分のみが白になる。
    ///   - "opaque"(D-032): 白インクの auto_undercoat を false に強制する
    ///     (magic と同じ)。他インクの和集合ではなく「純白でない画素すべて」
    ///     が白になるべきなので、Raster.cs 側の和集合ロジックは使わない。
    ///     マジックカラーへの直接一致分はここで magic と同様に確保しておき、
    ///     純白でない画素の分は呼び出し元(BuildJobPlanes)が画像から直接
    ///     計算して OR で足し込む(ApplyOpaqueWhite)。</summary>
    private static List<InkDefinition> ApplyWhiteMode(IReadOnlyList<InkDefinition> palette, string whiteMode)
    {
        var whiteInks = palette.Where(ink => ink.AutoUndercoat).ToList();
        if (whiteInks.Count != 1)
        {
            return palette.ToList();
        }
        var whiteInk = whiteInks[0];

        return whiteMode switch
        {
            "none" => palette.Where(ink => !ReferenceEquals(ink, whiteInk)).ToList(),
            "auto" => palette.Select(ink => ReferenceEquals(ink, whiteInk) ? WithAutoUndercoat(ink, true) : ink).ToList(),
            "magic" or "opaque" or "silhouette" or "alpha" => palette.Select(ink => ReferenceEquals(ink, whiteInk) ? WithAutoUndercoat(ink, false) : ink).ToList(),
            _ => throw new ArgumentException($"unknown white mode '{whiteMode}'; expected one of {string.Join(", ", ValidWhiteModes)}"),
        };
    }

    /// <summary>白版モード "opaque"(DOMAIN §7.1 / D-032)を適用する。
    ///
    /// 「白」はパレットで auto_undercoat: true になっているインクとして
    /// 判別する(ApplyWhiteMode と同じ規則。該当が 0 個または 2 個以上なら
    /// 白版モードの適用対象が定まらないため何もしない)。
    ///
    /// 純白(255,255,255)の画素だけは対象から除く — DOMAIN §6.1 の約束
    /// 「白は純白 255,255,255 ではない。255 は印刷しない領域」による。
    /// 純白まで白にすると、原稿の余白(印刷しない領域)ごと紙全体が白で
    /// 埋まってしまう。</summary>
    private static Dictionary<string, byte[]> ApplyOpaqueWhite(
        PpmImage image, IReadOnlyList<InkDefinition> palette, Dictionary<string, byte[]> planes)
    {
        var whiteInks = palette.Where(ink => ink.AutoUndercoat).ToList();
        if (whiteInks.Count != 1)
        {
            return planes;
        }
        var whiteInk = whiteInks[0];

        byte[] opaquePlane = ComputeNonWhitePixelPlane(image);

        if (planes.TryGetValue(whiteInk.Name, out var existing))
        {
            var merged = (byte[])existing.Clone();
            for (int i = 0; i < merged.Length; i++)
            {
                merged[i] |= opaquePlane[i];
            }
            planes[whiteInk.Name] = merged;
        }
        else
        {
            planes[whiteInk.Name] = opaquePlane;
        }

        return planes;
    }

    /// <summary>純白(255,255,255)でない画素すべてにビットを立てた 1bit プレーンを
    /// 作る(DOMAIN §6.1 / D-032)。ビット順・行バイト数は Raster.cs の
    /// ToPlanesMagic/ToPlanesAuto と同じ規則(MSB 先頭、(width+7)/8 バイト/行)。</summary>
    private static byte[] ComputeNonWhitePixelPlane(PpmImage image)
    {
        int width = image.Width, height = image.Height;
        byte[] pixels = image.Pixels;
        int rowBytes = (width + 7) / 8;
        var plane = new byte[rowBytes * height];

        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width * 3;
            int planeRowBase = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                int idx = rowBase + x * 3;
                byte r = pixels[idx], g = pixels[idx + 1], b = pixels[idx + 2];
                if (r == 255 && g == 255 && b == 255)
                {
                    // 純白は「印刷しない領域」(DOMAIN §6.1)。opaque の対象外。
                    continue;
                }
                int byteIndex = planeRowBase + (x >> 3);
                int bitMask = 0x80 >> (x & 7);
                plane[byteIndex] |= (byte)bitMask;
            }
        }

        return plane;
    }

    /// <summary>白版モード "silhouette"(DOMAIN §7.1 / D-034)を適用する。
    ///
    /// 「白」はパレットで auto_undercoat: true になっているインクとして
    /// 判別する(ApplyWhiteMode/ApplyOpaqueWhite と同じ規則。該当が 0 個または
    /// 2 個以上なら白版モードの適用対象が定まらないため何もしない)。</summary>
    private static Dictionary<string, byte[]> ApplySilhouetteWhite(
        PpmImage image, IReadOnlyList<InkDefinition> palette, Dictionary<string, byte[]> planes)
    {
        var whiteInks = palette.Where(ink => ink.AutoUndercoat).ToList();
        if (whiteInks.Count != 1)
        {
            return planes;
        }
        var whiteInk = whiteInks[0];

        byte[] silhouettePlane = ComputeSilhouettePlane(image);

        if (planes.TryGetValue(whiteInk.Name, out var existing))
        {
            var merged = (byte[])existing.Clone();
            for (int i = 0; i < merged.Length; i++)
            {
                merged[i] |= silhouettePlane[i];
            }
            planes[whiteInk.Name] = merged;
        }
        else
        {
            planes[whiteInk.Name] = silhouettePlane;
        }

        return planes;
    }

    /// <summary>紙の四辺から純白(255,255,255)だけを 4 近傍で辿って
    /// 到達できない画素すべてにビットを立てた 1bit プレーンを作る
    /// (DOMAIN §6.1 / §7.1 / D-034)。到達できた純白は「紙の背景」
    /// (印刷しない領域)、到達できない純白(絵に囲まれた穴)は白版の対象。
    /// ビット順・行バイト数は ComputeNonWhitePixelPlane と同じ規則。
    ///
    /// アルゴリズムはスキャンライン方式の塗りつぶし(横方向の連続区間を
    /// まとめて処理する)。ref/ の compute_silhouette_plane は同じ集合を
    /// キューを使った素直な 4 近傍探索(1 画素ずつ)で計算しており、
    /// アルゴリズムをあえて変えて突き合わせの網を強くしている(D-034)。
    /// 再帰は使わない(A4 600dpi = 約 3,060 万画素でスタック溢れを避ける)。</summary>
    private static byte[] ComputeSilhouettePlane(PpmImage image)
    {
        int width = image.Width, height = image.Height;
        bool[] reached = FloodFillFromEdges(image);

        int rowBytes = (width + 7) / 8;
        var plane = new byte[rowBytes * height];

        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width;
            int planeRowBase = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                if (!reached[rowBase + x])
                {
                    int byteIndex = planeRowBase + (x >> 3);
                    int bitMask = 0x80 >> (x & 7);
                    plane[byteIndex] |= (byte)bitMask;
                }
            }
        }

        return plane;
    }

    /// <summary>紙の四辺の純白画素を種として、スキャンライン方式(横方向の
    /// 連続区間ごとにまとめて処理する塗りつぶし)で純白の連結領域を求める。
    /// 戻り値は画素ごとの到達可否(true = 紙の背景として到達できた)。
    /// 種は画素単位ではなく区間単位でスタックに積むため、全画素を
    /// キューに積む実装(Queue&lt;int&gt; に全画素)より少ないスタック消費で済む。</summary>
    private static bool[] FloodFillFromEdges(PpmImage image)
    {
        int width = image.Width, height = image.Height;
        byte[] pixels = image.Pixels;
        var reached = new bool[width * height];

        bool IsPureWhite(int x, int y)
        {
            int idx = (y * width + x) * 3;
            return pixels[idx] == 255 && pixels[idx + 1] == 255 && pixels[idx + 2] == 255;
        }

        var stack = new Stack<(int X, int Y)>();

        void Seed(int x, int y)
        {
            int i = y * width + x;
            if (!reached[i] && IsPureWhite(x, y))
            {
                stack.Push((x, y));
            }
        }

        for (int x = 0; x < width; x++)
        {
            Seed(x, 0);
            if (height > 1)
            {
                Seed(x, height - 1);
            }
        }
        for (int y = 0; y < height; y++)
        {
            Seed(0, y);
            if (width > 1)
            {
                Seed(width - 1, y);
            }
        }

        while (stack.Count > 0)
        {
            var (sx, sy) = stack.Pop();
            int seedIndex = sy * width + sx;
            if (reached[seedIndex])
            {
                // 別の区間から先に埋められていた種(スキャン中に重複して
                // 積まれることがある)。
                continue;
            }

            // 横方向に左右へ伸ばして、この行の連続する純白区間を確定する。
            int xLeft = sx;
            while (xLeft > 0 && !reached[sy * width + (xLeft - 1)] && IsPureWhite(xLeft - 1, sy))
            {
                xLeft--;
            }
            int xRight = sx;
            while (xRight < width - 1 && !reached[sy * width + (xRight + 1)] && IsPureWhite(xRight + 1, sy))
            {
                xRight++;
            }

            int rowBase = sy * width;
            for (int x = xLeft; x <= xRight; x++)
            {
                reached[rowBase + x] = true;
            }

            // 上下の隣接行を、確定した区間の幅の範囲内だけ走査し、未到達の
            // 純白の連続区間ごとに 1 個だけ種を積む(区間内の残りは、その種を
            // 処理するときに上の左右伸長でまとめて埋まる)。
            ScanNeighbourRow(pixels, reached, width, height, xLeft, xRight, sy - 1, stack);
            ScanNeighbourRow(pixels, reached, width, height, xLeft, xRight, sy + 1, stack);
        }

        return reached;
    }

    /// <summary>FloodFillFromEdges の補助: 隣接行 ny のうち [xLeft, xRight]
    /// の範囲だけを走査し、未到達の純白の連続区間ごとに種を 1 個積む。</summary>
    private static void ScanNeighbourRow(
        byte[] pixels, bool[] reached, int width, int height, int xLeft, int xRight, int ny, Stack<(int X, int Y)> stack)
    {
        if (ny < 0 || ny >= height)
        {
            return;
        }

        bool IsPureWhite(int x, int y)
        {
            int idx = (y * width + x) * 3;
            return pixels[idx] == 255 && pixels[idx + 1] == 255 && pixels[idx + 2] == 255;
        }

        int rowBase = ny * width;
        int x = xLeft;
        while (x <= xRight)
        {
            if (!reached[rowBase + x] && IsPureWhite(x, ny))
            {
                stack.Push((x, ny));
                // この区間の残りは種を処理するときにまとめて埋まるため、
                // 同じ区間内で種を重複して積まないよう先へ進める。
                while (x <= xRight && !reached[rowBase + x] && IsPureWhite(x, ny))
                {
                    x++;
                }
            }
            else
            {
                x++;
            }
        }
    }

    /// <summary>白版モード "alpha"(DOMAIN §7.1 / D-037)を適用する。
    ///
    /// 「白」はパレットで auto_undercoat: true になっているインクとして
    /// 判別する(ApplyOpaqueWhite/ApplySilhouetteWhite と同じ規則。該当が
    /// 0 個または 2 個以上なら白版モードの適用対象が定まらないため何もしない)。
    ///
    /// マスクは image(色の元になった ppmraw)ではなく、別途 Ghostscript の
    /// pngalpha で変換した alphaImage から得る(D-037: 色とアルファは別の
    /// レンダリング結果)。</summary>
    private static Dictionary<string, byte[]> ApplyAlphaWhite(
        PngImage alphaImage, IReadOnlyList<InkDefinition> palette, Dictionary<string, byte[]> planes)
    {
        var whiteInks = palette.Where(ink => ink.AutoUndercoat).ToList();
        if (whiteInks.Count != 1)
        {
            return planes;
        }
        var whiteInk = whiteInks[0];

        byte[] alphaPlane = ComputeAlphaPlane(alphaImage);

        if (planes.TryGetValue(whiteInk.Name, out var existing))
        {
            var merged = (byte[])existing.Clone();
            for (int i = 0; i < merged.Length; i++)
            {
                merged[i] |= alphaPlane[i];
            }
            planes[whiteInk.Name] = merged;
        }
        else
        {
            planes[whiteInk.Name] = alphaPlane;
        }

        return planes;
    }

    /// <summary>アルファが 0 でない画素すべてにビットを立てた 1bit プレーンを
    /// 作る(DOMAIN §7.1 / D-037)。ビット順・行バイト数は ComputeNonWhitePixelPlane/
    /// ComputeSilhouettePlane と同じ規則(MSB 先頭、(width+7)/8 バイト/行)。
    /// alphaImage.Pixels は行優先・1 画素あたり R/G/B/A の 4 バイト(PngImage)。</summary>
    private static byte[] ComputeAlphaPlane(PngImage alphaImage)
    {
        int width = alphaImage.Width, height = alphaImage.Height;
        byte[] pixels = alphaImage.Pixels;
        int rowBytes = (width + 7) / 8;
        var plane = new byte[rowBytes * height];

        for (int y = 0; y < height; y++)
        {
            int rowBase = y * width * 4;
            int planeRowBase = y * rowBytes;
            for (int x = 0; x < width; x++)
            {
                byte alpha = pixels[rowBase + x * 4 + 3];
                if (alpha == 0)
                {
                    continue;
                }
                int byteIndex = planeRowBase + (x >> 3);
                int bitMask = 0x80 >> (x & 7);
                plane[byteIndex] |= (byte)bitMask;
            }
        }

        return plane;
    }

    /// <summary>InkDefinition は record ではないため with 式が使えない。
    /// AutoUndercoat のみを差し替えた複製を作る。</summary>
    private static InkDefinition WithAutoUndercoat(InkDefinition ink, bool autoUndercoat) => new()
    {
        Name = ink.Name,
        Label = ink.Label,
        PrinterCode = ink.PrinterCode,
        Order = ink.Order,
        MagicRgb = ink.MagicRgb,
        Tolerance = ink.Tolerance,
        Channel = ink.Channel,
        Barcode = ink.Barcode,
        Coverage = ink.Coverage,
        AutoUndercoat = autoUndercoat,
        Passes = ink.Passes,
    };

    /// <summary>「塗る範囲で決まるインク」(D-048)のうち、モードが "none" 以外の
    /// ものにプレーンを足す。
    ///
    /// coverage インクは magic_rgb も channel も持てない(ConfigLoader が弾く)ため
    /// Raster.cs は一切プレーンを作っておらず、合成する相手がいない — ここで
    /// 計算したものをそのまま入れる。
    ///
    /// coverage でないインクは coverageModes に何が書かれていても触らない。
    /// 辞書に無いインク・"none" のインクはプレーンを作らない(何も渡さなければ
    /// D-048 以前と 1 バイトも変わらない、の実体がここ)。
    ///
    /// ハーフトーンも色補正も掛けない — 画素ごとにオンかオフだけ
    /// (D-048 決定 4 / ppmtomd man:564-565)。</summary>
    private static Dictionary<string, byte[]> ApplyCoverageModes(
        PpmImage image, IReadOnlyList<InkDefinition> palette, Dictionary<string, byte[]> planes,
        IReadOnlyDictionary<string, string> coverageModes)
    {
        foreach (var ink in palette)
        {
            if (!ink.Coverage)
            {
                continue;
            }
            if (!coverageModes.TryGetValue(ink.Name, out var mode))
            {
                mode = "none";
            }
            switch (mode)
            {
                case "none":
                    break;
                case "artwork":
                    // 白版モード "opaque" と同じ判定。純白の判定を 2 か所に
                    // 書かないよう、同じ関数を使い回す。
                    planes[ink.Name] = ComputeNonWhitePixelPlane(image);
                    break;
                case "full":
                    planes[ink.Name] = ComputeFullCoveragePlane(image.Width, image.Height);
                    break;
                default:
                    // BuildJobPlanes が先に検証しているのでここへは来ない。
                    throw new ArgumentException(
                        $"unknown coverage mode '{mode}' for ink '{ink.Name}'; expected one of {string.Join(", ", ValidCoverageModes)}");
            }
        }
        return planes;
    }

    /// <summary>width x height の全画素にビットを立てた 1bit プレーンを作る
    /// (D-048 の "full")。行末の余りビット(width 以降)は 0 のままにする —
    /// 他のプレーン生成と同じ規則であり、ここを埋めると ref/ と出力バイトが
    /// 食い違う。</summary>
    private static byte[] ComputeFullCoveragePlane(int width, int height)
    {
        int rowBytes = (width + 7) / 8;
        var row = new byte[rowBytes];
        for (int x = 0; x < width; x++)
        {
            row[x >> 3] |= (byte)(0x80 >> (x & 7));
        }

        var plane = new byte[rowBytes * height];
        for (int y = 0; y < height; y++)
        {
            Array.Copy(row, 0, plane, y * rowBytes, rowBytes);
        }
        return plane;
    }

    /// <summary>"auto" 方式のプレーンを組み立てる。cmyk_map はコードに
    /// 埋め込まず、パレットの channel フィールドから導出する(D-019 / DOMAIN
    /// §4.5)。magic_rgb と channel の両方を持つ二役インク(既定パレットの
    /// black)は、Raster.ToPlanesAuto の内部では特色側のプレーンが CMYK 側で
    /// 上書きされてしまう(D-019 補足)ため、内部専用の一時キーで CMYK 側を
    /// 受け取ってから OR で合成する。</summary>
    private static Dictionary<string, byte[]> BuildAutoPlanes(
        PpmImage image, IReadOnlyList<InkDefinition> palette, string halftone,
        string colourCorrection, int resolution, string? photoLutPath)
    {
        var cmykMap = new Dictionary<string, string>();
        var twoRoleTempNames = new Dictionary<string, string>(); // 一時キー -> 実際のインク名

        foreach (var ink in palette)
        {
            if (ink.Channel is null)
            {
                continue;
            }
            if (ink.MagicRgb is not null)
            {
                // 二役インク: CMYK 側を一時キーで受け、後で特色側のプレーンへ OR 合成する。
                string tempName = ink.Name + "\0__cmyk_dup";
                cmykMap[ink.Channel] = tempName;
                twoRoleTempNames[tempName] = ink.Name;
            }
            else
            {
                cmykMap[ink.Channel] = ink.Name;
            }
        }

        var raw = Raster.ToPlanesAuto(image, palette, cmykMap, halftone, colourCorrection, resolution, photoLutPath);

        var merged = new Dictionary<string, byte[]>();
        foreach (var (name, buf) in raw)
        {
            if (twoRoleTempNames.ContainsKey(name))
            {
                continue;
            }
            merged[name] = buf;
        }

        foreach (var (tempName, actualName) in twoRoleTempNames)
        {
            byte[] cmykBuf = raw[tempName];
            // spotInks は常に自分自身のキーでゼロ初期化済みプレーンを持つため
            // (Raster.ToPlanesAuto)、merged[actualName] は必ず存在する。
            byte[] spotBuf = merged[actualName];
            for (int i = 0; i < spotBuf.Length; i++)
            {
                spotBuf[i] |= cmykBuf[i];
            }
        }

        return merged;
    }

    /// <summary>プレーンに 1 ドットでも立っているかを調べる。</summary>
    public static bool PlaneHasContent(byte[] plane)
    {
        foreach (byte b in plane)
        {
            if (b != 0)
            {
                return true;
            }
        }
        return false;
    }
}
