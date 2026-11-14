<#
.SYNOPSIS
    把 NativePlugins/VoxRTPlugin/shaders 下的 HLSL 编译为 DXIL 并拷贝到 Unity 插件目录。

.DESCRIPTION
    Unity Built-in RP 不编译 DXR 着色器（#pragma require raytracing 只对 HDRP 生效），
    因此 raygen / miss / intersection / closest-hit 必须离线编译成 DXIL 库（lib_6_3），
    由 VoxRTPlugin.dll 在运行时读文件交给 D3D12 创建 RTPSO。

    ⚠ DXIL 签名：dxc.exe 同目录必须有 dxil.dll，否则产出的 DXIL 未签名，
      在非开发者模式（未开启 "开发者模式" / 未安装 Windows SDK 的调试运行时）
      会导致 ID3D12Device::CreateStateObject 失败。本脚本会做存在性检查并提示。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File build_shaders.ps1
#>

[CmdletBinding()]
param(
    [string]$DxcPath  = "",                                     # 留空则自动查找
    [string]$OutDir   = "",                                     # 留空 = ..\..\Assets\Plugins\x86_64
    [string]$ShaderDir = "",
    [switch]$KeepPdbs
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

if ([string]::IsNullOrEmpty($ShaderDir)) { $ShaderDir = Join-Path $scriptDir 'shaders' }
if ([string]::IsNullOrEmpty($OutDir))    { $OutDir    = Join-Path $scriptDir '..\..\Assets\Plugins\x86_64' }
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

Write-Host "== VoxRTPlugin 着色器编译 ==" -ForegroundColor Cyan
Write-Host "  源码目录: $ShaderDir"
Write-Host "  输出目录: $OutDir"

# ---------------------------------------------------------------- 定位 dxc.exe
# 选择策略：收集 PATH + Windows SDK 的全部候选，**优先选同目录带 dxil.dll 的**。
# 否则像 VulkanSDK 里的 dxc.exe 会因排在 PATH 前面而被选中，产物无签名 →
# 非开发者模式下 CreateStateObject 会失败（错误很隐蔽）。
if ([string]::IsNullOrEmpty($DxcPath)) {
    $cands = New-Object System.Collections.Generic.List[string]

    $cmd = Get-Command dxc.exe -ErrorAction SilentlyContinue
    if ($cmd) { $cands.Add($cmd.Source) }

    $sdkRoots = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin",
        "$env:ProgramFiles\Windows Kits\10\bin"
    ) | Where-Object { Test-Path $_ }

    foreach ($root in $sdkRoots) {
        Get-ChildItem -Path $root -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { $cands.Add((Join-Path $_.FullName 'x64\dxc.exe')) }
    }

    $DxcPath = ($cands | Where-Object {
        (Test-Path $_) -and (Test-Path (Join-Path (Split-Path -Parent $_) 'dxil.dll'))
    } | Select-Object -First 1)

    if ([string]::IsNullOrEmpty($DxcPath)) {
        $DxcPath = ($cands | Where-Object { Test-Path $_ } | Select-Object -First 1)
    }
}

if ([string]::IsNullOrEmpty($DxcPath) -or -not (Test-Path $DxcPath)) {
    Write-Error @"
未找到 dxc.exe。三种解决办法（任选其一）：
  1) 安装 Windows 10/11 SDK（含 "Windows SDK for Desktop C++ x86 and x64"），
     dxc.exe 位于 <SDK>\bin\<版本>\x64\dxc.exe
  2) 从 https://github.com/microsoft/DirectXShaderCompiler/releases 下载 release，
     解压后把 ...\bin\x64 加入 PATH
  3) 直接指定：build_shaders.ps1 -DxcPath "D:\dxc\bin\x64\dxc.exe"
"@
}

Write-Host "  dxc.exe : $DxcPath"

# ---------------------------------------------------------------- 检查 dxil.dll
# 与 dxc.exe 同目录的 dxil.dll 负责给 DXIL 签名；缺失 → 非开发者模式下 PSO 创建失败
$dxilDll = Join-Path (Split-Path -Parent $DxcPath) 'dxil.dll'
if (-not (Test-Path $dxilDll)) {
    Write-Warning @"
未找到 $dxilDll
  → 产物 DXIL 未签名，只有在 Windows "开发者模式" 下才能被 D3D12 接受。
  → 请把 dxil.dll 与 dxc.exe 放在同一目录（DirectXShaderCompiler release 包里自带）。
"@
} else {
    Write-Host "  dxil.dll: OK（DXIL 将被签名）"
}

# ---------------------------------------------------------------- 编译
$src  = Join-Path $ShaderDir 'voxel_pathtracing.hlsl'
if (-not (Test-Path $src)) { Write-Error "找不到入口文件: $src" }

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$out = Join-Path $OutDir 'voxel_pathtracing.dxil'

# lib 目标不需要 -E；-HV 2018 保证 DXIL 版本与 Win10 1809+ 的 D3D12 兼容
$dxcArgs = @('-T','lib_6_3','-HV','2018','-Fo',$out,'-I',$ShaderDir,$src)
if ($KeepPdbs) { $dxcArgs += @('-Zi','-Qembed_debug') }
else           { $dxcArgs += @('-Qstrip_debug','-Qstrip_reflect') }

Write-Host "  dxc $($dxcArgs -join ' ')"
& $DxcPath @dxcArgs
if ($LASTEXITCODE -ne 0) { Write-Error "dxc 编译失败（退出码 $LASTEXITCODE）" }

if (-not (Test-Path $out)) { Write-Error "dxc 未产出 $out" }
$size = (Get-Item $out).Length
Write-Host "  产出: $out ($size 字节)" -ForegroundColor Green

# ---------------------------------------------------------------- 完成
Write-Host ""
Write-Host "下一步：" -ForegroundColor Cyan
Write-Host "  msbuild VoxRTPlugin.vcxproj /p:Configuration=Release /p:Platform=x64"
Write-Host "  copy build\x64\Release\VoxRTPlugin.dll ..\..\Assets\Plugins\x86_64\"
Write-Host ""
Write-Host "注意：Unity 会为 voxel_pathtracing.dxil 生成 .meta，记得一并提交。" -ForegroundColor DarkGray
