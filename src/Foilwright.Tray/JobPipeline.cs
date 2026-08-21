// Foilwright.Tray — ジョブの変換(Ghostscript → 切り出し → プレーン分解)と
// 送出をまとめる層。PreviewForm から呼ばれる。
//
// Foilwright.Cli.Program の HandleJob と同じ流れを踏襲しているが、Cli の
// private メンバーは参照できない(別エージェントが編集中のため無変更)ので
// ここに複製してある。パス数・使用インク・順序を UI に渡す点、送出を
// 独立したメソッドに分けてある点が Cli との違い(プレビューと送出を
// 分離するため)。

using Foilwright.Core;

namespace Foilwright.Tray;

public sealed class JobConfig
{
    public required ProfileSpec Profile { get; init; }
    public required PaperSpec Paper { get; init; }
    public required MediaSpec Media { get; init; }
    public required List<InkDefinition> Palette { get; init; }
}

/// <summary>プレビュー用のインク 1 件分の要約(§7.2「ジョブ内容の表示」)。</summary>
public sealed class InkPreviewInfo
{
    public required string Name { get; init; }
    public required string Label { get; init; }
    public required int Order { get; init; }
    public required int Passes { get; init; }
    public required int PrinterCode { get; init; }
    public required System.Drawing.Color Color { get; init; }
}

public sealed class PreviewResult : IDisposable
{
    public required System.Drawing.Bitmap Preview { get; init; }
    public required IReadOnlyList<InkPreviewInfo> Inks { get; init; }
    public required int Width { get; init; }
    public required int Height { get; init; }

    /// <summary>このプレビューを作った際に解決した解像度。Emitter.PrintJob.Resolution
    /// に渡す値(ResolutionEntry.DpiX)はこれを使う — Print() を呼ぶ側が
    /// 再度プロファイルを読んで解決し直す必要をなくす。</summary>
    public required ResolutionEntry Resolution { get; init; }

    /// <summary>Emitter.EmitJob にそのまま渡せるプレーン。</summary>
    public required Dictionary<string, byte[]> Planes { get; init; }

    /// <summary>Emitter.EmitJob にそのまま渡せる印刷順のインク一覧。</summary>
    public required List<JobInk> JobInks { get; init; }

    /// <summary>ジョブが実際に使うインクの定義一覧(Barcode を含む)。
    /// カセットの過不足判定(§7.3 / D-026 / CassetteCheck)に渡す。</summary>
    public required IReadOnlyList<InkDefinition> RequiredInks { get; init; }

    /// <summary>Ghostscript で変換し、用紙寸法で切り出し済みの画像。D-028 補足:
    /// インク除外の切り替えでは Ghostscript を再実行せず、この画像を保持した
    /// まま <see cref="JobPipeline.RebuildFromImage"/> でジョブ組み立てだけを
    /// やり直す。</summary>
    public required PpmImage Image { get; set; }

    /// <summary>このプレビューを組み立てた際のジョブ設定(パレット・用紙・
    /// メディア・プロファイル)。RebuildFromImage の再呼び出しに必要。</summary>
    public required JobConfig Config { get; set; }

    /// <summary>白版モードが "alpha" のときだけ Ghostscript の pngalpha で
    /// 変換し、用紙寸法で切り出し済みの画像(D-037)。alpha 以外のモードでは
    /// null(Ghostscript を pngalpha で走らせない)。D-028 補足と同じ理屈で、
    /// インク除外やパス数の切り替え(RebuildFromImage)では Ghostscript を
    /// 再実行せずこれを保持したまま使い回す。</summary>
    public PngImage? AlphaImage { get; set; }

    /// <summary>Bitmap(GDI ハンドル)と、切り出し済み画像・プレーン(管理ヒープ上の
    /// 大きなバイト配列。A4/600dpi で約 68MB、1200x600 で約 137MB)を解放する。
    /// 古いプレビューを差し替える際は必ずこれを呼ぶこと(DOMAIN §7.2 補足)。</summary>
    public void Dispose()
    {
        Preview.Dispose();
        Planes.Clear();
        Image = null!;
        Config = null!;
        AlphaImage = null;
    }
}

public static class JobPipeline
{
    // Foilwright.Cli.Program の既定値と同じ(D-024 のトレイアプリ設定既定値)。
    public const string DefaultResolutionKey = "600";
    public const string DefaultPaperName = "a4";
    public const string DefaultMediaName = "plain_paper";
    public const string DefaultHalftone = "none";
    public const string DefaultWhiteMode = "auto";

    // D-029: 色補正の既定は photo。photo_colcor テーブルはリポジトリ直下
    // colour/photo_colcor.bin に同梱してある(D-029 §3)。
    public const string DefaultColourCorrection = "photo";

    private const int PreviewMaxWidth = 900;

    public static JobConfig LoadJobConfig(string assetRoot, MachineRoute route, string paperName, string mediaName)
    {
        var profile = ConfigLoader.LoadProfile(Path.Combine(assetRoot, "profiles", route.ProfileFileName));
        var paperTable = ConfigLoader.ResolvePaperTable(profile, Path.Combine(assetRoot, "papers"));
        if (!paperTable.TryGetValue(paperName, out var paper))
        {
            throw new ConfigException($"paper '{paperName}' not found in paper table '{profile.PaperTable}'");
        }
        var mediaTable = ConfigLoader.LoadMediaTable(Path.Combine(assetRoot, "media.yaml"));
        if (!mediaTable.TryGetValue(mediaName, out var media))
        {
            throw new ConfigException($"media '{mediaName}' not found in media.yaml");
        }
        var palette = ConfigLoader.LoadPalette(Path.Combine(assetRoot, "palette", "default.yaml"));
        return new JobConfig { Profile = profile, Paper = paper, Media = media, Palette = palette };
    }

    /// <summary>PostScript ファイルを変換し、プレビュー用のビットマップと
    /// ジョブ情報を組み立てる。送出は一切行わない(UI 側が確認するまで印刷は
    /// 始まってはならない。DOMAIN §7.2)。
    ///
    /// resolutionKey: ResolutionEntry.Key の形式(例: "600" / "1200x600")。
    /// プロファイルの resolutions から解決する(DOMAIN §4.5: コードに埋め込まない)。</summary>
    public static PreviewResult BuildPreview(
        string psPath, string assetRoot, MachineRoute route, string inkMode,
        string paperName, string mediaName, string resolutionKey, string halftone, string whiteMode,
        IReadOnlySet<string> usedInks, IReadOnlyDictionary<string, int> passesOverride,
        string colourCorrection = DefaultColourCorrection,
        IReadOnlyDictionary<string, int[]?>? magicRgbOverride = null,
        IReadOnlyDictionary<string, string>? coverageModes = null)
    {
        var config = LoadJobConfig(assetRoot, route, paperName, mediaName);
        var resolutionEntry = config.Profile.ResolveResolutionByKey(resolutionKey);
        string ppmPath = Path.Combine(Path.GetTempPath(), $"foilwright_{Guid.NewGuid():n}.ppm");
        // 白版モードが "alpha" のときだけ使う(D-037)。他のモードではここが
        // null のままで、pngPath も作られない -- Ghostscript を pngalpha で
        // 走らせるのは alpha を選んだときだけという制約をここで守る。
        string? pngPath = whiteMode == "alpha"
            ? Path.Combine(Path.GetTempPath(), $"foilwright_{Guid.NewGuid():n}.png")
            : null;
        try
        {
            Ghostscript.ConvertToPpm(psPath, ppmPath, resolutionEntry.DpiX, resolutionEntry.DpiY);
            var fullImage = PpmImage.Read(ppmPath);
            // 用紙表は 600dpi 基準のため、選んだ解像度へ換算してから切り出す
            // (DOMAIN §7.1: 1200x600 は幅方向だけ 2 倍)。
            var scaledPaper = config.Paper.ScaleToResolution(resolutionEntry.DpiX, resolutionEntry.DpiY);
            var image = fullImage.Crop(scaledPaper.LeftMargin, scaledPaper.TopMargin, scaledPaper.Width, scaledPaper.Length);

            PngImage? alphaImage = null;
            if (pngPath is not null)
            {
                // D-037: 白版モード alpha のときだけ、色(ppmraw)の変換に加えて
                // pngalpha でもう 1 回変換する。切り出しは色(image)と同じ
                // scaledPaper を使う(制約: 色とアルファで食い違わせない)。
                Ghostscript.ConvertToPngAlpha(psPath, pngPath, resolutionEntry.DpiX, resolutionEntry.DpiY);
                var fullAlphaImage = PngImage.Read(pngPath);
                alphaImage = CropAlpha(fullAlphaImage, scaledPaper.LeftMargin, scaledPaper.TopMargin, scaledPaper.Width, scaledPaper.Length);
            }

            return BuildPreviewCore(image, config, resolutionEntry, inkMode, halftone, whiteMode, usedInks, passesOverride, colourCorrection, alphaImage, magicRgbOverride, coverageModes);
        }
        finally
        {
            TryDelete(ppmPath);
            if (pngPath is not null)
            {
                TryDelete(pngPath);
            }
        }
    }

    /// <summary>PngImage(RGBA)を、色の切り出し(PpmImage.Crop)と同じ規則で
    /// 切り出す(D-037: 切り出しを色とアルファで食い違わせない)。PngImage
    /// 自体には Crop を持たせない(D-036 の対象外。Ghostscript の pngalpha
    /// 出力を読むだけの最小デコーダに留める)。Foilwright.Cli.Program の
    /// 同名ヘルパーと同じ実装(別プロジェクトのため複製 -- このファイル冒頭の
    /// コメントの流儀どおり)。</summary>
    private static PngImage CropAlpha(PngImage image, int x, int y, int width, int height)
    {
        if (x < 0 || y < 0)
        {
            throw new ArgumentException($"crop origin must be non-negative, got ({x}, {y})");
        }
        if (width < 0 || height < 0)
        {
            throw new ArgumentException($"crop size must be non-negative, got ({width}, {height})");
        }

        int availableWidth = Math.Max(0, image.Width - x);
        int availableHeight = Math.Max(0, image.Height - y);
        int outWidth = Math.Min(width, availableWidth);
        int outHeight = Math.Min(height, availableHeight);

        byte[] outPixels = new byte[outWidth * outHeight * 4];
        int srcRowBytes = image.Width * 4;
        int dstRowBytes = outWidth * 4;
        for (int row = 0; row < outHeight; row++)
        {
            int srcOffset = (y + row) * srcRowBytes + x * 4;
            int dstOffset = row * dstRowBytes;
            Array.Copy(image.Pixels, srcOffset, outPixels, dstOffset, dstRowBytes);
        }

        return new PngImage(outWidth, outHeight, outPixels);
    }

    /// <summary>切り出し済みの画像を保持したまま、ジョブ組み立て(インク割り当て・
    /// プレーン分解・プレビュー描画)だけをやり直す。Ghostscript は再実行しない
    /// (D-028 補足)。プレビュー画面でインクの許可リスト(チェック)を切り替えたときに使う。
    ///
    /// usedInks: D-030 の「そのジョブで使うインクの許可リスト」。ここに
    /// 含まれるインクだけを <paramref name="config"/>.Palette から残して組み立てる
    /// ため、`auto` では許可リストに無いインクへ割り当たるはずだった画素が
    /// そのまま CMYK 分解へ回る(D-028 の「除外 = パレットから外す」の一般形。
    /// プレーンを作ってから捨てるのではない)。</summary>
    public static PreviewResult RebuildFromImage(
        PpmImage image, JobConfig config, ResolutionEntry resolution,
        string inkMode, string halftone, string whiteMode,
        IReadOnlySet<string> usedInks, IReadOnlyDictionary<string, int> passesOverride,
        string colourCorrection = DefaultColourCorrection, PngImage? alphaImage = null,
        IReadOnlyDictionary<string, int[]?>? magicRgbOverride = null,
        IReadOnlyDictionary<string, string>? coverageModes = null)
    {
        return BuildPreviewCore(image, config, resolution, inkMode, halftone, whiteMode, usedInks, passesOverride, colourCorrection, alphaImage, magicRgbOverride, coverageModes);
    }

    /// <summary>BuildPreview と RebuildFromImage の共通処理(インク割り当て以降)。
    /// D-030: usedInks に含まれないインクはパレットから除いてから
    /// JobAssembly.BuildJobPlanes に渡す — プレーンを作ってから捨てるのではない。
    /// D-031: passesOverride にそのインクの上書きがあれば、JobAssembly.BuildJobPlanes
    /// が返す InkDefinition.Passes(パレットの既定値)をここで差し替える。JobInk
    /// (実際に送出へ使う値)と InkPreviewInfo.Passes(プレビュー表示)の両方に
    /// 同じ上書き後の値を使うこと — 表示と実際の出力がずれてはならない。
    /// D-029: colourCorrection == "photo" のとき、photo_colcor テーブル
    /// (colour/photo_colcor.bin、リポジトリ直下から解決)と選択中の解像度を
    /// JobAssembly.BuildJobPlanes へ渡す。ガンマの既定値が解像度で変わるため
    /// (600 は 0.8、1200 は -0.9)、解像度を渡し忘れると色がずれる。
    /// D-042: magicRgbOverride にそのインクの上書きがあれば、パレットの magic_rgb を
    /// 差し替えた「照合用パレット」を作ってから JobAssembly.BuildJobPlanes に渡す
    /// (JobAssembly / raster には手を入れない。D-042 決定 6)。
    /// D-048: coverageModes(ink 名 → none/artwork/full)はそのまま
    /// JobAssembly.BuildJobPlanes へ渡す。パレットで coverage: true のインクだけに効き、
    /// 渡さなければ(null)coverage インクのプレーンは作られない — 使わない人の
    /// 出力は 1 バイトも変わらない(D-048 決定 3)。</summary>
    private static PreviewResult BuildPreviewCore(
        PpmImage image, JobConfig config, ResolutionEntry resolutionEntry,
        string inkMode, string halftone, string whiteMode, IReadOnlySet<string> usedInks,
        IReadOnlyDictionary<string, int> passesOverride, string colourCorrection, PngImage? alphaImage = null,
        IReadOnlyDictionary<string, int[]?>? magicRgbOverride = null,
        IReadOnlyDictionary<string, string>? coverageModes = null)
    {
        var palette = config.Palette.Where(ink => usedInks.Contains(ink.Name)).ToList();

        // D-042: マジックカラーの上書きは「照合」にだけ効かせる。表示色は元のパレットの
        // ままにする — 白を #000000 に割り当てたとき、プレビューまで黒く描いてしまうと
        // 黒インクと見分けが付かず、誤爆を検出できなくなる(DOMAIN §7.2)。
        var rawByName = palette.ToDictionary(ink => ink.Name);
        var matchPalette = TraySettings.ApplyMagicRgbOverride(palette, magicRgbOverride);

        string assetRoot = AssetRoot.ResolveDefault();
        string photoLutPath = Path.Combine(assetRoot, "colour", "photo_colcor.bin");

        var jobPlanes = JobAssembly.BuildJobPlanes(
            image, matchPalette, inkMode, halftone, whiteMode, colourCorrection, resolutionEntry.DpiX, photoLutPath, alphaImage,
            coverageModes);

        var planes = jobPlanes.ToDictionary(jp => jp.Ink.Name, jp => jp.Plane);
        var jobInks = jobPlanes
            .Select(jp => new JobInk
            {
                Name = jp.Ink.Name,
                PrinterCode = jp.Ink.PrinterCode,
                Passes = ResolvePasses(jp.Ink, passesOverride),
            })
            .ToList();
        var inkInfos = jobPlanes
            .Select(jp => new InkPreviewInfo
            {
                Name = jp.Ink.Name,
                Label = jp.Ink.Label,
                Order = jp.Ink.Order,
                Passes = ResolvePasses(jp.Ink, passesOverride),
                PrinterCode = jp.Ink.PrinterCode,
                // D-042: 表示色は上書き前(パレットのまま)のインクから引く。
                Color = PreviewRenderer.ResolveDisplayColor(RawInkOf(jp.Ink, rawByName)),
            })
            .ToList();

        // D-042: 描画にも上書き前のインクを使う(色の対応付けを変えない)。
        // プレーンそのものは上書き後の照合結果であり、差し替えるのは Ink だけ。
        // 並びの組み立ては RenderPreviewBitmap と共有する(同じ処理を 2 箇所に書かない)。
        var displayPlanes = BuildDisplayPlanes(jobPlanes, rawByName, onlyInk: null);

        // D-038: 1200x600 のように画素が正方形でない解像度では、縦横に同じ倍率を
        // かけると縦に潰れて見える(PreviewRenderer 側で dpiX/dpiY を使って補正する)。
        var bitmap = PreviewRenderer.Render(
            image.Width, image.Height, displayPlanes, PreviewMaxWidth, resolutionEntry.DpiX, resolutionEntry.DpiY);

        return new PreviewResult
        {
            Preview = bitmap,
            Inks = inkInfos,
            Width = image.Width,
            Height = image.Height,
            Planes = planes,
            JobInks = jobInks,
            RequiredInks = jobPlanes.Select(jp => jp.Ink).ToList(),
            Resolution = resolutionEntry,
            Image = image,
            Config = config,
            AlphaImage = alphaImage,
        };
    }

    /// <summary>できあがった PreviewResult から、プレビュー画像だけを描き直す。
    /// Ghostscript もジョブ組み立て(JobAssembly.BuildJobPlanes)も走らせない
    /// (プレーンは既にあるものをそのまま使い、描き直すだけである)。
    ///
    /// onlyInk が null なら全インク、インク名を指定するとそのインクだけを描く。
    /// 全インクが重なったままでは「白がどこに乗るのか」「金が意図しない所を
    /// 拾っていないか」が見えず、マジックカラーの誤爆を発見できない
    /// (DOMAIN §7.2: 誤爆を見つける手段はプレビューしかない)。
    ///
    /// **ジョブの中身(Planes / JobInks / RequiredInks)は一切変えない** —
    /// これは見せ方だけの機能であり、表示を絞ったまま印刷しても刷られるものは
    /// 全インクのままである(その旨の注意文は UI 側の PreviewForm.BuildInkFilterNotice)。
    ///
    /// 戻り値は新しい Bitmap。呼び出し側が古いものを破棄すること。</summary>
    public static System.Drawing.Bitmap RenderPreviewBitmap(PreviewResult result, string? onlyInk)
    {
        // D-042: 表示色は上書き前のパレットから引く(BuildPreviewCore と同じ規則)。
        var rawByName = result.Config.Palette.ToDictionary(ink => ink.Name);

        // 並びは RequiredInks の順(= 印刷順)をそのまま保つ。順を崩すと重なりの
        // 見え方(後に刷ったものが上の層)が変わってしまう(DOMAIN §4.3)。
        var source = new List<(InkDefinition Ink, byte[] Plane)>();
        foreach (var ink in result.RequiredInks)
        {
            if (result.Planes.TryGetValue(ink.Name, out var plane))
            {
                source.Add((ink, plane));
            }
        }

        var displayPlanes = BuildDisplayPlanes(source, rawByName, onlyInk);

        // D-038: 画素が正方形でない解像度の補正も、通常の描画とまったく同じ引数で行う。
        return PreviewRenderer.Render(
            result.Width, result.Height, displayPlanes, PreviewMaxWidth,
            result.Resolution.DpiX, result.Resolution.DpiY);
    }

    /// <summary>描画へ渡す (Ink, Plane) の並びを組み立てる。BuildPreviewCore と
    /// RenderPreviewBitmap の共通部分(同じ処理を 2 箇所に書かないため)。
    ///
    /// D-042: Ink は必ず上書き前(rawByName)のものへ差し替える — プレーンそのものは
    /// 上書き後の照合結果であり、差し替えるのは Ink だけ。
    /// onlyInk が指定されていれば、その名前のものだけを残す。該当が無ければ
    /// 空の並びを返す(例外にしない — 背景だけの絵になる)。
    /// 元の並び(印刷順)は崩さない。</summary>
    private static List<(InkDefinition Ink, byte[] Plane)> BuildDisplayPlanes(
        IEnumerable<(InkDefinition Ink, byte[] Plane)> source,
        IReadOnlyDictionary<string, InkDefinition> rawByName,
        string? onlyInk)
    {
        return source
            .Where(item => onlyInk is null || item.Ink.Name == onlyInk)
            .Select(item => (Ink: RawInkOf(item.Ink, rawByName), item.Plane))
            .ToList();
    }

    /// <summary>D-031: 指定したインクについて、上書きがあればそれを、無ければ
    /// パレットの既定値(InkDefinition.Passes)を返す。TraySettings.ResolvePasses
    /// と同じ流儀(名前で引き、無ければ既定へフォールバック)。</summary>
    private static int ResolvePasses(InkDefinition ink, IReadOnlyDictionary<string, int> passesOverride) =>
        passesOverride.TryGetValue(ink.Name, out int passes) ? passes : ink.Passes;

    /// <summary>D-042: 照合用パレットのインク(マジックカラーを上書き済み)から、
    /// 上書き前のインクを名前で引き直す。表示(プレビューの色・凡例)は常に
    /// 上書き前の色で行う — 引けなかった場合は渡されたインクをそのまま使う
    /// (白版の下地など、パレットに無いインクが返ってくる可能性への保険)。</summary>
    private static InkDefinition RawInkOf(InkDefinition ink, IReadOnlyDictionary<string, InkDefinition> rawByName) =>
        rawByName.TryGetValue(ink.Name, out var raw) ? raw : ink;

    /// <summary>RGL を組み立てるだけで送出しない。実機を消費せずに
    /// バイト列を検査するための経路(§9.5: バイト列の検証と実機の刷り上がり
    /// 確認は到達範囲が異なる)。</summary>
    public static byte[] BuildRgl(Dictionary<string, byte[]> planes, PrintJob job)
    {
        return Emitter.EmitJob(planes, job);
    }

    /// <summary>実機へ送出する。DOMAIN §15.2.1: トレイアプリが送出を排他的に
    /// 所有する — この呼び出しの間は状態問い合わせ(ReadRawStatus)を挟んでは
    /// ならない(呼び出し側 UI が busy フラグで担保する)。</summary>
    public static void Print(
        Dictionary<string, byte[]> planes, PrintJob job, MachineRoute route, string vid, Action<int, int>? progress)
    {
        byte[] rgl = Emitter.EmitJob(planes, job);
        using var transport = AlpsTransport.OpenDevice(vid, mode: route.Mode);
        transport.SendJob(rgl, progress);
    }

    /// <summary>プリンタ状態を生の値のまま読む(§7.2 の 7「プリンタ状態表示」)。
    /// カセットの過不足判定(§7.3)のデコードは別エージェントが実装中のため、
    /// ここでは Foilwright.Core.CassetteStatus をそのまま返すに留める。</summary>
    public static CassetteStatus ReadRawStatus(MachineRoute route, string vid)
    {
        using var transport = AlpsTransport.OpenDevice(vid, mode: route.Mode);
        return transport.ReadStatus();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // 後始末の失敗はジョブの成否に影響しないため無視する。
        }
    }
}
