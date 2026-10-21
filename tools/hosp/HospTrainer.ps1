#Requires -Version 5.1
<#
.SYNOPSIS
    Hospital Rhapsody 修改器 —— 交互式入口

.DESCRIPTION
    双击运行（或在 PowerShell 里执行），按菜单选择要改什么。
    每次操作都会现场扫描定位存档，读档后无需重新配置。

.NOTES
    需要游戏正在运行。修改前会显示当前值和目标值，确认后才写入。
#>
$ErrorActionPreference = "Stop"
$OutputEncoding = [System.Text.Encoding]::UTF8

$gt = "F:\git\temp\game-trainer\temp\build\GameTrainer\Release\net8.0-windows\GameTrainer.exe"
$game = "hospitalrhapsody"

function Get-GamePid {
    $p = Get-Process -Name "Hospital Rhapsody" -ErrorAction SilentlyContinue | Select-Object -First 1
    if (-not $p) {
        Write-Host ""
        Write-Host "  游戏没有运行。请先启动 Hospital Rhapsody 并读取存档。" -ForegroundColor Red
        return $null
    }
    return $p
}

function Show-Employees {
    Write-Host ""
    Write-Host "当前员工列表（读取中，约 2-3 秒）..." -ForegroundColor DarkGray
    & $gt sem $game read employee_good 2>&1 | Select-Object -Skip 2
}

function Invoke-Read {
    param([string]$Semantic, [string]$Title)
    Write-Host ""
    Write-Host "=== $Title ===" -ForegroundColor Cyan
    & $gt sem $game read $Semantic 2>&1 | Select-Object -Skip 1
}

function Invoke-Write {
    param([string]$Semantic, [string]$Title, [string]$Hint)

    Write-Host ""
    Write-Host "=== $Title ===" -ForegroundColor Cyan
    Write-Host "  $Hint" -ForegroundColor DarkGray
    Write-Host ""

    $val = Read-Host "  输入新值（直接回车取消）"
    if ([string]::IsNullOrWhiteSpace($val)) { Write-Host "  已取消"; return }

    Write-Host ""
    Write-Host "  正在写入..." -ForegroundColor DarkGray
    & $gt sem $game write $Semantic $val 2>&1 | Select-Object -Skip 1 | Select-Object -Last 3
}

# ---------------- 主菜单 ----------------

while ($true) {
    Clear-Host
    Write-Host ""
    Write-Host "  ╔══════════════════════════════════════════════╗" -ForegroundColor Cyan
    Write-Host "  ║   Hospital Rhapsody  修改器                  ║" -ForegroundColor Cyan
    Write-Host "  ╚══════════════════════════════════════════════╝" -ForegroundColor Cyan

    $proc = Get-Process -Name "Hospital Rhapsody" -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($proc) {
        Write-Host "     游戏状态: 运行中 (PID $($proc.Id))" -ForegroundColor Green
    } else {
        Write-Host "     游戏状态: 未运行" -ForegroundColor Red
    }

    Write-Host ""
    Write-Host "   [1] 查看所有员工（姓名 / 医术 / 好感 / 猥琐）"
    Write-Host "   [2] 改总资产"
    Write-Host "   [3] 改好感度"
    Write-Host "   [4] 改猥琐值"
    Write-Host "   [5] 全体改医术"
    Write-Host ""
    Write-Host "   [0] 退出"
    Write-Host ""

    $choice = Read-Host "  请选择"

    switch ($choice) {
        "1" {
            if (-not (Get-GamePid)) { Read-Host "按回车返回"; continue }
            Show-Employees
            Read-Host "`n按回车返回"
        }
        "2" {
            if (-not (Get-GamePid)) { Read-Host "按回车返回"; continue }
            Invoke-Read "gold_total" "当前总资产"
            Invoke-Write "gold_total" "改总资产" "例如 5000000"
            Read-Host "`n按回车返回"
        }
        "3" {
            if (-not (Get-GamePid)) { Read-Host "按回车返回"; continue }
            Show-Employees
            Write-Host ""
            $id = Read-Host "  输入员工ID（如 2500），直接回车=全部员工"
            Write-Host ""
            $val = Read-Host "  输入目标好感度（直接回车取消）"
            if (-not [string]::IsNullOrWhiteSpace($val)) {
                $target = if ([string]::IsNullOrWhiteSpace($id)) { $val } else { "$id=$val" }
                & $gt sem $game write employee_good $target 2>&1 | Select-Object -Skip 1 | Select-Object -Last 3
            }
            Read-Host "`n按回车返回"
        }
        "4" {
            if (-not (Get-GamePid)) { Read-Host "按回车返回"; continue }
            Show-Employees
            Write-Host ""
            $id = Read-Host "  输入员工ID（如 1001），直接回车=全部员工"
            Write-Host ""
            $val = Read-Host "  输入目标猥琐值（直接回车取消）"
            if (-not [string]::IsNullOrWhiteSpace($val)) {
                $target = if ([string]::IsNullOrWhiteSpace($id)) { $val } else { "$id=$val" }
                & $gt sem $game write employee_bad $target 2>&1 | Select-Object -Skip 1 | Select-Object -Last 3
            }
            Read-Host "`n按回车返回"
        }
        "5" {
            if (-not (Get-GamePid)) { Read-Host "按回车返回"; continue }
            Invoke-Read "employee_medical" "当前医术"
            Invoke-Write "employee_medical" "全体改医术" "例如 30"
            Read-Host "`n按回车返回"
        }
        "0" { return }
        default {
            Write-Host "  无效选项" -ForegroundColor Yellow
            Start-Sleep -Milliseconds 800
        }
    }
}
