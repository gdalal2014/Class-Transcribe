<#
.SYNOPSIS
A dry run of a ClassScribe branch: published to a side folder, recorded on the real path a class takes, transcribed by a
local stand-in for ElevenLabs, and checked. See README.md beside this script.

.DESCRIPTION
Double-click Start-DryRun.cmd beside this script, so that the window and its recorder start outside any agent's or
editor's job. The script:
  1. refuses while a class records (the run files; recording.lock is never opened) or while the side copy runs;
  2. lists %LOCALAPPDATA%\ClassScribe, %APPDATA%\ClassScribe and %USERPROFILE%\ClassScribe (names, sizes, times);
  3. publishes the worktree it lives in (or -Source) to %LOCALAPPDATA%\Programs\ClassScribe-Dry, never single-file and
     never through an installer; with -Exe it uses that exe instead;
  4. unless -Real, starts the stand-in for ElevenLabs (ElevenLabsStub.cs) on a free port of 127.0.0.1;
  5. sets, in its own process only (never at user scope): a dummy ELEVENLABS_API_KEY (with -Real, the user-scope key,
     after a typed YES), ElevenLabs__BaseUrl at the stand-in, Drive__Enabled=false, and CLASSSCRIBE_SETTINGS_FOLDER at a
     folder of the dry run's own, seeded with a copy of %APPDATA%\ClassScribe\appsettings.json when there is one;
  6. starts the window: record "<Name>" --app <App> --mic "<Mic>" --mic-follows <Follows> --new
     --output "%USERPROFILE%\ClassScribe dry runs", and waits until the run is over and the window is closed;
  7. checks the run file (the recorder ran separately, the transcript was saved, the run was seen), the transcript and
     any -Expect files, the recording's log, and the stand-in's log;
  8. stops the stand-in, removes the side copy (unless -Keep), lists the three folders again and names each change it
     did not expect;
  9. writes the branch, the commit it published and the result to %LOCALAPPDATA%\ClassScribe-DryRuns\runs.log.

Everything the dry run writes of its own goes to %LOCALAPPDATA%\ClassScribe-DryRuns\<time>\. The script is ASCII only and
runs under Windows PowerShell 5.1. Dot-sourced (. .\Start-DryRun.ps1) it defines its functions and runs nothing, which
is how the tests reach them (DryRunScriptTests).

.EXAMPLE
Start-DryRun.cmd
The full dry run of the worktree this script is in: Chrome and the microphone array, about 15 minutes.

.EXAMPLE
Start-DryRun.cmd -Short -NoMic
Five minutes of the class alone, stopped by itself: for a branch whose head moved after its dry run, and for hotfixes.
#>
[CmdletBinding()]
param(
    # The worktree to publish: by default the one this script is in.
    [string]$Source,
    # An installed ClassScribe.exe to run instead of publishing the worktree.
    [string]$Exe,
    # Sends the recording to ElevenLabs itself, with the user-scope key, and is billed: only at Gulzar's word.
    [switch]$Real,
    # The recording's name.
    [string]$Name = 'Dry run',
    # The app whose sound is the class: chrome, or the test player with -Player.
    [string]$App,
    # The microphone, by part of its name, as the class launchers name it.
    [string]$Mic = 'Microphone Array',
    # Records the class alone, without the microphone.
    [switch]$NoMic,
    # The app the microphone follows: the class app by default; none records the microphone throughout.
    [string]$Follows,
    # A saved class to record (record --class), for builds that have saved classes.
    [string]$Class,
    # A camera, by part of its name, to record in person with (record --in-person --camera), for builds that have it.
    [string]$Camera,
    # A WAV clip the test player plays aloud as the class, from a process of its own: only at Gulzar's word.
    [string]$Player,
    # Settings for this dry run only, as Key:Name=value, set as environment overrides in the script's own process.
    [string[]]$Setting = @(),
    # Files expected beside the recording, by their endings after the base name, such as .srt or .vtt.
    [string[]]$Expect = @(),
    # Five minutes, stopped by itself, no pause or close.
    [switch]$Short,
    # Keeps the side copy after the dry run.
    [switch]$Keep
)

$script:BoundNames = @($PSBoundParameters.Keys)

# The states in which a run file says a run is under way.
$script:ActiveStates = @('Starting', 'Recording', 'Stopping', 'Transcribing')

# The dummy key: never a real one, so a wrong address can never bill.
$script:DummyKey = 'dry-run-dummy-key-not-an-elevenlabs-key'

# The environment variables the dry run sets in its own process, restored at its end.
$script:Variables = @('ELEVENLABS_API_KEY', 'ElevenLabs__BaseUrl', 'Drive__Enabled', 'CLASSSCRIBE_SETTINGS_FOLDER')

$script:Invariant = [Globalization.CultureInfo]::InvariantCulture

# Lists the files under Root, each with its path relative to Root, its size and its time (UTC); nothing for a folder
# that does not exist. Each item's Line is the three as one line of text, for the listings kept in the dry run's folder.
function Get-FolderListing {
    param([Parameter(Mandatory = $true)][string]$Root)

    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { return }
    $full = (Resolve-Path -LiteralPath $Root).ProviderPath.TrimEnd('\')
    Get-ChildItem -LiteralPath $full -Recurse -File -Force -ErrorAction SilentlyContinue | ForEach-Object {
        $relative = $_.FullName.Substring($full.Length + 1)
        $time = $_.LastWriteTimeUtc.ToString('yyyy-MM-ddTHH:mm:ss.fffffffZ', $script:Invariant)
        [pscustomobject]@{ Path = $relative; Size = $_.Length; Time = $time; Line = "$relative`t$($_.Length)`t$time" }
    } | Sort-Object Path
}

# Compares two listings: each file added (New), changed in size or time (Changed) or gone (Removed), by path; a changed
# or removed file keeps the time it had before (Before).
function Compare-FolderListing {
    param($Before, $After)

    $old = @{}
    foreach ($item in @($Before)) { if ($item) { $old[$item.Path] = $item } }
    $new = @{}
    foreach ($item in @($After)) { if ($item) { $new[$item.Path] = $item } }
    $changes = @()
    foreach ($path in $new.Keys) {
        if (-not $old.ContainsKey($path)) { $changes += [pscustomobject]@{ Change = 'New'; Path = $path; Before = $null } }
        elseif ($old[$path].Size -ne $new[$path].Size -or $old[$path].Time -ne $new[$path].Time) { $changes += [pscustomobject]@{ Change = 'Changed'; Path = $path; Before = $old[$path].Time } }
    }
    foreach ($path in $old.Keys) {
        if (-not $new.ContainsKey($path)) { $changes += [pscustomobject]@{ Change = 'Removed'; Path = $path; Before = $old[$path].Time } }
    }
    $changes | Sort-Object Path
}

# A time from a run file as UTC: Windows PowerShell 5.1 reads it as text, PowerShell 7 as a date.
function ConvertTo-UtcTime {
    param($Value)

    if ($Value -is [DateTime]) { return $Value.ToUniversalTime() }
    if ($Value -is [DateTimeOffset]) { return $Value.UtcDateTime }
    return [DateTimeOffset]::Parse([string]$Value, $script:Invariant).UtcDateTime
}

# The run files in RunsFolder, read; a file that cannot be read is passed over, and request files are not run files.
function Get-RunFiles {
    param([Parameter(Mandatory = $true)][string]$RunsFolder)

    if (-not (Test-Path -LiteralPath $RunsFolder -PathType Container)) { return }
    Get-ChildItem -LiteralPath $RunsFolder -Filter '*.json' -File -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -notlike '*.request.json' } |
        ForEach-Object {
            try { Get-Content -LiteralPath $_.FullName -Raw -ErrorAction Stop | ConvertFrom-Json }
            catch { Write-Verbose "Passed over $($_.Exception.Message)" }
        } |
        Where-Object { $_ -and $_.runId }
}

# True when the run's process is alive and is the one that wrote the run file: the same id, started within 2 seconds of
# the time the file gives, as ClassScribe's own ProcessLiveness tells them apart.
function Test-RunProcess {
    param([Parameter(Mandatory = $true)]$Run)

    $id = 0
    if (-not [int]::TryParse([string]$Run.processId, [ref]$id) -or $id -le 0) { return $false }
    $process = Get-Process -Id $id -ErrorAction SilentlyContinue
    if (-not $process) { return $false }
    try {
        $recorded = ConvertTo-UtcTime $Run.processStartedUtc
        return [Math]::Abs(($process.StartTime.ToUniversalTime() - $recorded).TotalSeconds) -le 2
    }
    catch {
        return $false
    }
}

# The runs under way now: a run file in an active state whose process is the one that wrote it. This is how the dry run
# tells that a class records; it never opens recording.lock.
function Get-LiveRuns {
    param([Parameter(Mandatory = $true)][string]$RunsFolder)

    Get-RunFiles -RunsFolder $RunsFolder | Where-Object { $script:ActiveStates -contains $_.state -and (Test-RunProcess -Run $_) }
}

# The changes a dry run does not expect. In %LOCALAPPDATA%\ClassScribe (Area LocalAppData) it expects its own run file,
# the recorder's log and the lock file to be added or changed, and files of the runs folder last written more than 30
# days before to go, since any start of ClassScribe prunes the run files of runs that old; anything else there, and any
# change to the other two folders, is unexpected.
function Get-UnexpectedChanges {
    param($Changes, [Parameter(Mandatory = $true)][string]$Area, [string]$RunId)

    $expected = @("runs\$RunId.json", 'recorder.log', 'recorder.old.log', 'recording.lock')
    $pruneBefore = (Get-Date).ToUniversalTime().AddDays(-30)
    foreach ($change in @($Changes)) {
        if (-not $change) { continue }
        $isExpected = $false
        if ($Area -eq 'LocalAppData') {
            if ($change.Change -ne 'Removed') { $isExpected = $expected -contains $change.Path }
            elseif ($change.Path -like 'runs\*' -and $change.Before) {
                $isExpected = (ConvertTo-UtcTime $change.Before) -lt $pruneBefore
            }
        }
        if (-not $isExpected) { $change }
    }
}

# What is wrong with a finished dry run, by its run file: the recorder ran in the window, the run did not finish or was
# not transcribed, the transcript is missing, or no window showed its end. Nothing for a run that passed.
function Get-RunProblems {
    param([Parameter(Mandatory = $true)]$Run, [bool]$Transcribe = $true)

    if ($Run.home -ne 'Separate') {
        "The recorder ran in the window (home $($Run.home)), not separately: the window was started inside a job that ends its processes (start the dry run by double-clicking Start-DryRun.cmd), $env:LOCALAPPDATA\ClassScribe could not be written, or the recorder failed to start twice."
    }
    if ($Run.state -ne 'Finished') { "The run ended in state $($Run.state), not Finished." }
    $wanted = if ($Transcribe) { 'Transcribed' } else { 'Recorded' }
    if (-not $Run.outcome) {
        "The run has no outcome."
    }
    elseif ($Run.outcome.kind -ne $wanted) {
        "The run ended as $($Run.outcome.kind), not $($wanted): $($Run.outcome.message)"
    }
    if ($Transcribe) {
        $transcript = if ($Run.outcome) { [string]$Run.outcome.transcriptPath } else { '' }
        if (-not $transcript -or -not (Test-Path -LiteralPath $transcript -PathType Leaf)) { "There is no transcript$(if ($transcript) { " at $transcript" })." }
    }
    if ($Run.seen -ne $true) { 'The run file is not marked seen: no window showed the end of the run.' }
}

# The warning and error lines of a recording's log (nothing when there is no log).
function Get-LogWarnings {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    Get-Content -LiteralPath $Path -Encoding UTF8 | Where-Object { $_ -match '^\d\d:\d\d:\d\d\s+(WARNING|ERROR)\s' }
}

# What the stand-in's log says: the transcripts it answered with the number of keyterms each carried, the reads of the
# account, the requests it refused or failed, and the requests to paths it does not know.
function Read-StandInLog {
    param([Parameter(Mandatory = $true)][string]$Path)

    $summary = [pscustomobject]@{ Transcripts = 0; Keyterms = @(); Accounts = 0; Refused = @(); Unexpected = @() }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $summary }
    foreach ($line in Get-Content -LiteralPath $Path -Encoding UTF8) {
        if ($line -match 'POST /v1/speech-to-text: .*answered 200') {
            $summary.Transcripts++
            if ($line -match '; (\d+) keyterms;') { $summary.Keyterms += [int]$Matches[1] }
        }
        elseif ($line -match 'GET /v1/user/subscription: answered 200') { $summary.Accounts++ }
        elseif ($line -match 'UNEXPECTED: ') { $summary.Unexpected += $line }
        elseif ($line -match 'answered [45]\d\d') { $summary.Refused += $line }
    }
    $summary
}

# Options given as one string each, as Start-DryRun.cmd hands them on (Windows PowerShell's -File passes ".srt,.vtt" as
# one string): each split at Separator and trimmed, empty ones left out.
function Split-Items {
    param([string[]]$Items, [Parameter(Mandatory = $true)][string]$Separator)

    foreach ($item in @($Items)) {
        foreach ($part in ([string]$item -split [regex]::Escape($Separator))) { if ($part.Trim()) { $part.Trim() } }
    }
}

# Runs a native command and returns its output; its error stream is dropped, since Windows PowerShell 5.1 turns each of
# its lines into an error, which stops the dry run under ErrorActionPreference Stop.
function Invoke-Native {
    param([Parameter(Mandatory = $true)][scriptblock]$Command)

    $ErrorActionPreference = 'Continue'
    & $Command 2>$null
}

# One argument quoted for a Windows command line, as the C runtime reads it back.
function ConvertTo-Argument {
    param([AllowEmptyString()][string]$Text)

    if ($Text -and $Text -notmatch '[\s"]') { return $Text }
    $escaped = [regex]::Replace($Text, '(\\*)"', { param($m) $m.Groups[1].Value + $m.Groups[1].Value + '\"' })
    $escaped = [regex]::Replace($escaped, '(\\+)$', { param($m) $m.Groups[1].Value + $m.Groups[1].Value })
    return '"' + $escaped + '"'
}

# The ClassScribe processes started from ExePath still running: windows and recorders alike.
function Get-CopyProcesses {
    param([Parameter(Mandatory = $true)][string]$ExePath)

    Get-Process -Name ClassScribe -ErrorAction SilentlyContinue | Where-Object {
        try { $_.Path -and ($_.Path -ieq $ExePath) } catch { $false }
    }
}

# True when the file holds Text, as UTF-16 (a .NET string literal, at either byte alignment) or as ASCII.
function Test-FileHoldsText {
    param([Parameter(Mandatory = $true)][string]$Path, [Parameter(Mandatory = $true)][string]$Text)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return $false }
    $bytes = [IO.File]::ReadAllBytes($Path)
    if ([Text.Encoding]::ASCII.GetString($bytes).Contains($Text)) { return $true }
    if ([Text.Encoding]::Unicode.GetString($bytes).Contains($Text)) { return $true }
    if ($bytes.Length -gt 1) { return [Text.Encoding]::Unicode.GetString($bytes, 1, $bytes.Length - 1).Contains($Text) }
    return $false
}

# The record command line of the dry run, as arguments.
function Get-RecordArguments {
    param([string]$Name, [string]$App, [string]$Mic, [bool]$NoMic, [string]$Follows, [string]$Class, [string]$Camera, [bool]$Short, [string]$Output)

    $arguments = @('record')
    if ($Class) { $arguments += @('--class', $Class) }
    elseif ($Camera) {
        $arguments += @($Name, '--in-person')
        if (-not $NoMic -and $Mic) { $arguments += $Mic }
        $arguments += @('--camera', $Camera)
    }
    else {
        $arguments += @($Name, '--app', $App)
        if (-not $NoMic) { $arguments += @('--mic', $Mic, '--mic-follows', $Follows) }
    }
    if ($Short) { $arguments += @('--minutes', '5') }
    $arguments += @('--new', '--output', $Output)
    $arguments
}

# Writes a line in a colour, and keeps it in the dry run's console record.
function Write-Line {
    param([string]$Text = '', [ConsoleColor]$Color = [ConsoleColor]::Gray)
    Write-Host $Text -ForegroundColor $Color
}

# Starts the stand-in: builds ElevenLabsStub.cs (no build servers left behind), then runs it on a free port of 127.0.0.1
# and waits for its ready file. Returns what it needs to be stopped and read, or null when it did not start.
function Start-StandIn {
    param([Parameter(Mandatory = $true)][string]$StubFile, [Parameter(Mandatory = $true)][string]$Folder)

    $log = Join-Path $Folder 'stand-in.log'
    $ready = Join-Path $Folder 'stand-in.ready'
    $build = Start-Process -FilePath 'dotnet' -ArgumentList "build $(ConvertTo-Argument $StubFile) --disable-build-servers -nologo" `
        -NoNewWindow -Wait -PassThru -RedirectStandardOutput (Join-Path $Folder 'stand-in.build.log') -RedirectStandardError (Join-Path $Folder 'stand-in.build.err.txt')
    if ($build.ExitCode -ne 0) {
        Write-Line "The stand-in did not build (exit code $($build.ExitCode)); see $(Join-Path $Folder 'stand-in.build.log')." Red
        return $null
    }
    $arguments = "run --no-build $(ConvertTo-Argument $StubFile) -- 0 $(ConvertTo-Argument $log) --ready $(ConvertTo-Argument $ready)"
    $process = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -WindowStyle Hidden -PassThru `
        -RedirectStandardOutput (Join-Path $Folder 'stand-in.console.txt') -RedirectStandardError (Join-Path $Folder 'stand-in.errors.txt')
    $deadline = (Get-Date).AddSeconds(60)
    while (-not (Test-Path -LiteralPath $ready) -and -not $process.HasExited -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 250 }
    if (-not (Test-Path -LiteralPath $ready)) {
        Write-Line "The stand-in did not start; see $(Join-Path $Folder 'stand-in.errors.txt')." Red
        if (-not $process.HasExited) { Stop-Process -Id $process.Id -ErrorAction SilentlyContinue }
        return $null
    }
    $fields = (Get-Content -LiteralPath $ready -Raw).Trim() -split ' '
    [pscustomobject]@{ Launcher = $process; ProcessId = [int]$fields[0]; Port = [int]$fields[1]; Url = "http://127.0.0.1:$($fields[1])/"; Log = $log }
}

# Stops the stand-in this dry run started, and the dotnet process that ran it.
function Stop-StandIn {
    param($StandIn)

    if (-not $StandIn) { return }
    Stop-Process -Id $StandIn.ProcessId -ErrorAction SilentlyContinue
    if (-not $StandIn.Launcher.WaitForExit(5000)) { Stop-Process -Id $StandIn.Launcher.Id -ErrorAction SilentlyContinue }
}

# The dry run's run file: one added since the dry run began, written by the copy it runs.
function Find-DryRun {
    param([string]$RunsFolder, [string]$ExePath, [string[]]$Known)

    Get-RunFiles -RunsFolder $RunsFolder |
        Where-Object { $Known -notcontains "runs\$($_.runId).json" -and [string]$_.exePath -ieq $ExePath } |
        Sort-Object { [string]$_.runId } |
        Select-Object -Last 1
}

# Waits until the dry run's run is over and no ClassScribe of the copy is left, while O opens the window again (then A
# in its menu shows the run) and X gives up. Returns the run file, or null when none was written.
function Wait-DryRun {
    param([string]$RunsFolder, [string]$ExePath, [string[]]$Known, [string]$WorkingFolder)

    $shown = ''
    while ($true) {
        $run = Find-DryRun -RunsFolder $RunsFolder -ExePath $ExePath -Known $Known
        $left = @(Get-CopyProcesses -ExePath $ExePath).Count
        $state = if ($run) { [string]$run.state } else { 'not started' }
        if ($run -and $script:ActiveStates -notcontains $state -and $left -eq 0) { return $run }
        $now = "Run: $state; ClassScribe processes of this copy: $left"
        if ($now -ne $shown) { Write-Line "  $now   (O opens the window again, X gives up)" DarkGray; $shown = $now }
        $key = $null
        try { if ([Console]::KeyAvailable) { $key = [Console]::ReadKey($true).Key } } catch { $key = $null }
        if ($key -eq [ConsoleKey]::O) {
            Start-Process -FilePath $ExePath -WorkingDirectory $WorkingFolder | Out-Null
            Write-Line '  The side copy opens in a window of its own: press A in its menu to see the recording.'
        }
        elseif ($key -eq [ConsoleKey]::X) {
            if ($left -eq 0) { return $run }
            Write-Line "  $left ClassScribe process(es) of this copy are still running: close them first (Q twice, then Enter, in each window)." Yellow
        }
        Start-Sleep -Milliseconds 500
    }
}

# Saves a listing to the dry run's folder.
function Save-Listing {
    param($Listing, [string]$Path)
    @($Listing | ForEach-Object { $_.Line }) | Set-Content -LiteralPath $Path -Encoding UTF8
}

# Where a dry run reads and writes: ClassScribe's three data folders (and the runs folder in the first), the side copy,
# the kit's own folder, the folder dry runs record into, and the user's settings file; this Windows user's, unless a test
# gives others.
function Get-DryRunRoots {
    @{
        LocalAppData = Join-Path $env:LOCALAPPDATA 'ClassScribe'
        AppData      = Join-Path $env:APPDATA 'ClassScribe'
        UserProfile  = Join-Path $env:USERPROFILE 'ClassScribe'
        Side         = Join-Path $env:LOCALAPPDATA 'Programs\ClassScribe-Dry'
        DryRuns      = Join-Path $env:LOCALAPPDATA 'ClassScribe-DryRuns'
        Recordings   = Join-Path $env:USERPROFILE 'ClassScribe dry runs'
        UserSettings = Join-Path $env:APPDATA 'ClassScribe\appsettings.json'
    }
}

# The dry run from start to end, with the script's options, in the folders Roots names; returns the exit code: 0
# passed, 1 a check failed, 2 refused (nothing done, or an option this build does not have).
function Invoke-DryRun {
    param([hashtable]$Roots = (Get-DryRunRoots))

    # Any error of a cmdlet stops the dry run and is reported as its failure (the catch below); native commands go
    # through Invoke-Native.
    $ErrorActionPreference = 'Stop'
    $started = Get-Date
    $dryRuns = $Roots.DryRuns
    $folder = Join-Path $dryRuns $started.ToString('yyyy-MM-dd_HH-mm-ss', $script:Invariant)
    New-Item -ItemType Directory -Force -Path $folder | Out-Null
    $settings = @(Split-Items -Items $Setting -Separator ';')
    $expected = @(Split-Items -Items $Expect -Separator ',')
    $settingVariables = @($settings | ForEach-Object { (($_ -split '=', 2)[0]) -replace ':', '__' })
    $saved = @{}
    foreach ($variable in $script:Variables + $settingVariables) { $saved[$variable] = [Environment]::GetEnvironmentVariable($variable, 'Process') }
    $refused = $true
    $standIn = $null
    $player = $null
    $run = $null
    $areas = $null
    $before = $null
    $exePath = ''
    $side = ''
    $branch = ''
    $commit = ''
    $build = ''
    $uncommitted = 0
    $problems = New-Object System.Collections.Generic.List[string]
    $notes = New-Object System.Collections.Generic.List[string]
    try { Start-Transcript -LiteralPath (Join-Path $folder 'console.txt') | Out-Null } catch { Write-Verbose 'No record of the console.' }
    try {
        try { $Host.UI.RawUI.WindowTitle = 'ClassScribe dry run' } catch { Write-Verbose 'No window title.' }
        Write-Line 'ClassScribe dry run' Cyan
        Write-Line "Its own files: $folder"
        Write-Line ''

        # The options, and what Gulzar must agree to, before anything is done.
        $sourceFolder = [IO.Path]::GetFullPath($(if ($Source) { $Source } else { Join-Path $PSScriptRoot '..\..' }))
        $appName = if ($App) { $App } elseif ($Player) { 'ClassScribeTestPlayer' } else { 'chrome' }
        $followed = if ($Follows) { $Follows } else { $appName }
        if (-not $Exe -and -not (Test-Path -LiteralPath (Join-Path $sourceFolder 'src\ClassScribe\ClassScribe.csproj'))) {
            Write-Line "No ClassScribe worktree at $sourceFolder (src\ClassScribe\ClassScribe.csproj): give -Source <worktree>, or -Exe <installed exe>." Red
            return 2
        }
        if ($Exe -and -not (Test-Path -LiteralPath $Exe -PathType Leaf)) { Write-Line "No exe at $Exe." Red; return 2 }
        if ($Player -and -not (Test-Path -LiteralPath $Player -PathType Leaf)) { Write-Line "No clip at $Player for the test player." Red; return 2 }
        foreach ($item in $settings) {
            if ($item -notmatch '^[A-Za-z][A-Za-z0-9]*(:[A-Za-z][A-Za-z0-9]*)+=') { Write-Line "-Setting $item is not Section:Name=value." Red; return 2 }
        }
        if ("$Name$Mic$Class$Camera$appName$followed" -match '"') { Write-Line 'A name with a double quote cannot be passed on.' Red; return 2 }
        if ($Real) {
            Write-Line 'With -Real the recording goes to ElevenLabs itself, with your key, and is billed (about 5 cents for 15 minutes).' Yellow
            if ((Read-Host 'Type YES to go on') -cne 'YES') { Write-Line 'Not started.'; return 2 }
            if (-not [Environment]::GetEnvironmentVariable('ELEVENLABS_API_KEY', 'User')) { Write-Line 'ELEVENLABS_API_KEY is not set for your user, so a real dry run cannot run.' Red; return 2 }
        }
        if ($Player) {
            Write-Line "With -Player the test player plays $Player aloud through the speakers, as the class." Yellow
            if ((Read-Host 'Type PLAY to let it play') -cne 'PLAY') { Write-Line 'Not started.'; return 2 }
        }

        # 1. No dry run while a class records (the run files; recording.lock is never opened), or while the copy runs.
        $runsFolder = Join-Path $Roots.LocalAppData 'runs'
        $live = @(Get-LiveRuns -RunsFolder $runsFolder)
        if ($live.Count -gt 0) {
            foreach ($liveRun in $live) { Write-Line "A run is under way: $($liveRun.runId), $($liveRun.state), from $($liveRun.exePath)." Yellow }
            Write-Line 'No dry run while a class records or is transcribed. Start it again afterwards.' Yellow
            return 2
        }
        $side = $Roots.Side
        $exePath = if ($Exe) { (Resolve-Path -LiteralPath $Exe).ProviderPath } else { Join-Path $side 'ClassScribe.exe' }
        $running = @(Get-CopyProcesses -ExePath $exePath)
        if ($running.Count -gt 0) {
            Write-Line "ClassScribe from $exePath is running (process $(($running | ForEach-Object { $_.Id }) -join ', ')): close it first." Yellow
            return 2
        }
        $refused = $false

        # Which commit is published.
        if (-not $Exe) {
            $branch = (Invoke-Native { git -C $sourceFolder branch --show-current }) -join ''
            $commit = (Invoke-Native { git -C $sourceFolder rev-parse HEAD }) -join ''
            $uncommitted = @(Invoke-Native { git -C $sourceFolder status --porcelain }).Count
            Write-Line "Worktree $sourceFolder, branch $branch, commit $commit"
            if ($uncommitted -gt 0) {
                Write-Line "  $uncommitted changed file(s) not committed are published too: this dry run does not test a commit." Yellow
                $notes.Add("$uncommitted uncommitted change(s) were published with the commit")
            }
        }

        # 2. The three folders before.
        $areas = [ordered]@{ LocalAppData = $Roots.LocalAppData; AppData = $Roots.AppData; UserProfile = $Roots.UserProfile }
        $listed = @{}
        foreach ($area in $areas.Keys) {
            $listed[$area] = @(Get-FolderListing -Root $areas[$area])
            Save-Listing -Listing $listed[$area] -Path (Join-Path $folder "before-$area.txt")
        }
        $before = $listed
        Write-Line 'Listed the data folders:'
        foreach ($area in $areas.Keys) { Write-Line "  $($areas[$area]): $($before[$area].Count) file(s)" }

        # 3. The side copy, published from the worktree; never single-file, never through an installer.
        if ($Exe) {
            Write-Line "Using $exePath as it is (-Exe): nothing is published."
        }
        else {
            if (Test-Path -LiteralPath $side) { Remove-Item -LiteralPath $side -Recurse -Force }
            $revision = if ($commit) { "$commit-dry" } else { 'unknown-dry' }
            Write-Line "Publishing to $side (about a minute)..."
            $arguments = @('publish', (ConvertTo-Argument (Join-Path $sourceFolder 'src\ClassScribe')), '-c', 'Release', '-r', 'win-x64',
                '--self-contained', 'false', '-o', (ConvertTo-Argument $side), "-p:SourceRevisionId=$revision",
                '-p:ContinuousIntegrationBuild=true', '--disable-build-servers', '-nologo') -join ' '
            $publish = Start-Process -FilePath 'dotnet' -ArgumentList $arguments -NoNewWindow -Wait -PassThru `
                -RedirectStandardOutput (Join-Path $folder 'publish.log') -RedirectStandardError (Join-Path $folder 'publish.errors.txt')
            if ($publish.ExitCode -ne 0) {
                Write-Line "The publish failed (exit code $($publish.ExitCode)). Its errors:" Red
                Get-Content -LiteralPath (Join-Path $folder 'publish.log') | Where-Object { $_ -match 'error' } | Select-Object -Last 8 | ForEach-Object { Write-Line "  $_" Red }
                $problems.Add("The publish failed (exit code $($publish.ExitCode)); see $(Join-Path $folder 'publish.log').")
                return 1
            }
            Start-Sleep -Seconds 2
            if (-not (Test-Path -LiteralPath $exePath)) {
                $problems.Add("$exePath was missing after the publish: Bitdefender may have quarantined it. Allow that exe in Bitdefender and start the dry run again.")
                return 1
            }
        }
        $build = (Get-Item -LiteralPath $exePath).VersionInfo.ProductVersion
        Write-Line "Build $build"

        # 4. The stand-in for ElevenLabs, on 127.0.0.1 only.
        if (-not $Real) {
            Write-Line 'Starting the stand-in for ElevenLabs...'
            $standIn = Start-StandIn -StubFile (Join-Path $PSScriptRoot 'ElevenLabsStub.cs') -Folder $folder
            if (-not $standIn) { $problems.Add("The stand-in did not start; see the stand-in files in $folder."); return 1 }
            Write-Line "The stand-in answers at $($standIn.Url) (process $($standIn.ProcessId)); its log is $($standIn.Log)."
        }

        # 5. The environment, in this process only (never at user scope), inherited by the window and its recorder.
        $settingsFolder = Join-Path $folder 'settings'
        New-Item -ItemType Directory -Force -Path $settingsFolder | Out-Null
        $userSettings = $Roots.UserSettings
        if (Test-Path -LiteralPath $userSettings) { Copy-Item -LiteralPath $userSettings -Destination $settingsFolder }
        [Environment]::SetEnvironmentVariable('Drive__Enabled', 'false', 'Process')
        [Environment]::SetEnvironmentVariable('CLASSSCRIBE_SETTINGS_FOLDER', $settingsFolder, 'Process')
        foreach ($item in $settings) {
            $pair = $item -split '=', 2
            [Environment]::SetEnvironmentVariable(($pair[0] -replace ':', '__'), $pair[1], 'Process')
        }
        if ($Real) {
            [Environment]::SetEnvironmentVariable('ELEVENLABS_API_KEY', [Environment]::GetEnvironmentVariable('ELEVENLABS_API_KEY', 'User'), 'Process')
            [Environment]::SetEnvironmentVariable('ElevenLabs__BaseUrl', $null, 'Process')
            Write-Line 'Set for this dry run only: your key, ElevenLabs itself (billed), Drive__Enabled false, CLASSSCRIBE_SETTINGS_FOLDER.'
        }
        else {
            [Environment]::SetEnvironmentVariable('ELEVENLABS_API_KEY', $script:DummyKey, 'Process')
            [Environment]::SetEnvironmentVariable('ElevenLabs__BaseUrl', $standIn.Url, 'Process')
            Write-Line "Set for this dry run only: a dummy key, ElevenLabs__BaseUrl $($standIn.Url), Drive__Enabled false, CLASSSCRIBE_SETTINGS_FOLDER."
        }
        $ownSettings = Join-Path (Split-Path -Parent $exePath) 'appsettings.json'
        if (Test-FileHoldsText -Path (Join-Path (Split-Path -Parent $exePath) 'ClassScribe.dll') -Text 'CLASSSCRIBE_SETTINGS_FOLDER') {
            $what = if (Test-Path -LiteralPath $userSettings) { 'a copy of your settings file' } else { 'empty: you have no settings file in %APPDATA%\ClassScribe' }
            Write-Line "Settings: this build reads them from $settingsFolder ($what)."
        }
        else {
            Write-Line "Settings: this build reads only its own $ownSettings; it does not read CLASSSCRIBE_SETTINGS_FOLDER, which came with 0.5.0."
        }
        foreach ($item in $settings) { Write-Line "  For this dry run only: $item" }

        # The options this build has.
        if ($Class -or $Camera) {
            $help = (Invoke-Native { & $exePath record --help }) -join "`n"
            if ($Class -and $help -notmatch '--class') { $problems.Add('This build has no saved classes (record --class came with 0.5.0), so -Class cannot be used.'); return 2 }
            if ($Camera -and $help -notmatch '--camera') { $problems.Add('This build cannot record a camera (record --camera came with 0.11.0), so -Camera cannot be used.'); return 2 }
        }

        # The test player, at Gulzar's word given above: it plays sound aloud.
        if ($Player) {
            $playerFolder = Join-Path $folder 'player'
            $project = ConvertTo-Argument (Join-Path $PSScriptRoot 'TestPlayer\ClassScribeTestPlayer.csproj')
            $built = Start-Process -FilePath 'dotnet' -ArgumentList "build $project -c Release -o $(ConvertTo-Argument $playerFolder) --disable-build-servers -nologo" `
                -NoNewWindow -Wait -PassThru -RedirectStandardOutput (Join-Path $folder 'player.build.log') -RedirectStandardError (Join-Path $folder 'player.build.errors.txt')
            if ($built.ExitCode -ne 0) { $problems.Add("The test player did not build; see $(Join-Path $folder 'player.build.log')."); return 1 }
            $player = Start-Process -FilePath (Join-Path $playerFolder 'ClassScribeTestPlayer.exe') -ArgumentList "$(ConvertTo-Argument $Player) 20" -WindowStyle Minimized -PassThru
            Write-Line "The test player (process $($player.Id)) plays the clip after 20 seconds of silence."
        }

        # 6. The window, in a console of its own, outside any job.
        $output = $Roots.Recordings
        $recordArguments = @(Get-RecordArguments -Name $Name -App $appName -Mic $Mic -NoMic $NoMic.IsPresent -Follows $followed -Class $Class -Camera $Camera -Short $Short.IsPresent -Output $output)
        $line = ($recordArguments | ForEach-Object { ConvertTo-Argument $_ }) -join ' '
        $known = @($before['LocalAppData'] | ForEach-Object { $_.Path })
        Write-Line ''
        Write-Line "Starting: $exePath $line" Cyan
        Start-Process -FilePath $exePath -ArgumentList $line -WorkingDirectory $folder | Out-Null
        Write-Line ''
        $classApp = if ($appName -eq 'chrome') { 'Chrome' } else { $appName }
        $play = if ($Player) { 'Let the test player play the clip' } elseif ($Camera -or $Class) { 'Teach, or play something aloud in the room' } else { "Play a lecture in $classApp" }
        if ($Short) {
            Write-Line "$play. The recording stops by itself after 5 minutes and is transcribed;"
            Write-Line 'when the transcript is saved, press Enter in the window to close it.'
        }
        else {
            Write-Line 'In the window:'
            Write-Line "  1. $play, and speak for part of it."
            Write-Line '  2. Press M once (a mark), then P twice (a pause, then recording again).'
            Write-Line '  3. Close the window once with its X: the recorder goes on recording.'
            Write-Line '  4. Here, press O: the side copy opens; press A in its menu to see the recording again.'
            Write-Line '  5. Stop with Q twice, wait for the transcript, then press Enter in the window to close it.'
        }
        if (-not $NoMic -and -not $Camera -and -not $Class -and $followed -ne 'none') {
            Write-Line "Your microphone is written only while $followed listens on it: to be heard, have $followed listen (a call, or a microphone test page), or start the dry run with -Follows none." DarkYellow
        }
        Write-Line 'This window goes on by itself once the run is over and no ClassScribe of the dry run is left.'
        $run = Wait-DryRun -RunsFolder $runsFolder -ExePath $exePath -Known $known -WorkingFolder $folder

        # 7. The checks.
        Write-Line ''
        Write-Line 'Checking...' Cyan
        if (-not $run) {
            $problems.Add('No run file of the dry run was found: the recording never started.')
        }
        else {
            Write-Line "Run $($run.runId): $($run.state), home $($run.home), $(if ($run.outcome) { $run.outcome.kind } else { 'no outcome' })"
            foreach ($problem in @(Get-RunProblems -Run $run -Transcribe $true)) { $problems.Add($problem) }
            if ($run.folder -and $run.baseName) {
                $recordingFolder = [string]$run.folder
                $baseName = [string]$run.baseName
                Write-Line "The recording's files in $($recordingFolder):"
                Get-ChildItem -LiteralPath $recordingFolder -File -ErrorAction SilentlyContinue |
                    Where-Object { $_.Name.StartsWith($baseName) } |
                    ForEach-Object { Write-Line "  $($_.Name) ($($_.Length) bytes)" }
                foreach ($ending in $expected) {
                    if (-not (Test-Path -LiteralPath (Join-Path $recordingFolder ($baseName + $ending)))) { $problems.Add("There is no $ending beside the recording.") }
                }
                $warnings = @(Get-LogWarnings -Path (Join-Path $recordingFolder "$baseName.log"))
                if ($warnings.Count -gt 0) {
                    Write-Line "The recording's log has $($warnings.Count) warning or error line(s); read them:" Yellow
                    foreach ($warning in $warnings) { Write-Line "  $warning" Yellow }
                    $notes.Add("$($warnings.Count) warning or error line(s) in the recording's log")
                }
                foreach ($errorLine in @($warnings | Where-Object { $_ -match '^\S+\s+ERROR\s' })) { $problems.Add("The recording's log has an error: $errorLine") }
            }
        }
        if ($standIn) {
            $summary = Read-StandInLog -Path $standIn.Log
            $terms = if ($summary.Keyterms.Count) { $summary.Keyterms -join ', ' } else { 'none' }
            Write-Line "The stand-in answered $($summary.Transcripts) transcript(s) (keyterms: $terms) and $($summary.Accounts) read(s) of the account."
            if ($summary.Transcripts -ne 1) { $problems.Add("The stand-in answered $($summary.Transcripts) transcripts; one was expected.") }
            if ($summary.Transcripts -gt 0 -and @($summary.Keyterms | Where-Object { $_ -gt 0 }).Count -eq 0) { $problems.Add('No keyterms went up with the transcript.') }
            foreach ($stray in $summary.Unexpected) { $problems.Add("The stand-in had a request it does not know: $stray") }
            foreach ($refusal in $summary.Refused) { $problems.Add("The stand-in refused or failed a request: $refusal") }
        }

        if ($problems.Count -eq 0) { return 0 }
        return 1
    }
    catch {
        # Not rethrown: an error the dry run did not foresee fails it, with its message, and the finish below still stops
        # the stand-in, waits for the copy, removes it and compares the folders.
        $problems.Add("The dry run stopped on an error: $($_.Exception.Message) (line $($_.InvocationInfo.ScriptLineNumber))")
        $refused = $false
        return 1
    }
    finally {
        # 8. Stop what the dry run started, wait for the copy to end, remove it, and compare the folders.
        $ErrorActionPreference = 'Continue'
        Stop-StandIn -StandIn $standIn
        if ($player -and -not $player.HasExited) { Stop-Process -Id $player.Id -ErrorAction SilentlyContinue }
        foreach ($variable in $saved.Keys) { [Environment]::SetEnvironmentVariable($variable, $saved[$variable], 'Process') }
        if ($refused) {
            Write-Line 'Nothing was done.'
        }
        else {
            if ($exePath -and @(Get-CopyProcesses -ExePath $exePath).Count -gt 0) {
                Write-Line "ClassScribe from $exePath is still running: close its windows (Q twice, then Enter). Waiting..." Yellow
                while (@(Get-CopyProcesses -ExePath $exePath).Count -gt 0) { Start-Sleep -Seconds 2 }
            }
            if (-not $Exe -and -not $Keep -and $side -and (Test-Path -LiteralPath $side)) {
                Remove-Item -LiteralPath $side -Recurse -Force -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $side) { $notes.Add("The side copy could not be removed: $side") } else { Write-Line "Removed the side copy $side." }
            }
            if ($areas -and $before) {
                $runId = if ($run) { [string]$run.runId } else { '' }
                Write-Line ''
                Write-Line 'What changed in the data folders:' Cyan
                foreach ($area in $areas.Keys) {
                    $after = @(Get-FolderListing -Root $areas[$area])
                    Save-Listing -Listing $after -Path (Join-Path $folder "after-$area.txt")
                    $changes = @(Compare-FolderListing -Before $before[$area] -After $after)
                    $unexpected = @(Get-UnexpectedChanges -Changes $changes -Area $area -RunId $runId)
                    Write-Line "  $($areas[$area]): $(if ($changes.Count) { "$($changes.Count) change(s)" } else { 'nothing' })"
                    foreach ($change in $changes) {
                        $odd = $unexpected -contains $change
                        $color = if ($odd) { [ConsoleColor]::Yellow } else { [ConsoleColor]::Gray }
                        Write-Line "    $($change.Change) $($change.Path)$(if ($odd) { '   (not expected)' })" $color
                        if ($odd) { $problems.Add("Not expected in $($areas[$area]): $($change.Change) $($change.Path)") }
                    }
                }
            }

            # 9. The record of the dry run, which the finish of a release reads.
            $verdict = if ($problems.Count -eq 0) { 'PASS' } else { 'FAIL' }
            Write-Line ''
            if ($problems.Count -eq 0) {
                Write-Line 'The dry run passed.' Green
            }
            else {
                Write-Line "The dry run failed ($($problems.Count) problem(s)):" Red
                foreach ($problem in $problems) { Write-Line "  - $problem" Red }
            }
            foreach ($note in $notes) { Write-Line "  Note: $note" DarkYellow }
            $options = if ($script:BoundNames.Count) { ($script:BoundNames | ForEach-Object { "-$_" }) -join ' ' } else { 'none' }
            $record = '{0}  {1}  branch {2}  commit {3}{4}  build {5}  options {6}  folder {7}  {8}' -f `
                (Get-Date).ToString('yyyy-MM-dd HH:mm:ss', $script:Invariant), $verdict, $(if ($branch) { $branch } else { '-' }),
                $(if ($commit) { $commit } else { '-' }), $(if ($uncommitted -gt 0) { ' (with uncommitted changes)' } else { '' }),
                $(if ($build) { $build } else { '-' }), $options, $folder, $(if ($problems.Count) { $problems[0] } else { 'all checks passed' })
            Add-Content -LiteralPath (Join-Path $dryRuns 'runs.log') -Value $record -Encoding UTF8
            @($problems) + @($notes | ForEach-Object { "Note: $_" }) | Set-Content -LiteralPath (Join-Path $folder 'result.txt') -Encoding UTF8
            Write-Line "Recorded in $(Join-Path $dryRuns 'runs.log')."
        }
        try { Stop-Transcript | Out-Null } catch { Write-Verbose 'No record of the console to stop.' }
        # A refused dry run leaves nothing behind, not even its own folder.
        if ($refused) { Remove-Item -LiteralPath $folder -Recurse -Force -ErrorAction SilentlyContinue }
    }
}

if ($MyInvocation.InvocationName -ne '.') { exit (Invoke-DryRun) }
