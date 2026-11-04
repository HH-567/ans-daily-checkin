# ANS 每日签到 · 计划任务执行器
# 供 Windows 任务计划程序调用：解析 node 路径 → 执行 checkin.mjs → 落盘输出
# 退出码原样透出，方便任务计划程序记录结果。

$ErrorActionPreference = 'Stop'

# 让 PowerShell 按 UTF-8 读取 node 的输出，否则中文日志会变成乱码
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$logDir = Join-Path $root 'logs'
if (-not (Test-Path $logDir)) { New-Item -ItemType Directory -Path $logDir | Out-Null }

$outFile = Join-Path $logDir 'last-run.txt'

function Write-RunLog([string]$text) {
    $stamp = Get-Date -Format 'yyyy-MM-dd HH:mm:ss'
    "[$stamp] $text" | Out-File -FilePath $outFile -Append -Encoding utf8
}

Write-RunLog '---- run-task.ps1 start ----'

# 1. 定位 node.exe（优先 PATH，其次常见安装位置）
$nodeCmd = Get-Command node -ErrorAction SilentlyContinue
$nodeExe = if ($nodeCmd) { $nodeCmd.Source } else { $null }
if (-not $nodeExe) {
    foreach ($candidate in @(
            "$env:ProgramFiles\nodejs\node.exe",
            "${env:ProgramFiles(x86)}\nodejs\node.exe",
            "$env:LOCALAPPDATA\Programs\nodejs\node.exe",
            "$env:APPDATA\nvm\node.exe")) {
        if (Test-Path $candidate) { $nodeExe = $candidate; break }
    }
}
if (-not $nodeExe) {
    Write-RunLog 'FAIL: node.exe not found. Install Node.js or re-run register-task.ps1.'
    exit 127
}
Write-RunLog "node: $nodeExe"

Set-Location $root

# 2. 整体重试：脚本内部已有单任务重试，这里再兜一层（仅对"部分失败"重试）
$maxAttempts = 2
$exitCode = 1
for ($i = 1; $i -le $maxAttempts; $i++) {
    Write-RunLog "attempt $i/$maxAttempts"
    $output = & $nodeExe 'checkin.mjs' 2>&1 | Out-String
    $exitCode = $LASTEXITCODE
    foreach ($line in ($output -split "`r?`n")) {
        if ($line.Trim()) { Write-RunLog "  $line" }
    }
    Write-RunLog "attempt $i exit=$exitCode"

    if ($exitCode -eq 0 -or $exitCode -eq 2 -or $exitCode -eq 4) { break }  # 成功 / 登录失效 / 服务端拒绝（重试无意义）
    if ($i -lt $maxAttempts) { Start-Sleep -Seconds 60 }
}

Write-RunLog "---- run-task.ps1 end (exit=$exitCode) ----"
exit $exitCode
