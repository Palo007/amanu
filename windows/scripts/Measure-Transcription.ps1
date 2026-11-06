<#
.SYNOPSIS
Transcribes one recording again on a copy and prints the time of each stage.

.DESCRIPTION
Roadmap-speed step 0. Runs `Amanu.exe --measure-transcription`, which copies the
recording's audio to a temp folder and transcribes only the copy with the local
model. The recording itself is never changed. Prints decode, echo removal, ASR
per track, ASR, total time and the word count. With -Reference, also prints the
WER (word error rate) against a hand-checked transcript of one time window.

Close Amanu first: a running Amanu shares the CPU and makes the numbers wrong.

.EXAMPLE
.\Measure-Transcription.ps1 2026.11.06-1000
.\Measure-Transcription.ps1 2026.11.06-1000 -Reference ..\tests\speed-set\2026.11.06-1000.ref.txt
#>
[CmdletBinding()]
param(
    # A recording folder, or its name under "~\Amanu Recordings".
    [Parameter(Mandatory, Position = 0)][string]$Recording,
    # Engine to use; default is the one the recording was transcribed with.
    [string]$Engine,
    # Reference transcript for WER. A line "# window: <start s>-<end s>", then the
    # text; "#" lines and a leading "[mm:ss speaker]" on a line are ignored.
    # "# speaker: me" compares one side only. A side-by-side check file (P:/G:/=>
    # blocks) is read too: "=> P", "=> G", "=> -" or the right words.
    [string]$Reference,
    # Amanu.exe to run; default is the published build.
    [string]$Exe = (Join-Path $PSScriptRoot '..\artifacts\publish\Amanu.exe'),
    # Where the copy goes; default is a new temp folder.
    [string]$Output,
    # Keep the copy (transcript.json, transcribe.log) after the run.
    [switch]$Keep,
    # Run even when Amanu is open.
    [switch]$Force,
    # Do not transcribe: only score the recording's own transcript.json against -Reference.
    [switch]$ScoreOnly
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $Recording)) {
    $Recording = Join-Path ([Environment]::GetFolderPath('UserProfile')) "Amanu Recordings\$Recording"
}
$Recording = (Resolve-Path $Recording).Path
$clock = [Diagnostics.Stopwatch]::new()
if ($ScoreOnly) {
    $Output = $Recording
    $Keep = $true
}
else {
    $Exe = (Resolve-Path $Exe).Path
    if (-not $Output) {
        $Output = Join-Path ([IO.Path]::GetTempPath()) ("amanu-measure\{0}-{1:yyyyMMdd-HHmmss}" -f (Split-Path $Recording -Leaf), (Get-Date))
    }
    if ((Get-Process Amanu -ErrorAction SilentlyContinue | Where-Object { $_.Path -ne $null }) -and -not $Force) {
        throw 'Amanu is open. Close it first (or pass -Force).'
    }
    $arguments = @('--measure-transcription', "`"$Recording`"", "`"$Output`"")
    if ($Engine) { $arguments += $Engine }
    $clock.Start()
    $process = Start-Process -FilePath $Exe -ArgumentList $arguments -PassThru -Wait -WindowStyle Hidden
    $clock.Stop()
    if ($process.ExitCode -ne 0) {
        $why = Join-Path $Output 'measure.error'
        if (Test-Path $why) { Get-Content $why | Select-Object -First 5 | Write-Host }
        throw "Measurement failed (exit $($process.ExitCode)). Copy: $Output"
    }
}

# Timing lines: "<time> timing <stage> <seconds> s (<detail>)". A score-only run
# reads the recording's own log, which has none before this build.
$timings = foreach ($line in Get-Content (Join-Path $Output 'transcribe.log') -Encoding UTF8) {
    if ($line -match ' timing (\S+) ([\d.]+) s(?: \((.*)\))?$') {
        [pscustomobject]@{ Stage = $Matches[1]; Seconds = [double]::Parse($Matches[2], [Globalization.CultureInfo]::InvariantCulture); Detail = $Matches[3] }
    }
}
$transcript = Get-Content (Join-Path $Output 'transcript.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$words = ($transcript.segments | ForEach-Object { ($_.text -split '\s+' | Where-Object { $_ }).Count } | Measure-Object -Sum).Sum
$meta = Get-Content (Join-Path $Recording 'meta.json') -Raw -Encoding UTF8 | ConvertFrom-Json

Write-Host ''
Write-Host ("Recording : {0} ({1:0.0} min)" -f (Split-Path $Recording -Leaf), ($meta.duration_seconds / 60))
Write-Host ("Engine    : {0}" -f $transcript.engine)
$timings | Format-Table -AutoSize Stage, @{ n = 'Seconds'; e = { '{0,7:0.0}' -f $_.Seconds } }, Detail | Out-Host
Write-Host ("Words     : {0}" -f $words)
if (-not $ScoreOnly) { Write-Host ("Process   : {0:0.0} s (includes start-up)" -f $clock.Elapsed.TotalSeconds) }

function Get-Words([string]$text) {
    # Lower case, letters and digits only: punctuation and case are not errors here.
    ($text.ToLowerInvariant() -replace "[^\p{L}\p{Nd}\s]", ' ') -split '\s+' | Where-Object { $_ }
}

if ($Reference) {
    $lines = Get-Content $Reference -Encoding UTF8
    $window = $lines | Where-Object { $_ -match '^#\s*window:\s*(\d+)\s*-\s*(\d+)' } | Select-Object -First 1
    if (-not $window -or $window -notmatch '(\d+)\s*-\s*(\d+)') { throw "No '# window: <start>-<end>' line in $Reference" }
    $from = [int]$Matches[1] * 1000; $to = [int]$Matches[2] * 1000
    # "# speaker: me" compares one side only.
    $speaker = ($lines | Where-Object { $_ -match '^#\s*speaker:\s*(\S+)' } | Select-Object -First 1) -replace '^#\s*speaker:\s*', ''
    if ($lines | Where-Object { $_ -match '^=>' }) {
        # A side-by-side check file: each block's "=>" says P, G, "-" or the right words.
        $text = @(); $p = ''; $g = ''
        foreach ($line in $lines) {
            if ($line -match '^P:\s?(.*)$') { $p = $Matches[1] }
            elseif ($line -match '^G:\s?(.*)$') { $g = $Matches[1] }
            elseif ($line -match '^=>\s*(.*)$') {
                $answer = $Matches[1].Trim()
                if (-not $answer) { throw "Check not finished: a block in $Reference has an empty '=>'." }
                $text += switch -CaseSensitive ($answer) { 'P' { $p } 'G' { $g } '-' { '' } default { $answer } }
            }
        }
        $expected = @(Get-Words ($text -join ' '))
    }
    else {
        # A line may start with a "[mm:ss speaker]" hint; it is not part of the text.
        $expected = @(Get-Words (($lines | Where-Object { $_ -notmatch '^#' } | ForEach-Object { $_ -replace '^\s*\[[^\]]*\]', '' }) -join ' '))
    }
    $heard = @(Get-Words ((($transcript.segments | Where-Object {
        $_.start_ms -ge $from -and $_.start_ms -lt $to -and (-not $speaker -or $_.speaker -like "$speaker*") } |
        Sort-Object start_ms) | ForEach-Object text) -join ' '))
    # Word-level edit distance.
    $previous = 0..$heard.Count
    for ($i = 1; $i -le $expected.Count; $i++) {
        $row = @($i) + (1..$heard.Count | ForEach-Object { 0 })
        for ($j = 1; $j -le $heard.Count; $j++) {
            $cost = if ($expected[$i - 1] -eq $heard[$j - 1]) { 0 } else { 1 }
            $row[$j] = [Math]::Min([Math]::Min($previous[$j] + 1, $row[$j - 1] + 1), $previous[$j - 1] + $cost)
        }
        $previous = $row
    }
    $errors = $previous[$heard.Count]
    Write-Host ("WER       : {0:0.0} % ({1} errors / {2} reference words, window {3}-{4} s{5})" -f
        (100 * $errors / [Math]::Max(1, $expected.Count)), $errors, $expected.Count, ($from / 1000), ($to / 1000),
        $(if ($speaker) { ", $speaker only" } else { '' }))
}

if ($ScoreOnly) { }
elseif ($Keep) { Write-Host "Copy kept : $Output" }
else { Remove-Item -Recurse -Force $Output }
