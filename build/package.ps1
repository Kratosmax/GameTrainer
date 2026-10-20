<#
.SYNOPSIS
    构建 GameTrainer 的发布包（Full / Lite，ZIP）。

.DESCRIPTION
    版本从 Directory.Build.props 的 GameTrainerVersion 读取，不在脚本里重复写死。

    通道差异：
      Full  自包含发布（含 .NET 运行时），开箱即用，体积大
      Lite  依赖框架发布（需预装 .NET 8 Desktop Runtime），体积小

    -PreviewOnly 用于本地验收：产物写到 temp/preview/，文件名带 -preview 标记，
    并在包内附带 PREVIEW-NOTICE.txt 说明它未经签名、不是正式 Release。

    本脚本只构建，不提交、不打标签、不发布。

.EXAMPLE
    pwsh -File build/package.ps1 -PreviewOnly
    pwsh -File build/package.ps1 -Channel Lite -PreviewOnly
#>
[CmdletBinding()]
param(
    [ValidateSet('Full', 'Lite', 'Both')]
    [string]$Channel = 'Both',

    [string]$OutputDir,

    [switch]$PreviewOnly
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$repoRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repoRoot 'Directory.Build.props'

# 统一写无 BOM UTF-8。PowerShell 5.1 没有 -Encoding utf8NoBOM，
# 而带 BOM 会污染 channel.txt / version.txt 的机器读取。
function Write-Utf8NoBom {
    param([string]$Path, [string]$Content)
    $enc = New-Object System.Text.UTF8Encoding $false
    [System.IO.File]::WriteAllText($Path, $Content, $enc)
}

if (-not (Test-Path $propsPath)) { throw "找不到版本源：$propsPath" }

# 版本唯一来源
$version = ([xml](Get-Content $propsPath -Raw)).Project.PropertyGroup.GameTrainerVersion
if ([string]::IsNullOrWhiteSpace($version)) { throw "Directory.Build.props 缺少 GameTrainerVersion" }
Write-Host "版本源 GameTrainerVersion = $version"

if (-not $OutputDir) {
    $OutputDir = if ($PreviewOnly) { Join-Path $repoRoot 'temp/preview' } else { Join-Path $repoRoot 'artifacts' }
}
New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

$project = Join-Path $repoRoot 'src/GameTrainer/GameTrainer.csproj'
$channels = if ($Channel -eq 'Both') { @('Full', 'Lite') } else { @($Channel) }
$built = @()

foreach ($ch in $channels) {
    Write-Host "`n=== 构建 $ch ===" -ForegroundColor Cyan
    $stage = Join-Path $repoRoot "temp/stage/$ch"
    if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $stage | Out-Null

    $publishArgs = @(
        'publish', $project,
        '-c', 'Release',
        '-o', $stage,
        "-p:GameTrainerVersion=$version"
    )

    if ($ch -eq 'Full') {
        $publishArgs += @('-r', 'win-x64', '--self-contained', 'true')
    }
    else {
        $publishArgs += @('--self-contained', 'false')
    }

    & dotnet @publishArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败（$ch）" }

    # 调试符号不进发行包
    Get-ChildItem $stage -Filter *.pdb -File -ErrorAction SilentlyContinue | Remove-Item -Force

    # 运行期资产：profiles 与 snapshots 必须随包分发，否则首次启动没有可用 profile。
    # 注意 Copy-Item 的坑：目标目录已存在时，-Destination <dir> 会把它复制成 <dir>\profiles，
    # 结果包内 profiles 是空的。必须明确目标是父目录。
    $profilesSrc = Join-Path $repoRoot 'profiles'
    $profilesDst = Join-Path $stage 'profiles'
    if (Test-Path $profilesDst) { Remove-Item $profilesDst -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $profilesDst | Out-Null
    if (Test-Path $profilesSrc) {
        Copy-Item (Join-Path $profilesSrc '*') -Destination $profilesDst -Recurse -Force -ErrorAction SilentlyContinue
    }

    # 首次启动需要激活记录目录存在
    New-Item -ItemType Directory -Force -Path (Join-Path $stage 'snapshots') | Out-Null

    # 通道标记：更新器只接受同通道包
    Write-Utf8NoBom (Join-Path $stage 'channel.txt') $ch
    Write-Utf8NoBom (Join-Path $stage 'version.txt') $version

    if ($PreviewOnly) {
        $notice = @"
本地预览包 —— 非正式 Release
================================
版本    : $version
通道    : $ch
构建时间: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
构建主机: $env:COMPUTERNAME

这是本地验收用的预览产物：
  * 未经过代码签名
  * 未生成更新清单，不能用于自动更新
  * 未推送到任何发布渠道
请勿当作正式发行版分发。
"@
        Write-Utf8NoBom (Join-Path $stage 'PREVIEW-NOTICE.txt') $notice
    }

    $suffix = if ($PreviewOnly) { '-preview' } else { '' }
    $zipName = "GameTrainer-$version-$ch$suffix.zip"
    $zipPath = Join-Path $OutputDir $zipName
    if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

    Compress-Archive -Path (Join-Path $stage '*') -DestinationPath $zipPath -CompressionLevel Optimal

    $item = Get-Item $zipPath
    $hash = (Get-FileHash $zipPath -Algorithm SHA256).Hash
    $built += [pscustomobject]@{
        Channel = $ch
        File    = $item.FullName
        Bytes   = $item.Length
        SHA256  = $hash
    }
    Write-Host ("{0}  {1:N0} 字节  SHA256={2}" -f $zipName, $item.Length, $hash)
}

Write-Host "`n=== 产物汇总 ===" -ForegroundColor Green
$built | Format-Table Channel, Bytes, File -AutoSize

# 校验和清单：便于验收时独立复核
$sumPath = Join-Path $OutputDir 'SHA256SUMS.txt'
$sumLines = $built | ForEach-Object { "$($_.SHA256)  $(Split-Path -Leaf $_.File)" }
Write-Utf8NoBom $sumPath (($sumLines -join "`r`n") + "`r`n")
Write-Host "校验和清单：$sumPath"

Write-Utf8NoBom (Join-Path $OutputDir 'build-manifest.json') ($built | ConvertTo-Json -Depth 4)

if ($PreviewOnly) {
    Write-Host "`n注意：以上为本地预览包，非正式 Release。" -ForegroundColor Yellow
}
