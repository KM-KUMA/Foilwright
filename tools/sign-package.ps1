#requires -Version 5.1
<#
Foilwright — PPD パッケージへの自己署名(D-022)

Windows 11 は未署名のプリンタドライバパッケージを拒否する
(Code Integrity、エラー 0xE000022F)。純正の PostScript エンジン自体は
署名済みで OS に同梱されているが、「そのエンジンをこの PPD と組み合わせる」
と宣言する INF は新しいドライバパッケージ扱いになり、署名が要る。

本スクリプトは管理者権限を必要としない部分だけを行う:
  1. 署名用の自己署名証明書を作る(無ければ。CurrentUser\My に置く)
  2. makecat でカタログ(.cat)を生成する
  3. signtool でカタログに署名する
  4. 配布用に証明書の公開部分(.cer)を書き出す

秘密鍵はファイルに書き出さない(証明書ストアに留める)。**.pfx を作らないこと** —
リポジトリや配布物に秘密鍵が混入した時点で、この署名の意味は失われる。

証明書の信頼登録とドライバの導入は tools/install-printer.cmd(管理者)が行う。
#>

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$ppdDir = Join-Path $repo 'ppd'
$subject = 'CN=Foilwright Self-Signed (development)'

function Find-SdkTool([string]$name) {
    $roots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "${env:ProgramFiles}\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }
    $found = foreach ($root in $roots) {
        Get-ChildItem -Path $root -Filter $name -Recurse -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -match '\\x64\\' }
    }
    $tool = $found | Sort-Object FullName -Descending | Select-Object -First 1
    if (-not $tool) { throw "$name が見つからない(Windows SDK を確認)" }
    return $tool.FullName
}

$makecat = Find-SdkTool 'makecat.exe'
$signtool = Find-SdkTool 'signtool.exe'
Write-Host "makecat : $makecat"
Write-Host "signtool: $signtool"

$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq $subject } |
    Sort-Object NotAfter -Descending | Select-Object -First 1

if (-not $cert) {
    Write-Host '証明書を新規作成する'
    $cert = New-SelfSignedCertificate `
        -Subject $subject `
        -Type CodeSigningCert `
        -KeyUsage DigitalSignature `
        -KeyLength 2048 `
        -CertStoreLocation 'Cert:\CurrentUser\My' `
        -NotAfter (Get-Date).AddYears(5)
}
Write-Host ("証明書  : {0}  拇印 {1}" -f $cert.Subject, $cert.Thumbprint)

# makecat は CDF のあるディレクトリを基準にファイルを探す。
Push-Location $ppdDir
try {
    $cat = Join-Path $ppdDir 'foilwright.cat'
    if (Test-Path $cat) { Remove-Item $cat -Force }

    & $makecat -v 'foilwright.cdf'
    if (-not (Test-Path $cat)) { throw 'カタログの生成に失敗した' }
    Write-Host "カタログ: $cat"

    # タイムスタンプは付けない。外部サービスに依存させないため、
    # 証明書失効後は署名し直す運用とする(有効期限 5 年)。
    & $signtool sign /fd SHA256 /sha1 $cert.Thumbprint /v $cat
    if ($LASTEXITCODE -ne 0) { throw "signtool が失敗した (exit $LASTEXITCODE)" }

    & $signtool verify /pa /v $cat | Select-Object -Last 6
}
finally { Pop-Location }

$cerPath = Join-Path $ppdDir 'foilwright.cer'
Export-Certificate -Cert $cert -FilePath $cerPath -Force | Out-Null
Write-Host "公開証明書: $cerPath"
Write-Host '完了。次は管理者で tools\install-printer.cmd を実行する。'
