#Requires -Version 5.1
<#
.SYNOPSIS
    构建 GameTrainer 正式发布产物：四个包 + 三个签名的更新清单 + 校验和。

.DESCRIPTION
    产物布局（temp/release/v<版本>/）：
        GameTrainer-<ver>-Full-Setup.exe      安装器（自带运行时）
        GameTrainer-<ver>-Lite-Setup.exe      安装器（需 .NET 8）
        GameTrainer-<ver>-Full.zip            便携包（自带运行时）
        GameTrainer-<ver>-Lite.zip            便携包（需 .NET 8）
        update.json                           兼容清单（指向 Lite）
        update-lite.json                      Lite 通道清单
        update-full.json                      Full 通道清单
        SHA256SUMS.txt                        全部产物的校验和

    签名清单必须由私钥生成；没有私钥时只出包与校验和，并明确警告
    「这不是可发布状态」——不允许无签名清单冒充正式 Release。

.PARAMETER PrivateKeyPath
    更新签名私钥（PEM）。仅发布者本机持有，绝不入库。

.PARAMETER InnoSetupPath
    Inno Setup 6 的 ISCC.exe。省略时自动在常见位置查找。

.EXAMPLE
    pwsh -File build\release.ps1 -PrivateKeyPath temp\keys\gametrainer-update-private.pem
#>
[CmdletBinding()]
param(
    [string]$PrivateKeyPath = "",
    [string]$InnoSetupPath = "",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot

function Write-Step($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }
function Write-Ok($msg)   { Write-Host "  OK  $msg" -ForegroundColor Green }
function Write-Warn2($msg){ Write-Host "  !!  $msg" -ForegroundColor Yellow }

# ---------- 版本（唯一来源） ----------
[xml]$props = Get-Content -Raw (Join-Path $repoRoot "Directory.Build.props")
$version = [string]$props.Project.PropertyGroup.GameTrainerVersion
if ([string]::IsNullOrWhiteSpace($version)) { throw "Directory.Build.props 缺少 GameTrainerVersion" }
Write-Step "版本 $version"

$releaseRoot = Join-Path $repoRoot "temp\release\v$version"
if (Test-Path $releaseRoot) { Remove-Item $releaseRoot -Recurse -Force }
New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null

# ---------- 1. 构建 ----------
Write-Step "构建解决方案"
dotnet build (Join-Path $repoRoot "GameTrainer.sln") -c $Configuration -m:1 --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "构建失败" }
Write-Ok "构建通过"

Write-Step "运行单元测试"
dotnet test (Join-Path $repoRoot "src\GameTrainer.Tests") -c $Configuration --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "测试失败" }
Write-Ok "测试通过"

# ---------- 2. 两个便携包 ----------
Write-Step "打包（Full / Lite）"
& (Join-Path $repoRoot "build\package.ps1") -Channel Both -OutputDir $releaseRoot
if ($LASTEXITCODE -ne 0) { throw "打包失败" }

$fullZip = Join-Path $releaseRoot "GameTrainer-$version-Full.zip"
$liteZip = Join-Path $releaseRoot "GameTrainer-$version-Lite.zip"
foreach ($z in @($fullZip, $liteZip)) {
    if (-not (Test-Path $z)) { throw "缺少产物 $z" }
}

# ---------- 3. 安装器 ----------
Write-Step "查找 Inno Setup 6"
if ([string]::IsNullOrWhiteSpace($InnoSetupPath)) {
    $candidates = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe")
    )
    $InnoSetupPath = $candidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($InnoSetupPath) -or -not (Test-Path -LiteralPath $InnoSetupPath)) {
    throw "未找到 Inno Setup 6 编译器 ISCC.exe。请安装 https://jrsoftware.org/isdl.php 或用 -InnoSetupPath 指定。"
}
Write-Ok $InnoSetupPath

$stageRoot = Join-Path $repoRoot "temp\stage"
$issPath = Join-Path $repoRoot "installer\GameTrainer.iss"

function Build-Setup {
    param([string]$Channel, [string]$SourceStage, [switch]$RequireRuntime)

    $outBase = "GameTrainer-$version-$Channel-Setup"
    $args = @(
        "/DAppVersion=$version",
        "/DSourceDir=$SourceStage",
        "/DOutputDir=$releaseRoot",
        "/DOutputBaseFilename=$outBase"
    )
    if ($RequireRuntime) { $args += "/DRequireDesktopRuntime=1" }

    & $InnoSetupPath @args $issPath
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup 编译失败（$Channel）" }

    $exe = Join-Path $releaseRoot "$outBase.exe"
    if (-not (Test-Path $exe)) { throw "安装器未生成：$exe" }
    Write-Ok "$([IO.Path]::GetFileName($exe))  $([math]::Round((Get-Item $exe).Length/1MB,1)) MB"
}

Write-Step "编译安装器"
# package.ps1 把打包中间态留在 temp\stage\<Channel>\
Build-Setup -Channel "Full" -SourceStage (Join-Path $stageRoot "Full")
Build-Setup -Channel "Lite" -SourceStage (Join-Path $stageRoot "Lite") -RequireRuntime

# ---------- 4. 签名更新清单 ----------
Write-Step "生成更新清单"
$tool = Join-Path $repoRoot "tools\ReleaseTool\ReleaseTool.csproj"
dotnet build $tool -c $Configuration --nologo -v q | Out-Null
    # 输出被 Directory.Build.props 重定向到 temp/build/<项目名>/，不是默认 bin/
    $toolExe = Join-Path $repoRoot "temp\build\ReleaseTool\$Configuration\net8.0\GameTrainer.ReleaseTool.exe"
    if (-not (Test-Path $toolExe)) {
        $found = Get-ChildItem (Join-Path $repoRoot "temp\build\ReleaseTool") -Recurse -Filter "GameTrainer.ReleaseTool.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
        if (-not $found) { throw "找不到 ReleaseTool 产物" }
        $toolExe = $found.FullName
    }

$hasKey = -not [string]::IsNullOrWhiteSpace($PrivateKeyPath) -and (Test-Path $PrivateKeyPath)

function New-Manifest {
    param([string]$Channel, [string]$Package, [string]$FileName)
    $out = Join-Path $releaseRoot $FileName
    # Capture tool stdout: otherwise it pollutes the function return value
    # and $liteJson becomes a string like "generated F:...", breaking Copy-Item.
    # --notes is required: without it releaseNotes is empty and the client
    # update dialog shows "no release notes provided".
    $signArgs = @(
        "sign", "--channel", $Channel, "--version", $version,
        "--package", $Package, "--output", $out, "--key", $PrivateKeyPath
    )
    if ($script:releaseNotesPath) { $signArgs += @("--notes", $script:releaseNotesPath) }
    $toolOut = & $toolExe @signArgs 2>&1
    if ($LASTEXITCODE -ne 0) {
        Write-Host ($toolOut | Out-String)
        throw "清单生成失败（$Channel）"
    }
    if (-not (Test-Path $out)) { throw "清单未生成：$out" }
    Write-Ok "$FileName"
    return $out   # 只返回路径
}

if ($hasKey) {
    # Release notes file: current version only, used for manifest releaseNotes
    $script:releaseNotesPath = Join-Path $repoRoot "RELEASE_NOTES_CURRENT.md"
    if (-not (Test-Path $script:releaseNotesPath)) {
        $script:releaseNotesPath = $null
        Write-Host "  [warn] RELEASE_NOTES_CURRENT.md missing; manifest will omit notes"
    }

    $liteJson = New-Manifest -Channel "lite" -Package $liteZip -FileName "update-lite.json"
    $fullJson = New-Manifest -Channel "full" -Package $fullZip -FileName "update-full.json"
    # 兼容清单：老版本只认 update.json，指向 Lite
    Copy-Item $liteJson (Join-Path $releaseRoot "update.json") -Force
    Write-Ok "update.json（兼容，等同 Lite）"

    # 自校验：用公钥验一遍，确认签名/大小/哈希三者自洽
    Write-Step "验证清单"
    $pubKey = Join-Path (Split-Path $PrivateKeyPath) "gametrainer-update-public.pem"
    if (Test-Path $pubKey) {
        & $toolExe verify --channel lite --manifest $liteJson --package $liteZip --key $pubKey
        if ($LASTEXITCODE -ne 0) { throw "Lite 清单自校验失败" }
        & $toolExe verify --channel full --manifest $fullJson --package $fullZip --key $pubKey
        if ($LASTEXITCODE -ne 0) { throw "Full 清单自校验失败" }
    } else {
        Write-Warn2 "找不到公钥 $pubKey，跳过自校验"
    }
} else {
    Write-Warn2 "未提供私钥：不生成更新清单"
    Write-Warn2 "这不是可发布状态——正式 Release 必须有签名清单"
}

# ---------- 5. 校验和 ----------
Write-Step "生成 SHA256SUMS.txt"
$sums = Join-Path $releaseRoot "SHA256SUMS.txt"
Get-ChildItem $releaseRoot -File | Where-Object { $_.Name -ne "SHA256SUMS.txt" } |
    Sort-Object Name | ForEach-Object {
        $h = (Get-FileHash $_.FullName -Algorithm SHA256).Hash
        "$h  $($_.Name)"
    } | Set-Content $sums -Encoding UTF8
Write-Ok "$((Get-Content $sums).Count) 个文件已记录"

# ---------- 汇总 ----------
Write-Step "产物汇总"
Get-ChildItem $releaseRoot -File | Sort-Object Name |
    Select-Object @{n='文件';e={$_.Name}}, @{n='大小';e={"{0:N0}" -f $_.Length}} |
    Format-Table -AutoSize

Write-Host ""
Write-Host "产物目录: $releaseRoot" -ForegroundColor Cyan
if (-not $hasKey) {
    Write-Host "警告：未签名清单，仅供本地验证，不可发布。" -ForegroundColor Yellow
}
