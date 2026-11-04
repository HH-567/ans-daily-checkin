# 编译桌面客户端 ANS签到.exe
#
#   右键「使用 PowerShell 运行」，或在 PowerShell 里执行  .\build-exe.ps1
#
# 原理：用 Windows 自带的 .NET Framework 编译器（csc.exe）编译，不需要下载
#       Electron / Tauri 之类的运行时，产物只有几十 KB，双击即用。

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
}
if (-not (Test-Path $csc)) {
    throw '找不到 .NET Framework 编译器 csc.exe（Windows 默认自带，可能被精简过）'
}

$source = Join-Path $root 'desktop\Launcher.cs'
$icon = Join-Path $root 'desktop\app.ico'
$output = Join-Path $root 'ANS签到.exe'

if (-not (Test-Path $source)) { throw "缺少源码：$source" }

# 图标缺失时自动生成
if (-not (Test-Path $icon)) {
    $py = Get-Command python -ErrorAction SilentlyContinue
    if ($py) {
        Write-Host '正在生成图标…'
        & $py.Source (Join-Path $root 'desktop\make_icon.py')
    } else {
        Write-Host '! 未找到 python，跳过图标生成（exe 将使用默认图标）' -ForegroundColor Yellow
    }
}

$refs = @(
    '/r:System.dll',
    '/r:System.Core.dll',
    '/r:System.Windows.Forms.dll',
    '/r:System.Drawing.dll',
    '/r:Microsoft.CSharp.dll'
)

$args = @(
    '/nologo',
    '/target:winexe',
    '/optimize+',
    '/platform:anycpu',
    "/out:$output",
    $refs
) + @($source)

if (Test-Path $icon) { $args = @('/win32icon:' + $icon) + $args }

Write-Host '正在编译…'
$out = & $csc @args 2>&1
$code = $LASTEXITCODE

if ($out) { $out | ForEach-Object { Write-Host $_ } }

if ($code -ne 0 -or -not (Test-Path $output)) {
    throw "编译失败（退出码 $code）"
}

$info = Get-Item $output
Write-Host ''
Write-Host "✓ 已生成：$($info.FullName)" -ForegroundColor Green
Write-Host "  体积：$([math]::Round($info.Length / 1KB, 1)) KB"
Write-Host '  双击即可启动（后台起服务 + 托盘常驻）'
