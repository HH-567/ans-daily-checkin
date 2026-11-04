# 注册 ANS 每日签到 到 Windows 任务计划程序
#
#   .\scripts\register-task.ps1                        # 默认每天 12:00 跑一次
#   .\scripts\register-task.ps1 -Times 09:00           # 换成别的时间
#   .\scripts\register-task.ps1 -Times 09:00,21:00     # 多个时间点
#   .\scripts\register-task.ps1 -Unregister            # 删除计划任务
#
# 说明：接口是幂等的——已经领过只会返回 duplicate，不会重复加积分，
#      所以加多个时间点也不会重复得分。
#      任务以"仅在用户登录时运行"注册，不需要输入 Windows 密码；
#      若当天错过了触发时间，会在下次开机/登录后尽快补跑。

param(
    [string[]]$Times = @('12:00'),
    [string]$TaskName = 'ANS每日签到',
    [switch]$Unregister
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)

if ($Unregister) {
    $existing = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    if ($existing) {
        Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
        Write-Host "已删除计划任务：$TaskName" -ForegroundColor Yellow
    } else {
        Write-Host "未找到计划任务：$TaskName" -ForegroundColor Yellow
    }
    return
}

# 校验前置条件
$runner = Join-Path $root 'scripts\run-task.ps1'
if (-not (Test-Path $runner)) { throw "缺少 run-task.ps1" }
if (-not (Test-Path (Join-Path $root 'checkin.mjs'))) { throw "缺少 checkin.mjs" }
if (-not (Test-Path (Join-Path $root 'session.json'))) {
    Write-Host '! 尚未导入 Cookie，任务会以"登录失效"退出。请先运行 tools\导入Cookie.bat' -ForegroundColor Yellow
}

$parsedTimes = @()
foreach ($t in $Times) {
    try {
        $parsedTimes += [datetime]::ParseExact($t.Trim(), 'HH:mm', $null)
    } catch {
        throw "时间格式应为 HH:mm，例如 09:00（当前：$t）"
    }
}
if ($parsedTimes.Count -eq 0) { throw '至少需要一个执行时间' }

$powershell = "$env:SystemRoot\System32\WindowsPowerShell\v1.0\powershell.exe"
$action = New-ScheduledTaskAction `
    -Execute $powershell `
    -Argument "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File `"$runner`"" `
    -WorkingDirectory $root

$triggers = @($parsedTimes | ForEach-Object { New-ScheduledTaskTrigger -Daily -At $_ })

$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -ExecutionTimeLimit (New-TimeSpan -Minutes 10) `
    -RestartCount 2 `
    -RestartInterval (New-TimeSpan -Minutes 5)

# 登录用户身份运行，不需要保存密码
$principal = New-ScheduledTaskPrincipal -UserId ([System.Security.Principal.WindowsIdentity]::GetCurrent().Name) -LogonType Interactive -RunLevel Limited

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $triggers `
    -Settings $settings `
    -Principal $principal `
    -Description '每天自动完成 ANS 工作台每日签到与日常任务' `
    -Force | Out-Null

Write-Host ''
Write-Host "✓ 已注册计划任务：$TaskName" -ForegroundColor Green
Write-Host "  执行时间：每天 $(($parsedTimes | ForEach-Object { $_.ToString('HH:mm') }) -join ' / ')"
Write-Host "  执行脚本：$runner"
Write-Host "  运行日志：$(Join-Path $root 'logs\last-run.txt')"
Write-Host ''
Write-Host '可用的验证命令：' -ForegroundColor Cyan
Write-Host "  Start-ScheduledTask -TaskName '$TaskName'     # 立即触发一次"
Write-Host "  Get-ScheduledTaskInfo -TaskName '$TaskName'   # 查看上次结果"
Write-Host "  .\scripts\register-task.ps1 -Unregister               # 删除任务"
