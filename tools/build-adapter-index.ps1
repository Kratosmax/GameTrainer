#Requires -Version 5.1
param([string]$OutFile = "")

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
    $name = $id
    try { $parsed = $json | ConvertFrom-Json; if ($parsed.name) { $name = $parsed.name } }
    catch { Write-Warning "$id parse failed"; return }

    $bytes = [System.Text.Encoding]::UTF8.GetBytes($json)
    $sha = [System.Security.Cryptography.SHA256]::Create()
    try { $hashBytes = $sha.ComputeHash($bytes) } finally { $sha.Dispose() }
    $hash = -join ($hashBytes | ForEach-Object { $_.ToString("x2") })

    $readme = $null
    $rp = Join-Path $dir "README.md"
    if (Test-Path $rp) { $readme = [IO.File]::ReadAllText($rp) }

    $items += [ordered]@{ id=$id; name=$name; sha256=$hash; json=$json; readme=$readme }
    Write-Host ("  {0,-24} {1}  {2}" -f $id, $hash.Substring(0,12), $name)
}

$index = [ordered]@{ version = 1; adapters = $items }
$jsonOut = $index | ConvertTo-Json -Depth 6
[System.IO.File]::WriteAllText($OutFile, $jsonOut, (New-Object System.Text.UTF8Encoding $false))
Write-Host ""
Write-Host "generated: $OutFile  count=$($items.Count)  size=$([math]::Round((Get-Item $OutFile).Length/1KB,1))KB"