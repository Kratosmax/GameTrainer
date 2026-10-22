#Requires -Version 5.1
param([string]$OutFile = "")

# 生成适配包索引。
#
# 只输出元数据（id / name / version / engine / summary / sha256 / path），
# 不内嵌 adapter.json 全文。
#
# 历史教训：早期把全文内嵌进索引，2 个包就 29KB，安装时只是把字符串写到磁盘，
# 根本没有网络下载。索引随包数量线性膨胀，且改包必须重发索引。
# 现在索引只是清单，内容按需从 adapters/<id>/adapter.json 下载。

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$adaptersRoot = Join-Path $repoRoot "adapters"
if (-not $OutFile) { $OutFile = Join-Path $adaptersRoot "index.json" }
if (-not (Test-Path $adaptersRoot)) { Write-Error "no adapters dir"; exit 1 }

$items = @()

Get-ChildItem $adaptersRoot -Directory | Sort-Object Name | ForEach-Object {
    $dir = $_.FullName
    $adapterPath = Join-Path $dir "adapter.json"
    if (-not (Test-Path $adapterPath)) { return }

    $id = $_.Name
    $json = [IO.File]::ReadAllText($adapterPath)
    if ([string]::IsNullOrWhiteSpace($json)) { Write-Warning "$id empty"; return }

    try { $parsed = $json | ConvertFrom-Json }
    catch { Write-Warning "$id parse failed"; return }

    $name = $id
    if ($parsed.name) { $name = $parsed.name }

    $engine = ""
    if ($parsed.engine) { $engine = $parsed.engine }

    # summary：给首页卡片用的副标题，取 bindings 数量与引擎
    $bindingCount = 0
    if ($parsed.bindings) { $bindingCount = @($parsed.bindings).Count }
    $summary = "$engine · $bindingCount 个数值"

    # SHA256 必须与 GitHub 上的字节一致。
    # git 会把 CRLF 转成 LF（.gitattributes 设了 eol=lf），
    # 所以这里也按 LF 归一后再算，否则客户端校验必然失败
    # （实测同一份文件本地 10127 字节、远端 9939 字节）。
    $normalized = $json -replace "`r`n", "`n"
    $bytes = [System.Text.Encoding]::UTF8.GetBytes($normalized)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $hashBytes = $sha.ComputeHash($bytes) } finally { $sha.Dispose() }
    $hash = -join ($hashBytes | ForEach-Object { $_.ToString("x2") })

    $items += [ordered]@{
        id      = $id
        name    = $name
        version = "1"
        engine  = $engine
        summary = $summary
        sha256  = $hash
        path    = "$id/adapter.json"
    }

    Write-Host ("  {0,-24} {1}  {2}" -f $id, $hash.Substring(0,12), $name)
}

$index = [ordered]@{ version = 1; adapters = $items }
$jsonOut = $index | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($OutFile, $jsonOut, (New-Object System.Text.UTF8Encoding $false))

$size = [math]::Round((Get-Item $OutFile).Length / 1KB, 1)
Write-Host ""
Write-Host "generated: $OutFile  count=$($items.Count)  size=${size}KB"
