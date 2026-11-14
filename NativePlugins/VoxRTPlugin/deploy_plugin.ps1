<#
.SYNOPSIS
    Deploy the freshly built VoxRTPlugin.dll into Assets/Plugins/x86_64.

.DESCRIPTION
    已加载的 native plugin 在 Unity 编辑器生命周期内会一直被映射（文件被锁），
    因此必须**先退出 Unity** 才能覆盖 DLL。本脚本会等待 Unity 进程退出后再拷贝。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File deploy_plugin.ps1
#>

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Definition
$src  = Join-Path $root 'build\x64\Release\VoxRTPlugin.dll'
$dir  = Join-Path $root '..\..\Assets\Plugins\x86_64'
$dst  = Join-Path $dir 'VoxRTPlugin.dll'

if (-not (Test-Path $src)) {
    Write-Error "Build output not found: $src  (run msbuild first)"
}

if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

$unity = Get-Process -Name Unity -ErrorAction SilentlyContinue
if ($unity) {
    Write-Host "Unity is running (PID $($unity.Id -join ', '))." -ForegroundColor Yellow
    Write-Host "Waiting for Unity to exit - please close the Unity editor..." -ForegroundColor Cyan
    while (Get-Process -Name Unity -ErrorAction SilentlyContinue) { Start-Sleep -Seconds 2 }
}

Copy-Item $src $dst -Force
$item = Get-Item $dst
Write-Host "Deployed: $dst ($($item.Length) bytes)" -ForegroundColor Green
Write-Host "Now reopen Unity and re-run: Tools/VoxRender/DXR Capability Probe" -ForegroundColor Cyan
