# Update the daily trigger times of the "ANS Daily Check-in" scheduled task.
#
#   .\set-schedule.ps1 -Times "00:10,09:00,16:30"
#
# Only the triggers are replaced; the action, principal and settings are kept.
# Prints one line of JSON on success. On failure writes to stderr and exits non-zero.
#
# Messages are intentionally ASCII-only: Windows PowerShell 5.1 reads .ps1 files
# without a BOM as ANSI, which garbles any non-ASCII text and breaks parsing.

param(
    [Parameter(Mandatory = $true)][string]$Times,
    [string]$TaskName = 'ANS Daily Check-in'
)

$ErrorActionPreference = 'Stop'

# Resolve the task by name; fall back to a wildcard so the localized name still matches.
function Get-TargetTask([string]$name) {
    $t = Get-ScheduledTask -TaskName $name -ErrorAction SilentlyContinue
    if ($t) { return $t }
    # The task name is Chinese ("ANS每日签到") when registered by the app.
    $t = Get-ScheduledTask -TaskName 'ANS*' -ErrorAction SilentlyContinue | Select-Object -First 1
    return $t
}

try {
    $task = Get-TargetTask $TaskName
    if (-not $task) {
        throw "Scheduled task not found. Enable scheduled runs in the app first."
    }
    $TaskName = $task.TaskName

    $parsed = @()
    foreach ($item in ($Times -split ',')) {
        $t = $item.Trim()
        if (-not $t) { continue }
        $dt = [datetime]::MinValue
        if (-not [datetime]::TryParseExact($t, 'HH:mm', $null, [System.Globalization.DateTimeStyles]::None, [ref]$dt)) {
            throw "Invalid time: $t (expected HH:mm, e.g. 09:00)"
        }
        $parsed += $dt
    }

    $parsed = @($parsed | Sort-Object -Unique)
    if ($parsed.Count -lt 1) { throw 'At least one time is required.' }
    if ($parsed.Count -gt 8) { throw 'At most 8 times are supported.' }

    $triggers = @($parsed | ForEach-Object { New-ScheduledTaskTrigger -Daily -At $_ })
    Set-ScheduledTask -TaskName $TaskName -Trigger $triggers | Out-Null

    $info = Get-ScheduledTaskInfo -TaskName $TaskName
    $timesOut = @($parsed | ForEach-Object { $_.ToString('HH:mm') })
    $next = ''
    if ($info.NextRunTime) { $next = $info.NextRunTime.ToString('yyyy-MM-dd HH:mm:ss') }

    [ordered]@{ ok = $true; times = $timesOut; next = $next } | ConvertTo-Json -Compress
    exit 0
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    exit 1
}
