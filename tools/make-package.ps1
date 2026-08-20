<#
.SYNOPSIS
  配布 zip を組み立てる(D-039 / D-040 / D-041)。

.DESCRIPTION
  Foilwright.Tray と Foilwright.Cli を self-contained で publish し、
  設定ファイル一式(palette/ profiles/ papers/ media.yaml colour/)を
  実行ファイルと同じ場所へ集め、install.ps1 / uninstall.ps1 / README.txt を
  添えて zip にまとめる。出力先は dist/。

  Tray と Cli は同じ .NET ランタイム(net10.0 / win-x64)で publish される
  ため、共有ランタイムの DLL は内容が一致する。二重に持つと zip がさらに
  大きくなるので、1 つの app フォルダへまとめて上書きコピーする
  (AssetRoot の段 2「実行ファイルの隣」を両方の実行ファイルが同時に
  満たせるようにするねらいもある)。

.PARAMETER Configuration
  ビルド構成。既定は Release。

.EXAMPLE
  .\tools\make-package.ps1
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string]$Message)
    Write-Host "=== $Message ===" -ForegroundColor Cyan
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$distDir = Join-Path $repoRoot 'dist'
$stageDir = Join-Path $distDir 'stage'
$packageRoot = Join-Path $stageDir 'package'
$appDir = Join-Path $packageRoot 'app'
$pubTrayDir = Join-Path $stageDir 'pub-tray'
$pubCliDir = Join-Path $stageDir 'pub-cli'

# --- 版数 ---------------------------------------------------------------------
# 【推測】正式なバージョニング規則は決まっていない。git のコミット日時と
# 短縮ハッシュから機械的に組み立てる(D-041 には版数の形式についての記述が
# 無いため)。
Push-Location $repoRoot
try {
    $commitHash = (git rev-parse --short HEAD).Trim()
    $commitDate = (git log -1 --format=%cd --date=format:%Y%m%d).Trim()
} finally {
    Pop-Location
}
$version = "$commitDate+$commitHash"
Write-Host "版数: $version"

# --- 準備 ---------------------------------------------------------------------
Write-Step '準備'
if (Test-Path $stageDir) {
    Remove-Item -Path $stageDir -Recurse -Force
}
New-Item -ItemType Directory -Path $appDir -Force | Out-Null
Write-Host "  作業場所: $stageDir"

# --- publish (Foilwright.Tray) -------------------------------------------------
Write-Step 'dotnet publish: Foilwright.Tray'
$trayProject = Join-Path $repoRoot 'src\Foilwright.Tray\Foilwright.Tray.csproj'
dotnet publish $trayProject -c $Configuration -r win-x64 --self-contained true -o $pubTrayDir
if ($LASTEXITCODE -ne 0) {
    throw 'Foilwright.Tray の publish に失敗した'
}

# --- publish (Foilwright.Cli) --------------------------------------------------
Write-Step 'dotnet publish: Foilwright.Cli'
$cliProject = Join-Path $repoRoot 'src\Foilwright.Cli\Foilwright.Cli.csproj'
dotnet publish $cliProject -c $Configuration -r win-x64 --self-contained true -o $pubCliDir
if ($LASTEXITCODE -ne 0) {
    throw 'Foilwright.Cli の publish に失敗した'
}

# --- 実行ファイル一式をまとめる ------------------------------------------------
Write-Step '実行ファイル一式をまとめる'
Copy-Item -Path (Join-Path $pubTrayDir '*') -Destination $appDir -Recurse -Force
Copy-Item -Path (Join-Path $pubCliDir '*') -Destination $appDir -Recurse -Force
Write-Host "  $appDir へ集約した(Tray → Cli の順で上書き。共有ランタイムは内容一致のため無害)"

# --- 設定ファイル一式を同じ場所へ集める ----------------------------------------
Write-Step '設定ファイル一式を配置'
$configItems = @(
    @{ Name = 'palette'; IsDir = $true },
    @{ Name = 'profiles'; IsDir = $true },
    @{ Name = 'papers'; IsDir = $true },
    @{ Name = 'media.yaml'; IsDir = $false },
    @{ Name = 'colour'; IsDir = $true }
)
foreach ($item in $configItems) {
    $source = Join-Path $repoRoot $item.Name
    if (-not (Test-Path $source)) {
        throw "設定ファイルが見つからない: $source"
    }
    $destination = Join-Path $appDir $item.Name
    if ($item.IsDir) {
        Copy-Item -Path $source -Destination $destination -Recurse -Force
    } else {
        Copy-Item -Path $source -Destination $destination -Force
    }
    Write-Host "  配置した: $($item.Name)"
}

# install.ps1 が manifest に版数を記録できるように残しておく。
Set-Content -Path (Join-Path $appDir 'version.txt') -Value $version -NoNewline -Encoding utf8

# --- install.ps1 / uninstall.ps1 / README.txt を添える --------------------------
Write-Step '導入・削除スクリプトを添える'
$sourcePackageDir = Join-Path $repoRoot 'tools\package'
foreach ($name in @('install.ps1', 'uninstall.ps1', 'README.txt')) {
    Copy-Item -Path (Join-Path $sourcePackageDir $name) -Destination (Join-Path $packageRoot $name) -Force
    Write-Host "  添えた: $name"
}

# --- zip にまとめる -------------------------------------------------------------
Write-Step 'zip にまとめる'
$zipName = "Foilwright-$version-win-x64.zip"
$zipPath = Join-Path $distDir $zipName
if (Test-Path $zipPath) {
    Remove-Item -Path $zipPath -Force
}
Compress-Archive -Path (Join-Path $packageRoot '*') -DestinationPath $zipPath -CompressionLevel Optimal
Write-Host "  作成した: $zipPath"

# --- 中身の一覧と合計サイズを表示する --------------------------------------------
Write-Step '中身の一覧'
$files = Get-ChildItem -Path $packageRoot -Recurse -File
$totalBytes = ($files | Measure-Object -Property Length -Sum).Sum
foreach ($file in ($files | Sort-Object FullName)) {
    $relative = $file.FullName.Substring($packageRoot.Length + 1)
    Write-Host ("  {0,10:N0}  {1}" -f $file.Length, $relative)
}
$totalMb = [math]::Round($totalBytes / 1MB, 1)
$zipBytes = (Get-Item $zipPath).Length
$zipMb = [math]::Round($zipBytes / 1MB, 1)
Write-Host ''
Write-Host "展開後の合計サイズ: $totalMb MB ($totalBytes バイト、$($files.Count) ファイル)" -ForegroundColor Green
Write-Host "zip のサイズ: $zipMb MB ($zipBytes バイト)" -ForegroundColor Green
Write-Host "出力先: $zipPath" -ForegroundColor Green
