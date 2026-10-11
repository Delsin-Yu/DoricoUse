<#
    tools/smoke.ps1 - a by-hand smoke test of the LIVE dorico-use surface.

    WHAT THIS IS
      Nothing in CI can reach Dorico, so `session`, `host`, `project`, `options`, `library`, `playback`,
      `properties`, `command`, `note`, `export`, `ui` and the live half of `session read` have no regression cover
      at all. This script is that cover, run by hand: it runs each of those paths once, parses the one JSON object
      the CLI prints, asserts something real about it, and prints ONE line per check - `PASS  <name>` or
      `FAIL  <name>` plus the JSON it saw - so the output can be pasted into a release report.
      It exits non-zero when any check failed, and also when a check could not be run at all (Dorico closed, or
      no document open), so a green run can never be faked.

    HOW TO RUN IT
      powershell -NoProfile -File tools/smoke.ps1
      powershell -NoProfile -File tools/smoke.ps1 -Exe src/DoricoUse.Cli/bin/Debug/net10.0-windows/dorico-use.exe
      pwsh      -NoProfile -File tools/smoke.ps1 -AllowWrite         # the write checks too (scratch project!)
      powershell -NoProfile -File tools/smoke.ps1 -AllowUnsafeScriptRunApi   # only for a build made with
                                                                            # -p:ExposeUnsafeScriptRunApi=true

      Parameters:
        -Exe <path>                 the CLI to test (see the resolution order below).
        -AllowWrite                 run the two checks that write: `session read --rebind` and a real
                                    `note insert`. Without it they print SKIP and why.
        -AllowUnsafeScriptRunApi    this binary was built with -p:ExposeUnsafeScriptRunApi=true, so the
                                    Script.* check expects the command to be accepted instead of refused.
                                    A released binary must never need this switch.
        -JsonWidth <n>              how much of each payload to print (default 500 characters; 0 = all of it).
                                    The assertion message names the field that was wrong, so a trimmed payload
                                    is still readable.

      Where the CLI comes from, in order:
        1. -Exe, when given (a path, or a name looked up on PATH);
        2. src/DoricoUse.Cli/bin/Release/net10.0-windows/win-x64/publish/dorico-use.exe (the published binary);
        3. src/DoricoUse.Cli/bin/Debug/net10.0-windows/dorico-use.exe             (a plain build);
        4. `dorico-use` on PATH.

    WHAT IT TOUCHES
      A running Dorico, through the host, exactly as a user would: it starts the host if none is running, and
      `host restart` stops and starts it (that is what the host checks are for). Dorico itself is asked to run the
      CLI's own commands and nothing else.
      Files, outside any document:
        - %TEMP%\dorico-use\live-read.tsv and live-read.lua   the live read's own dump, written by Dorico;
        - %TEMP%\dorico-use-smoke\smoke-<timestamp>.csv       the CSV `export text` writes;
        - a timestamped copy under the system temporary directory, only with -AllowWrite, from --rebind.
      Nothing is deleted, and the script never sends File.Save, never closes a document, and never sends File.New
      or anything else that would replace the open project.

    SAFETY (read this before using -AllowWrite)
      - The active document's score ID is printed first, from `session status`, and again before any write. The
        API exposes no document title, so the title line says so rather than guessing one.
      - `session read --rebind` SAVES A COPY with File.SaveAs (a timestamped file under the system temporary
        directory) AND REBINDS THE OPEN DOCUMENT TO THAT COPY: autosave, backups and a later File.Save follow the
        copy, not the file you had open. This script prints the copy's path. Re-open the original to undo it.
        It runs only with -AllowWrite.
      - A real `note insert` writes a C5 at the start of the first flow of the ACTIVE document. It runs only with
        -AllowWrite, and Dorico's Ctrl+Z undoes it. On a build older than the A4 change, its `--read` half also
        saves a copy and rebinds the document - the payload's `note.read.source` says which route ran.
        Use a scratch project, as the release plan says.
      - Every check that writes passes --expect-score, so a stolen window focus is a refusal, not damage to
        somebody else's project.
      - Nothing is ever sent to the scripting layer: the `command send --name Script.RunScript` check runs with
        --dry-run, which is refused before anything is sent when the guard is present, and sends nothing when it
        is not.

    EXIT CODES
      0  every check passed (SKIP is not a failure - it means -AllowWrite was not given)
         the real dialogs are a printed checklist at the end of the run, not a check: a script cannot raise one
      1  at least one check FAILED
      2  no check failed, but at least one was not run (Dorico unreachable, or no document open)
#>

[CmdletBinding()]
param(
    [string] $Exe,
    [switch] $AllowWrite,
    [switch] $AllowUnsafeScriptRunApi,
    [int]    $JsonWidth = 500
)

$ErrorActionPreference = 'Stop'

# Windows PowerShell 5.1 decodes a native command's stdout with the console's output encoding, which on a Chinese
# or Western install is not UTF-8. The CLI prints UTF-8 (a score title is not ASCII), and reading those bytes with
# the wrong code page can turn a multi-byte character into a stray quote and break the JSON. The encoding of this
# process only is changed for the run; it is not persisted anywhere. (A console-less host can refuse this, which
# costs nothing: the payloads that are pure ASCII still parse.)
try {
    [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false)
}
catch {
    Write-Output ('note: the console encoding could not be set to UTF-8 ({0}); a payload with non-ASCII text may not parse.' -f $_.Exception.Message)
}

# --------------------------------------------------------------------------------------------------------------
# State. Everything the checks share lives here, because PowerShell script blocks get their own scope.
# --------------------------------------------------------------------------------------------------------------

$script:RepoRoot            = Split-Path -Parent $PSScriptRoot
$script:ExePath             = ''
$script:Fixture             = Join-Path $script:RepoRoot 'tests\corpus\fixture.dorico'
$script:ExportDir           = Join-Path ([System.IO.Path]::GetTempPath()) 'dorico-use-smoke'
$script:ExportCsv           = ''

$script:Passed              = 0
$script:Failed              = 0
$script:Skipped             = 0
$script:NotRun              = 0

$script:ScoreId             = $null
$script:HasDocument         = $false
$script:Reachable           = $false
$script:NotRunReason        = ''
$script:NoDocumentReason    = 'open a scratch project and run this again; this script never opens one'

$script:CollectionName      = 'kClefCollection'
$script:HostPidBeforeRestart = $null
$script:HostPidAfterRestart  = $null
$script:ReportedUnsafe      = $null
$script:LastJson            = $null
$script:Dialogue            = $null

# --------------------------------------------------------------------------------------------------------------
# Printing. Every line goes to stdout, so the whole run can be redirected into a file and pasted into a report.
# --------------------------------------------------------------------------------------------------------------

function Write-Line {
    param([string] $Text = '')

    # Write-Host rather than Write-Output, and that is a fix rather than a style choice: a check's assert block is
    # run with `& $Assert`, so anything the block writes to the output stream is captured as part of its return
    # value. A NOTE printed from inside an assert therefore turned a passing check into "the verdict is not a bool"
    # - measured, twice, on the panel checks that report what is up while they assert. Write-Host goes to the
    # console the script's output is read from, and the assert's return value stays the one value it means.
    Write-Host $Text
}

function Format-Value {
    param($Value)
    if ($null -eq $Value) { return 'absent' }
    if ($Value -is [bool]) { if ($Value) { return 'true' } else { return 'false' } }
    return "$Value"
}

function Format-Snippet {
    param([string] $Text, [int] $Length = 240)
    if ([string]::IsNullOrEmpty($Text)) { return '' }
    $flat = ($Text -replace '\s+', ' ').Trim()
    if ($flat.Length -le $Length) { return $flat }
    return $flat.Substring(0, $Length) + '...'
}

# The JSON as the CLI printed it: one compact, trimmed line. The whole payload is printed with -JsonWidth 0.
function Format-Json {
    param([string] $Text)
    if ([string]::IsNullOrWhiteSpace($Text)) { return '<no stdout>' }
    $flat = ($Text -replace '\s+', ' ').Trim()
    if ($JsonWidth -gt 0 -and $flat.Length -gt $JsonWidth) {
        return $flat.Substring(0, $JsonWidth) + ('[... +{0} chars]' -f ($flat.Length - $JsonWidth))
    }
    return $flat
}

# --------------------------------------------------------------------------------------------------------------
# Running the CLI. One invocation per check, stdout parsed as JSON, exit code kept.
# --------------------------------------------------------------------------------------------------------------

function Invoke-SmokeCli {
    param([string[]] $Arguments)

    $text = ''
    $code = 0

    # A native command's stderr becomes an error record, and the script's $ErrorActionPreference = 'Stop' would
    # turn that into a thrown exception even though stdout carries the JSON that matters. Only this one call is
    # relaxed, and only while it runs.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'

    try {
        $text = (& $script:ExePath @Arguments 2>$null) -join "`n"
        $code = $LASTEXITCODE
    }
    catch {
        $text = 'the CLI could not be started: ' + $_.Exception.Message
        $code = -1
    }
    finally {
        $ErrorActionPreference = $previous
    }

    $text = $text.Trim()
    $json = $null

    if ($text.Length -gt 0) {
        try { $json = $text | ConvertFrom-Json } catch { $json = $null }
    }

    return @{ Text = $text; Json = $json; ExitCode = $code }
}

# PowerShell unwraps a one-element JSON array into a scalar, so counting has to go through @().
function Get-ItemCount {
    param($Value)
    if ($null -eq $Value) { return 0 }
    return @($Value).Count
}

# --------------------------------------------------------------------------------------------------------------
# Assertions. Each returns $true, or a sentence saying what was wrong; the caller prints it on the FAIL line.
# --------------------------------------------------------------------------------------------------------------

function Assert-Ok {
    param($Json, $ExitCode)

    if ($null -eq $Json) {
        return ('stdout was not one JSON object (exit code {0})' -f $ExitCode)
    }

    if ($Json.ok -ne $true) {
        $code = 'none'
        $message = 'no message'

        if ($null -ne $Json.error) {
            if ($null -ne $Json.error.code) { $code = $Json.error.code }
            if ($null -ne $Json.error.message) { $message = $Json.error.message }
        }

        return ('exit code {0}, error.code={1}: {2}' -f $ExitCode, $code, (Format-Snippet "$message"))
    }

    return $true
}

function Assert-ExitCode {
    param($Json, $ExitCode, [int] $Expected)
    if ($ExitCode -ne $Expected) { return ('exit code was {0}, expected {1}' -f $ExitCode, $Expected) }
    return $true
}

function Assert-Flag {
    param($Value, [string] $Name, [bool] $Expected)
    if ($Value -isnot [bool]) {
        return ('{0} is {1}, expected the boolean {2}' -f $Name, (Format-Value $Value), (Format-Value $Expected))
    }
    if ($Value -ne $Expected) {
        return ('{0} is {1}, expected {2}' -f $Name, $Value, (Format-Value $Expected))
    }
    return $true
}

function Assert-NotNull {
    param($Value, [string] $Name)
    if ($null -eq $Value) { return ('{0} is absent' -f $Name) }
    return $true
}

# --------------------------------------------------------------------------------------------------------------
# The check runner. One line out per check, always the same shape:
#     PASS  <name>  <json>
#     FAIL  <name>  => <what was wrong>  <json>
#     SKIP  <name>  - <why a write check did not run>           (needs -AllowWrite)
#     not run  <name>  - <why nothing could be checked>         (no Dorico, or no document)
# --------------------------------------------------------------------------------------------------------------

function Invoke-Check {
    param(
        [Parameter(Mandatory)][string]   $Name,
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][scriptblock] $Assert,
        [switch] $NeedsScore,
        [switch] $Offline
    )

    # version and file read need neither Dorico nor the host, so they run and report normally even when nothing
    # live can be reached - that is what makes the run's JSON evidence useful on a closed machine too.
    if (-not $Offline) {
        if (-not $script:Reachable) {
            Write-Line ('not run  {0}  - {1}' -f $Name, $script:NotRunReason)
            $script:NotRun++
            return
        }

        if ($NeedsScore -and -not $script:HasDocument) {
            Write-Line ('not run  {0}  - no document is open in Dorico: {1}' -f $Name, $script:NoDocumentReason)
            $script:NotRunReason = 'no document is open in Dorico'
            $script:NotRun++
            return
        }
    }

    $result = Invoke-SmokeCli -Arguments $Arguments
    $script:LastJson = $result.Json

    $verdict = $null

    try {
        $verdict = & $Assert $result.Json $result.ExitCode $result.Text
    }
    catch {
        $verdict = 'the assertion threw: ' + $_.Exception.Message
    }

    if (($verdict -is [bool]) -and $verdict) {
        Write-Line ('PASS  {0}  {1}' -f $Name, (Format-Json $result.Text))
        $script:Passed++
        return
    }

    Write-Line ('FAIL  {0}  => {1}  {2}' -f $Name, (Format-Snippet "$verdict"), (Format-Json $result.Text))
    $script:Failed++
}

# A check that puts a document at risk: it runs only with -AllowWrite, and says why when it does not.
function Invoke-WriteCheck {
    param(
        [Parameter(Mandatory)][string]   $Name,
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][scriptblock] $Assert,
        [Parameter(Mandatory)][string]   $Reason,
        [switch] $NeedsScore
    )

    if (-not $AllowWrite) {
        Write-Line ('SKIP  {0}  - {1} Add -AllowWrite to run it.' -f $Name, $Reason)
        $script:Skipped++
        return
    }

    # The active document is printed again here, immediately before a write, because the whole risk of this script
    # is writing into a document somebody else has open: session status named it at the start, and every write is
    # also passed --expect-score so that a stolen focus is a refusal rather than damage.
    if ($script:Reachable -and $script:HasDocument) {
        Write-Line ('WRITE {0}  - about to write into the active document, scoreId={1} (as session status reported it)' -f $Name, (Format-Value $script:ScoreId))
    }

    Invoke-Check -Name $Name -Arguments $Arguments -Assert $Assert -NeedsScore:$NeedsScore
}

# --------------------------------------------------------------------------------------------------------------
# Finding the CLI.
# --------------------------------------------------------------------------------------------------------------

function Resolve-SmokeExe {
    param([string] $Requested)

    if (-not [string]::IsNullOrWhiteSpace($Requested)) {
        if (Test-Path -LiteralPath $Requested -PathType Leaf) {
            return (Resolve-Path -LiteralPath $Requested).Path
        }

        $found = Get-Command $Requested -ErrorAction SilentlyContinue

        if ($null -ne $found) { return $found.Source }

        throw ('-Exe ''{0}'' is neither a file nor a command on PATH.' -f $Requested)
    }

    $candidates = @(
        (Join-Path $script:RepoRoot 'src\DoricoUse.Cli\bin\Release\net10.0-windows\win-x64\publish\dorico-use.exe'),
        (Join-Path $script:RepoRoot 'src\DoricoUse.Cli\bin\Debug\net10.0-windows\dorico-use.exe')
    )

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    }

    $onPath = Get-Command 'dorico-use' -ErrorAction SilentlyContinue

    if ($null -ne $onPath) { return $onPath.Source }

    throw ('No dorico-use.exe under {0}: build the CLI, pass -Exe, or put dorico-use on PATH.' -f $script:RepoRoot)
}

# --------------------------------------------------------------------------------------------------------------
# The active document, before anything else. `getstatus` exposes the score ID; it carries no document title, so
# this prints one only if a future Dorico reports a title-ish field, and says so when it does not.
# --------------------------------------------------------------------------------------------------------------

function Get-DocumentTitle {
    param($Status)

    if ($null -eq $Status) { return $null }

    foreach ($property in $Status.PSObject.Properties) {
        if ($property.Name -match '(?i)title|document') {
            $value = $property.Value
            if (($value -is [string]) -and ($value.Length -gt 0)) { return $value }
        }
    }

    return $null
}

# --------------------------------------------------------------------------------------------------------------
# The run.
# --------------------------------------------------------------------------------------------------------------

Write-Line 'dorico-use smoke test - the live surface, run by hand against a running Dorico.'
Write-Line ('  PowerShell {0}' -f $PSVersionTable.PSVersion.ToString())

try {
    $script:ExePath = Resolve-SmokeExe -Requested $Exe
}
catch {
    Write-Line ('FAIL  the CLI to test  => {0}' -f $_.Exception.Message)
    exit 1
}

Write-Line ('  CLI        {0}' -f $script:ExePath)
Write-Line ('  repository {0}' -f $script:RepoRoot)

$mode = 'read-only; the two checks that write will print SKIP'
if ($AllowWrite) { $mode = 'writes allowed (-AllowWrite): a scratch project is expected' }
Write-Line ('  mode       {0}' -f $mode)

if ($AllowUnsafeScriptRunApi) {
    Write-Line '  NOTE       -AllowUnsafeScriptRunApi was given: this binary is expected to be an unsafe build'
    Write-Line '             (ExposeUnsafeScriptRunApi=true) and the Script.* check expects the command to be accepted.'
    Write-Line '             A released binary must never need this switch.'
}

Write-Line ''
Write-Line 'SAFETY  this script never sends File.Save, never closes a document and never sends File.New or anything'
Write-Line '        else that would replace the open project. It acts on whichever document Dorico has active.'
Write-Line 'SAFETY  session read --rebind SAVES A COPY of the open document with File.SaveAs (a timestamped file under'
Write-Line '        the system temporary directory) and REBINDS THE OPEN DOCUMENT TO THAT COPY, so autosave, backups'
Write-Line '        and a later File.Save follow the copy rather than the file you had open. It runs only with'
Write-Line '        -AllowWrite, and the run prints the copy''s path on its own line right after that check.'
Write-Line '        Re-open the original file in Dorico to undo the rebinding.'
Write-Line 'SAFETY  a real note insert (only with -AllowWrite) enters C5 at the start of the first flow of the active'
Write-Line '        document; Ctrl+Z in Dorico undoes it. Use a scratch project.'
Write-Line 'NOTE    the live read hands Dorico a small read-only Lua script whose dump lands in'
Write-Line '        %TEMP%\dorico-use\live-read.tsv. Nothing is saved and the document is not rebound.'
Write-Line ''

# -- the active document, first, so a run against a document somebody cares about is visible before it is touched
$preflight = Invoke-SmokeCli -Arguments @('session', 'status', '--raw')

# A build in another window, or another agent's `host stop`, closes the host pipe between two of our commands and
# the CLI reports HostGone or HostTimeout. One retry turns that into a real answer - or into a real NotReachable -
# instead of a run full of "not run" lines that says nothing about Dorico at all.
if (($null -eq $preflight.Json) -or ($preflight.Json.ok -ne $true)) {
    $firstCode = 'none'

    if (($null -ne $preflight.Json) -and ($null -ne $preflight.Json.error) -and ($null -ne $preflight.Json.error.code)) {
        $firstCode = $preflight.Json.error.code
    }

    Write-Line ('RETRY  session status said {0}; asking once more in 2 s even though nothing was changed here.' -f $firstCode)
    Start-Sleep -Seconds 2
    $preflight = Invoke-SmokeCli -Arguments @('session', 'status', '--raw')
}

if (($null -ne $preflight.Json) -and ($preflight.Json.ok -eq $true)) {
    $script:Reachable = $true
    $status = $preflight.Json.status

    if ($null -ne $status) {
        $script:ScoreId = $status.scoreId
        if ($status.hasScore -eq $true) { $script:HasDocument = $true }
    }

    $title = Get-DocumentTitle $status

    Write-Line ('ACTIVE DOCUMENT  scoreId={0}  hasScore={1}  title={2}' -f `
        (Format-Value $script:ScoreId), (Format-Value $status.hasScore), $(if ($null -eq $title) { '(getstatus carries no document title, and the API exposes none - read the Dorico window)' } else { $title }))

    if ($null -ne $script:ScoreId) {
        Write-Line ('                 every check that writes is passed --expect-score {0}, so a stolen window focus is a refusal, not damage' -f $script:ScoreId)
    }

    if (-not $script:HasDocument) {
        Write-Line 'WARNING  Dorico is reachable but has no document open (hasScore=false): every check that needs a score'
        Write-Line '         is reported as "not run" and this script exits non-zero. It never opens a document for you.'
    }
}
else {
    $script:Reachable = $false

    $code = 'none'

    if (($null -ne $preflight.Json) -and ($null -ne $preflight.Json.error) -and ($null -ne $preflight.Json.error.code)) {
        $code = $preflight.Json.error.code
    }

    if ($code -eq 'NotReachable') {
        $script:NotRunReason = 'Dorico is not reachable'
    }
    elseif ($code -eq 'none') {
        $script:NotRunReason = 'the session could not be read (no JSON came back)'
    }
    else {
        $script:NotRunReason = ('the session could not be read ({0})' -f $code)
    }

    Write-Line ('ACTIVE DOCUMENT  unknown - {0}' -f $script:NotRunReason)
    Write-Line ('                 session status said: {0}' -f (Format-Json $preflight.Text))
    Write-Line 'WARNING  without Dorico, every check that needs it is reported as "not run" and this script exits non-zero.'
    Write-Line '         version and file read need neither Dorico nor the host, so they still run and report normally.'
}

Write-Line ''

# --------------------------------------------------------------------------------------------------------------
# version - the one command that always answers, and the only place a released binary states its build flags.
# --------------------------------------------------------------------------------------------------------------

Invoke-Check -Name 'version' -Arguments @('version') -Offline -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    $build = $j.version.build
    if ($null -eq $build) {
        return 'version.build is absent: this binary does not report its build flags (expected exposeUnsafeScriptRunApi=false and neverPersistSession=false). Rebuild the CLI with the B1 change.'
    }

    if ($build.exposeUnsafeScriptRunApi -ne $false) {
        return ('version.build.exposeUnsafeScriptRunApi is {0}, expected false' -f (Format-Value $build.exposeUnsafeScriptRunApi))
    }

    if ($build.neverPersistSession -ne $false) {
        return ('version.build.neverPersistSession is {0}, expected false' -f (Format-Value $build.neverPersistSession))
    }

    if ($null -eq $j.compatibility.testedAgainst) { return 'compatibility.testedAgainst is absent' }
    if ($null -eq $j.version.nativeAot) { return 'version.nativeAot is absent' }

    $script:ReportedUnsafe = $build.exposeUnsafeScriptRunApi
    return $true
}

# --------------------------------------------------------------------------------------------------------------
# session / host
# --------------------------------------------------------------------------------------------------------------

Invoke-Check -Name 'session status' -Arguments @('session', 'status', '--raw') -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($null -eq $j.status) { return 'status is absent' }
    if ($j.status.hasScore -isnot [bool]) { return ('status.hasScore is {0}, expected a boolean' -f (Format-Value $j.status.hasScore)) }
    if ($null -eq $j.status.mode) { return 'status.mode is absent' }
    if ($null -eq $j.status.noteInput) { return 'status.noteInput is absent' }
    if ($null -eq $j.raw) { return 'raw is absent although --raw was given' }
    return $true
}

Invoke-Check -Name 'session info' -Arguments @('session', 'info') -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($null -eq $j.app) { return 'app is absent' }
    if ("$($j.app.number)" -notmatch '^\d+\.\d+') { return ('app.number is {0}, expected a Dorico version such as 6.2.30' -f (Format-Value $j.app.number)) }
    if ($null -eq $j.session) { return 'session is absent' }
    if ([string]::IsNullOrWhiteSpace("$($j.session.clientName)")) { return 'session.clientName is absent' }
    if ($null -eq $j.storePath) { return 'storePath is absent' }
    return $true
}

Invoke-Check -Name 'session events' -Arguments @('session', 'events', '--limit', '20') -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($null -eq $j.total) { return 'total is absent' }
    if ((Get-ItemCount $j.events) -gt 20) { return ('events carries {0} entries, but --limit was 20' -f (Get-ItemCount $j.events)) }
    if ($null -eq $j.counts) { return 'counts is absent' }
    $ok = Assert-Flag $j.connected 'connected' $true
    if ($ok -ne $true) { return $ok }
    return $true
}

Invoke-Check -Name 'host status' -Arguments @('host', 'status') -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($null -eq $j.host) { return 'host is absent' }
    if ($j.host.running -isnot [bool]) { return ('host.running is {0}, expected a boolean' -f (Format-Value $j.host.running)) }

    if ($j.host.running -eq $true) {
        if ($null -eq $j.host.pid) { return 'host.running is true but host.pid is absent' }
        $script:HostPidBeforeRestart = $j.host.pid
        return $true
    }

    return $true
}

Invoke-Check -Name 'host restart' -Arguments @('host', 'restart') -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($null -eq $j.stopped) { return 'stopped is absent' }
    $ok = Assert-Flag $j.stopped.running 'stopped.running' $false
    if ($ok -ne $true) { return $ok }
    $ok = Assert-Flag $j.host.alreadyRunning 'host.alreadyRunning' $false
    if ($ok -ne $true) { return $ok }
    $ok = Assert-NotNull $j.host.pid 'host.pid'
    if ($ok -ne $true) { return $ok }

    if (($null -ne $script:HostPidBeforeRestart) -and ($j.host.pid -eq $script:HostPidBeforeRestart)) {
        return ('restart reported the same pid {0} as the host it should have replaced' -f $j.host.pid)
    }

    $script:HostPidAfterRestart = $j.host.pid
    return $true
}

Invoke-Check -Name 'host status (after restart)' -Arguments @('host', 'status') -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    $ok = Assert-Flag $j.host.running 'host.running' $true
    if ($ok -ne $true) { return $ok }

    if (($null -ne $script:HostPidAfterRestart) -and ($j.host.pid -ne $script:HostPidAfterRestart)) {
        return ('host.pid is {0}, but host restart reported {1}' -f (Format-Value $j.host.pid), $script:HostPidAfterRestart)
    }

    return $true
}

# --------------------------------------------------------------------------------------------------------------
# project / options / library / playback / properties - the project's own data. All need an open document.
# --------------------------------------------------------------------------------------------------------------

Invoke-Check -Name 'project flows' -Arguments @('project', 'flows') -NeedsScore -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($j.count -lt 1) { return ('count is {0}: the open document has no flow' -f (Format-Value $j.count)) }
    if ((Get-ItemCount $j.flows) -ne $j.count) { return ('count is {0} but flows carries {1}' -f $j.count, (Get-ItemCount $j.flows)) }
    if (($null -ne $script:ScoreId) -and ($j.scoreId -ne $script:ScoreId)) { return ('scoreId is {0}, but session status reported {1}' -f (Format-Value $j.scoreId), $script:ScoreId) }
    if ($null -eq $j.flows[0].name) { return 'the first flow has no name' }
    return $true
}

Invoke-Check -Name 'project layouts' -Arguments @('project', 'layouts') -NeedsScore -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($j.total -lt 1) { return ('total is {0}: the open document has no layout' -f (Format-Value $j.total)) }
    if ((Get-ItemCount $j.layouts) -ne $j.count) { return ('count is {0} but layouts carries {1}' -f $j.count, (Get-ItemCount $j.layouts)) }
    if ($j.total -lt $j.count) { return ('total {0} is smaller than count {1}' -f $j.total, $j.count) }

    foreach ($layout in @($j.layouts)) {
        if ($null -eq $layout.id) { return 'a layout has no id' }
        if ($null -eq $layout.kind) { return ('layout {0} has no kind' -f $layout.id) }
    }

    return $true
}

Invoke-Check -Name 'project players' -Arguments @('project', 'players') -NeedsScore -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    # No protocol request returns players, so this is a live read and it has to say so: a payload that claimed a
    # fetch would be describing something the API does not have.
    if ("$($j.source)" -ne 'live') { return ('source is {0}, expected live' -f (Format-Value $j.source)) }
    if ($j.count -lt 1) { return ('count is {0}: the open document reports no player' -f (Format-Value $j.count)) }
    if ((Get-ItemCount $j.players) -ne $j.count) { return ('count is {0} but players carries {1}' -f $j.count, (Get-ItemCount $j.players)) }

    foreach ($player in @($j.players)) {
        if ($null -eq $player.index) { return 'a player has no index' }
        if ([string]::IsNullOrWhiteSpace("$($player.described)")) { return ('player {0} has no description' -f $player.index) }
    }

    # The members the scripting layer refuses have to be reported as refusals rather than left looking null, and the
    # caveat that names the missing request has to be there.
    $caveats = (@($j.caveats) | ForEach-Object { "$_" }) -join ' '
    if ($caveats -notlike '*no Remote Control request that returns players*') {
        return 'the caveats do not say that no protocol request returns players'
    }

    if ($null -ne $j.script -and $null -eq $j.script.ack) { return 'script.ack is absent: the read must report what Dorico acknowledged' }
    return $true
}

Invoke-Check -Name 'options get' -Arguments @('options', 'get', '--type', 'engraving', '--limit', '5') -NeedsScore -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($j.optionsType -ne 'kEngraving') { return ('optionsType is {0}, expected kEngraving for --type engraving' -f (Format-Value $j.optionsType)) }
    if ($j.returned -lt 1) { return ('returned is {0}: no engraving option came back' -f (Format-Value $j.returned)) }
    if ((Get-ItemCount $j.options) -ne $j.returned) { return ('returned is {0} but options carries {1}' -f $j.returned, (Get-ItemCount $j.options)) }
    if ($j.count -lt $j.returned) { return ('count {0} is smaller than returned {1}' -f $j.count, $j.returned) }

    foreach ($option in @($j.options)) {
        if ([string]::IsNullOrWhiteSpace("$($option.path)")) { return 'an option came back without a path' }
    }

    return $true
}

Invoke-Check -Name 'library collections' -Arguments @('library', 'collections') -NeedsScore -Assert {
    param($j, $code, $text)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($j.count -lt 1) { return ('count is {0}: the library reports no collection' -f (Format-Value $j.count)) }
    if ((Get-ItemCount $j.collections) -ne $j.count) { return ('count is {0} but collections carries {1}' -f $j.count, (Get-ItemCount $j.collections)) }

    # `library entities` needs a collection name from this list. The CLI documents kClefCollection; prefer a name
    # of that shape that this project actually reports, and fall back to the documented one.
    $match = [regex]::Match($text, '"k[A-Za-z0-9]*Collection"')

    if ($match.Success) {
        $script:CollectionName = $match.Value.Trim('"')
    }

    return $true
}

Invoke-Check -Name ('library entities (' + $script:CollectionName + ')') -Arguments @('library', 'entities', '--collection', $script:CollectionName) -NeedsScore -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($j.count -lt 1) { return ('count is {0}: collection {1} reported no entity (run `library collections` and try a name from it)' -f (Format-Value $j.count), $script:CollectionName) }
    if ((Get-ItemCount $j.entities) -ne $j.count) { return ('count is {0} but entities carries {1}' -f $j.count, (Get-ItemCount $j.entities)) }

    foreach ($entity in @($j.entities)) {
        if ($null -eq $entity.id) { return 'an entity came back without an id' }
    }

    return $true
}

Invoke-Check -Name 'playback techniques' -Arguments @('playback', 'techniques') -NeedsScore -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($null -eq $j.expressionMapCount) { return 'expressionMapCount is absent' }
    if ((Get-ItemCount $j.expressionMaps) -ne $j.expressionMapCount) { return ('expressionMapCount is {0} but expressionMaps carries {1}' -f $j.expressionMapCount, (Get-ItemCount $j.expressionMaps)) }
    if ($null -eq $j.drumKitNoteMapCount) { return 'drumKitNoteMapCount is absent' }
    if ((Get-ItemCount $j.drumKitNoteMaps) -ne $j.drumKitNoteMapCount) { return ('drumKitNoteMapCount is {0} but drumKitNoteMaps carries {1}' -f $j.drumKitNoteMapCount, (Get-ItemCount $j.drumKitNoteMaps)) }
    if ($null -eq $j.scoreId) { return 'scoreId is absent: the answer is not about the open document' }

    return $true
}

Invoke-Check -Name 'properties get' -Arguments @('properties', 'get') -NeedsScore -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($null -eq $j.count) { return 'count is absent' }
    if ((Get-ItemCount $j.properties) -ne $j.count) { return ('count is {0} but properties carries {1}' -f $j.count, (Get-ItemCount $j.properties)) }
    if ($null -eq $j.scoreId) { return 'scoreId is absent: the answer is not about the open document' }
    return $true
}

# --------------------------------------------------------------------------------------------------------------
# the command catalogue and one command that is deliberately not sent
# --------------------------------------------------------------------------------------------------------------

Invoke-Check -Name 'command list' -Arguments @('command', 'list', '--family', 'NoteInput', '--filter', '*Pitch*', '--limit', '5') -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($null -eq $j.catalogue) { return 'catalogue is absent' }
    if ($j.catalogue.count -lt 500) { return ('catalogue.count is {0}: Dorico 6.2.30 Pro reports about 1090 commands' -f (Format-Value $j.catalogue.count)) }
    if ($j.matchCount -lt 1) { return 'matchCount is 0: --family NoteInput --filter *Pitch* matched nothing' }
    if ((Get-ItemCount $j.matches) -ne $j.matchCount) { return ('matchCount is {0} but matches carries {1}' -f $j.matchCount, (Get-ItemCount $j.matches)) }

    # Only what --limit 5 can support: CatalogueView.Select stops scanning at the limit in Dorico's own catalogue
    # order, so which five entries come back is not fixed and requiring a particular name here would be luck. The
    # exact name is asserted by the `command info --name NoteInput.Pitch` check that follows.
    foreach ($match in @($j.matches)) {
        if ("$($match.name)" -notlike 'NoteInput.*') { return ('{0} is not a NoteInput command, but --family NoteInput was given' -f (Format-Value $match.name)) }
    }

    return $true
}

Invoke-Check -Name 'command info' -Arguments @('command', 'info', '--name', 'NoteInput.Pitch') -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($null -eq $j.command) { return 'command is absent: the name did not resolve to one catalogue entry' }
    if ("$($j.command.name)" -ne 'NoteInput.Pitch') { return ('command.name is {0}, expected NoteInput.Pitch' -f (Format-Value $j.command.name)) }
    if ([string]::IsNullOrWhiteSpace("$($j.command.displayName)")) { return 'command.displayName is absent' }
    if ($null -eq $j.catalogue) { return 'catalogue is absent' }
    return $true
}

Invoke-Check -Name 'command send --dry-run' -Arguments @('command', 'send', '--name', 'NoteInput.Pitch', '--param', 'Pitch=C', '--param', 'OctaveValue=5', '--dry-run') -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($null -eq $j.plan) { return 'plan is absent: --dry-run must compose the command without sending it' }
    $ok = Assert-Flag $j.plan.dryRun 'plan.dryRun' $true
    if ($ok -ne $true) { return $ok }
    if ("$($j.plan.command)" -ne 'NoteInput.Pitch?Pitch=C,OctaveValue=5') { return ('plan.command is {0}, expected NoteInput.Pitch?Pitch=C,OctaveValue=5' -f (Format-Value $j.plan.command)) }
    if ("$($j.plan.parameters.OctaveValue)" -ne '5') { return ('plan.parameters.OctaveValue is {0}, expected 5' -f (Format-Value $j.plan.parameters.OctaveValue)) }
    return $true
}

# The scripting layer is undocumented and has crashed Dorico when it was probed (docs/scripting.md section 8), so
# this check is ALWAYS a --dry-run: the guard runs before the dry-run branch, which means the refusal it looks for
# is the same one a real send would get, and a build without the guard still sends nothing to Dorico.
Invoke-Check -Name 'command send --name Script.RunScript (must be refused)' -Arguments @('command', 'send', '--name', 'Script.RunScript', '--dry-run') -Assert {
    param($j, $code)

    if ($AllowUnsafeScriptRunApi) {
        $ok = Assert-Ok $j $code
        if ($ok -ne $true) { return $ok }
        if ("$($j.plan.name)" -ne 'Script.RunScript') { return ('plan.name is {0}, expected Script.RunScript' -f (Format-Value $j.plan.name)) }
        return $true
    }

    if ($null -eq $j) { return ('stdout was not one JSON object (exit code {0}), so the refusal could not be read' -f $code) }

    if ($j.ok -eq $true) {
        if ($script:ReportedUnsafe -eq $true) {
            return 'the command was accepted, and version.build.exposeUnsafeScriptRunApi is true. This is an unsafe build: re-run with -AllowUnsafeScriptRunApi if that is intended. Nothing was sent.'
        }

        return 'the command was accepted, but Script.* must be refused with ScriptingDisabled unless the build sets EXPOSE_UNSAFE_SCRIPT_RUN_API. Nothing was sent (--dry-run), so Dorico was not touched.'
    }

    if ($code -ne 1) { return ('exit code was {0}, expected 1 for a refusal' -f $code) }
    if ("$($j.error.code)" -ne 'ScriptingDisabled') { return ('error.code is {0}, expected ScriptingDisabled' -f (Format-Value $j.error.code)) }
    if ($null -eq $j.error.message) { return 'the refusal carries no message' }
    return $true
}

# --------------------------------------------------------------------------------------------------------------
# the modal-dialog surface. Read-only: nothing here presses a button, and the two checks that need a real dialog
# are printed by hand at the end, because a script cannot raise one.
# --------------------------------------------------------------------------------------------------------------

# The probe runs on every invocation, so a payload without the field means the funnel lost it - which is exactly
# what this asserts while Dorico is live.
Invoke-Check -Name 'session status carries the dialog field' -Arguments @('session', 'status') -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($j.PSObject.Properties.Name -notcontains 'dialog') {
        return 'the payload has no `dialog` field: every payload must carry it, null when nothing is modal'
    }

    if ($null -eq $j.dialog) {
        $script:Dialogue = $null
        return $true
    }

    $script:Dialogue = $j.dialog

    if ([string]::IsNullOrEmpty("$($j.dialog.id)")) { return 'dialog is present but carries no id' }
    if ($null -eq $j.dialog.buttons) { return 'dialog is present but carries no buttons array' }
    return $true
}

# The panel layer: the same funnel, one field beside `dialog`, and the two commands that read and work a panel.
Invoke-Check -Name 'session status carries the panels field' -Arguments @('session', 'status') -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($j.PSObject.Properties.Name -notcontains 'panels') {
        return 'the payload has no `panels` field: every payload must carry it, empty when Dorico has no other window'
    }

    if ($null -eq $j.dialog -and $null -ne $j.panels -and $j.panels.Count -gt 0) {
        # The point of the field: a window is up and it is a panel, not a dialog. Report it rather than fail.
        foreach ($panel in $j.panels) {
            Write-Line ('      NOTE  panel up: {0} - {1} (blocksQueue {2})' -f (Format-Value $panel.id), (Format-Value $panel.title), (Format-Value $panel.blocksQueue))
        }
    }

    foreach ($panel in $j.panels) {
        if ([string]::IsNullOrEmpty("$($panel.hwnd)")) { return 'a panel carries no hwnd' }
        if ([string]::IsNullOrEmpty("$($panel.id)")) { return 'a panel carries no id' }
        if ([string]::IsNullOrEmpty("$($panel.class)")) { return 'a panel carries no window class' }
    }

    return $true
}

Invoke-Check -Name 'ui panels' -Arguments @('ui', 'panels') -Offline -Assert {
    param($j, $code)

    # Offline on purpose, exactly like `ui dialog`: this needs no host and no document, and a probe that cannot run
    # must say so rather than look like an empty window list.
    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($j.PSObject.Properties.Name -notcontains 'panels') { return 'the payload has no `panels` field' }
    if ($null -eq $j.probe) { return 'probe is absent: the answer must say what the probe cost' }
    if ($j.probe.degraded -eq $true) { return ('the probe itself failed: {0}' -f (Format-Value $j.probe.error)) }

    foreach ($panel in $j.panels) {
        if ($null -eq $panel.document) { return 'a panel does not say whether it is the document window' }
        if ($null -eq $panel.blocksQueue) { return 'a panel does not say whether it blocks the queue' }
        if ($panel.document -eq $true -and $panel.blocksQueue -eq $true) { return 'the document window is reported as blocking the queue' }
    }

    if ($j.panels.Count -gt 0) {
        Write-Line ('      NOTE  {0} window(s) beside the document window: {1}' -f $j.panels.Count, (($j.panels | ForEach-Object { "$($_.id)" }) -join ', '))
    }

    # Measured and fixed: the document window used to be resolved by Process.MainWindowHandle, which is whichever
    # unowned window EnumWindows lists first - so with the library manager up the *library manager* was flagged
    # `document: true`. There is always exactly one document window, and its id is a constant now, so both halves are
    # asserted here; the second one is what would have caught the bug with a panel open.
    $documents = @($j.panels | Where-Object { $_.document -eq $true })
    if ($documents.Count -gt 1) { return ('{0} windows are flagged as the document window, expected at most one' -f $documents.Count) }
    foreach ($document in $documents) {
        if ("$($document.id)" -ne 'dorico.document-window') { return ('the document window''s id is {0}, expected the constant dorico.document-window' -f (Format-Value $document.id)) }
    }

    return $true
}

# The document window is the one window an agent has to be able to name: Dorico's Setup work is inside it.
Invoke-Check -Name 'the document window carries the Setup lists as elements' -Arguments @('ui', 'panels', '--panel', 'dorico.document-window', '--raw') -NeedsScore -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($j.count -ne 1) { return ('count is {0}, expected the one document window' -f (Format-Value $j.count)) }

    $panel = @($j.panels)[0]
    if ("$($panel.id)" -ne 'dorico.document-window') { return ('the panel read back as {0}' -f (Format-Value $panel.id)) }
    if ($panel.elementCount -lt 1) { return 'the document window reported no element at all' }

    # Measured: the player, flow and layout lists expose their rows as control type 50007 (ListItem), which this
    # build once filtered out by asking for 50029 (DataItem) instead. A row with a selection action is what the
    # Setup work needs, so a payload with no row means the reader is blind to Setup again.
    $rows = @($panel.elements | Where-Object { $_.role -eq 'listItem' })
    if ($rows.Count -lt 1) { return ('the document window reported {0} elements and not one list row, so Dorico''s Setup lists are unreachable' -f $panel.elementCount) }
    if (-not ($rows | Where-Object { $_.action -eq 'select' })) { return 'no list row reports a selection action, so no row could be chosen' }

    Write-Line ('      NOTE  {0} elements, {1} of them list rows' -f $panel.elementCount, $rows.Count)
    return $true
}

# Naming a panel that is not up is a refusal, and it needs no window at all.
Invoke-Check -Name 'ui panels --panel nope reports a miss rather than an empty list' -Arguments @('ui', 'panels', '--panel', 'dorico.no-such-panel') -Offline -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($null -eq $j.miss) { return 'naming a panel that is not up answered with neither a panel nor a `miss`' }
    if ($j.count -ne 0) { return ('count is {0}, expected 0' -f (Format-Value $j.count)) }
    return $true
}

# Working an element of a panel that is not up refuses with PanelNotFound, exit 1, like every other refusal.
Invoke-Check -Name 'ui press --panel with no such panel refuses PanelNotFound' -Arguments @('ui', 'press', '--panel', 'dorico.no-such-panel', '--element', 'e1') -Offline -Assert {
    param($j, $code)

    if ($null -eq $j) { return ('stdout was not one JSON object (exit code {0})' -f $code) }
    if ($j.ok -eq $true) { return 'the press was accepted, but no such panel is up' }
    if ($code -ne 1) { return ('exit code was {0}, expected 1 for a refusal' -f $code) }
    if ("$($j.error.code)" -ne 'PanelNotFound') { return ('error.code is {0}, expected PanelNotFound' -f (Format-Value $j.error.code)) }
    if ($j.PSObject.Properties.Name -notcontains 'panels') { return 'the failure payload has no `panels` field, but every payload must carry it' }
    return $true
}

# Neither a dialog nor a panel named: the invocation is wrong, and the refusal has to say so rather than guess.
Invoke-Check -Name 'ui press with no window named is an InvalidArgument' -Arguments @('ui', 'press', '--dialog', 'dorico.export-musicxml') -Offline -Assert {
    param($j, $code)

    if ($null -eq $j) { return ('stdout was not one JSON object (exit code {0})' -f $code) }
    if ($j.ok -eq $true) { return 'the press was accepted with no element named' }
    if ($code -ne 1) { return ('exit code was {0}, expected 1 for a usage error' -f $code) }
    return $true
}

Invoke-Check -Name 'ui dialog' -Arguments @('ui', 'dialog') -Offline -Assert {
    param($j, $code)

    # Offline on purpose: this command needs no host and no document, so it runs and reports even with Dorico
    # closed, and a probe that cannot run at all must say so rather than look like "nothing is modal".
    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ($j.PSObject.Properties.Name -notcontains 'dialog') { return 'the payload has no `dialog` field' }
    if ($null -eq $j.probe) { return 'probe is absent: the answer must say what the probe cost even when it found nothing' }
    if ([string]::IsNullOrEmpty("$($j.probe.how)")) { return 'probe.how is absent' }
    if ($j.probe.degraded -eq $true) { return ('the probe itself failed: {0}' -f (Format-Value $j.probe.error)) }

    if ($null -eq $j.dialog) {
        # The expected state on a Dorico with nothing modal, and the field the whole feature is built on.
        $script:Dialogue = $null
        return $true
    }

    # A dialog being up is not a failure of this check - it is a state the run has to report, because everything
    # below it acts on whichever document Dorico has active.
    $script:Dialogue = $j.dialog
    Write-Line ('      NOTE  a modal dialog is up right now: {0} - {1}' -f (Format-Value $j.dialog.id), (Format-Value $j.dialog.title))
    Write-Line ('            pressWith: {0}' -f (Format-Value $j.dialog.pressWith))
    return $true
}

# The refusal is the contract, and it needs no dialog at all: with nothing modal there is nothing to press in.
Invoke-Check -Name 'ui press with nothing modal refuses DialogNotFound' -Arguments @('ui', 'press', '--dialog', 'nope', '--button', 'b1') -Offline -Assert {
    param($j, $code)

    if ($null -eq $j) { return ('stdout was not one JSON object (exit code {0})' -f $code) }
    if ($j.ok -eq $true) {
        return ('the press was accepted, but nothing is modal: `pressed` is {0}' -f (Format-Value $j.pressed))
    }

    if ($code -ne 1) { return ('exit code was {0}, expected 1 for a refusal' -f $code) }
    if ("$($j.error.code)" -ne 'DialogNotFound') { return ('error.code is {0}, expected DialogNotFound' -f (Format-Value $j.error.code)) }
    if ($null -eq $j.error.message) { return 'the refusal carries no message' }
    if ($j.PSObject.Properties.Name -notcontains 'dialog') { return 'the failure payload has no `dialog` field, but every payload must carry it' }

    # A refusal is the caller's fault and a probe failure is not, so the two exit codes have to stay apart.
    return $true
}

# --------------------------------------------------------------------------------------------------------------
# reading the open document back: the live read is the default and has no side effect
# --------------------------------------------------------------------------------------------------------------

Invoke-Check -Name 'session read (live)' -Arguments @('session', 'read', '--limit', '5') -NeedsScore -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ("$($j.source)" -ne 'live') { return ('source is {0}, expected live: the default read must not save the document' -f (Format-Value $j.source)) }
    if ($null -eq $j.score.openScoreId) { return 'score.openScoreId is absent' }
    if (($null -ne $script:ScoreId) -and ($j.score.openScoreId -ne $script:ScoreId)) { return ('score.openScoreId is {0}, but session status reported {1}' -f (Format-Value $j.score.openScoreId), $script:ScoreId) }
    if ($j.counts.flows -lt 1) { return ('counts.flows is {0}: the script walked no flow' -f (Format-Value $j.counts.flows)) }
    if ($j.counts.streams -lt 1) { return ('counts.streams is {0}: the script read no stream' -f (Format-Value $j.counts.streams)) }
    if ($null -eq $j.script) { return 'script is absent: the payload must say which command produced the read' }
    if ($null -eq $j.counts.unparsedLines) { return 'counts.unparsedLines is absent: unreadable printer lines must be counted, not hidden' }
    return $true
}

# --------------------------------------------------------------------------------------------------------------
# the write paths. Both need -AllowWrite, because they change the open document or the file it points at.
# --------------------------------------------------------------------------------------------------------------

$rebindArguments = @('session', 'read', '--rebind')
if ($null -ne $script:ScoreId) { $rebindArguments += @('--expect-score', $script:ScoreId) }

$script:LastJson = $null
Invoke-WriteCheck -Name 'session read --rebind' -Arguments $rebindArguments -NeedsScore `
    -Reason 'it saves a copy with File.SaveAs AND rebinds the open document to that copy.' -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ("$($j.source)" -ne 'file') { return ('source is {0}, expected file: --rebind reads the saved copy' -f (Format-Value $j.source)) }
    if ($j.save.document.repointed -ne $true) { return ('save.document.repointed is {0}, expected true: File.SaveAs rebinds the document' -f (Format-Value $j.save.document.repointed)) }
    if ($null -eq $j.save.file.path) { return 'save.file.path is absent: the payload must say where the copy went' }
    if ($null -eq $j.decodedFrom) { return 'decodedFrom is absent: the payload must say which file was decoded' }

    # The decoded half of this payload is ProjectFileView.Report, whose top-level keys are file/metadata/members/
    # score - so the note counts are at score.counts, not at the top level. notesReturned only exists once the copy
    # has been read (ProjectFileView.Report -> Score: ["counts"] { ["notesReturned"] = notes.Count }), and the
    # notes array it counts is written unconditionally at the end of Score, so the two must agree.
    if ($null -eq $j.score) { return 'score is absent: the saved copy was not decoded' }
    if ($null -eq $j.score.counts) { return 'score.counts is absent: the saved copy was not decoded' }
    if ($null -eq $j.score.counts.notesReturned) { return 'score.counts.notesReturned is absent: the saved copy was not decoded' }
    if ((Get-ItemCount $j.score.notes) -ne $j.score.counts.notesReturned) { return ('score.counts.notesReturned is {0} but score.notes carries {1}: the decoded list and its count disagree' -f $j.score.counts.notesReturned, (Get-ItemCount $j.score.notes)) }
    return $true
}

# Where the copy went is the one thing an operator has to know after a rebind, so it gets its own line.
if (($null -ne $script:LastJson) -and ($null -ne $script:LastJson.save.file.path)) {
    Write-Line ('      rebind copy: {0}' -f $script:LastJson.save.file.path)
    Write-Line '      the open document now points at that copy; re-open your original file to undo it'
}

Invoke-Check -Name 'note insert --dry-run' -Arguments @('note', 'insert', '--notes', 'C5', '--duration', 'crotchet', '--dry-run') -NeedsScore -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    # The host returns the plan AS `note` - HostServer.Notes.cs builds `var plan = new JsonObject { ["dryRun"] =
    # true, ["notes"] = NotesJson(notes), ... }` and ends with `return new JsonObject { ["note"] = plan };` - so
    # the fields are note.dryRun / note.notes / note.steps. (This is not `command send --dry-run`, whose payload
    # does nest the plan under a `plan` key.)
    if ($null -eq $j.note) { return 'note is absent' }
    $ok = Assert-Flag $j.note.dryRun 'note.dryRun' $true
    if ($ok -ne $true) { return $ok }
    if ((Get-ItemCount $j.note.notes) -lt 1) { return 'note.notes is empty: the C5 was not read' }
    if ((@($j.note.notes) -join ',') -notlike '*C5*') { return ('note.notes is {0}, expected C5' -f (@($j.note.notes) -join ',')) }
    if ("$($j.note.duration)" -ne 'kCrotchet') { return ('note.duration is {0}, expected kCrotchet' -f (Format-Value $j.note.duration)) }
    if ((Get-ItemCount $j.note.steps) -lt 1) { return 'note.steps is empty: the dry run planned no command' }
    return $true
}

# --------------------------------------------------------------------------------------------------------------
# the offline checks: the fixture needs no Dorico at all, and `export text` is the one unattended export
# --------------------------------------------------------------------------------------------------------------

Invoke-Check -Name 'file read (fixture.dorico)' -Arguments @('file', 'read', '--path', $script:Fixture, '--limit', '5', '--layout') -Offline -Assert {
    param($j, $code)

    if (-not (Test-Path -LiteralPath $script:Fixture -PathType Leaf)) {
        return ('the committed fixture is missing: {0}' -f $script:Fixture)
    }

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ("$($j.file.kind)" -notlike '*ZIP*') { return ('file.kind is {0}, expected a Dorico project (ZIP package)' -f (Format-Value $j.file.kind)) }
    if ($j.score.counts.notes -ne 3) { return ('score.counts.notes is {0}, but fixture.dorico holds three notes' -f (Format-Value $j.score.counts.notes)) }
    if ((Get-ItemCount $j.metadata.players) -lt 1) { return 'metadata.players is empty: the fixture has one Piano player' }
    if ((Get-ItemCount $j.score.notes) -ne 3) { return ('score.notes carries {0} entries, expected the fixture''s 3 notes' -f (Get-ItemCount $j.score.notes)) }

    # The player groups and the layout view state are the two reads the engraving checklist's group and
    # transposition items need, so the fixture check asserts they are in every payload.
    if ($null -eq $j.score.playerGroups) { return 'score.playerGroups is absent: every file read payload must carry it' }
    if ((Get-ItemCount $j.score.layout.layouts) -lt 2) { return 'the fixture has two layouts but the layout block carries fewer' }
    foreach ($layout in @($j.score.layout.layouts)) {
        if ($null -eq $layout.condensing) { return ('layout {0} reports no condensing flag' -f $layout.id) }
        if ("$($layout.transpositionType)" -notlike 'k*') { return ('layout {0} reports transpositionType {1}' -f $layout.id, (Format-Value $layout.transpositionType)) }
    }

    $pitch = @($j.score.notes | ForEach-Object { $_.pitch }) -join ','
    if ($pitch -notlike '*F#*') { return ('the fixture''s notes are {0}, expected an F sharp among them' -f $pitch) }
    return $true
}

# export text writes a CSV into a folder this script owns under the system temporary directory: no document is
# touched, and no dialog is opened (unlike `export musicxml`, which waits for a click and is deliberately absent).
New-Item -ItemType Directory -Path $script:ExportDir -Force | Out-Null
$script:ExportCsv = Join-Path $script:ExportDir ('smoke-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '.csv')

$exportArguments = @('export', 'text', '--path', $script:ExportCsv)
if ($null -ne $script:ScoreId) { $exportArguments += @('--expect-score', $script:ScoreId) }

Invoke-Check -Name 'export text' -Arguments $exportArguments -NeedsScore -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    if ("$($j.export.ack)" -ne 'kOK') { return ('export.ack is {0}, expected kOK' -f (Format-Value $j.export.ack)) }
    if ($j.export.count -ne 1) { return ('export.count is {0}: Dorico wrote no CSV' -f (Format-Value $j.export.count)) }
    if ($j.export.bytes -lt 1) { return ('export.bytes is {0}, expected a non-empty CSV' -f (Format-Value $j.export.bytes)) }
    if ("$($j.export.path)" -notlike '*.csv') { return ('export.path is {0}, expected the .csv Dorico forces' -f (Format-Value $j.export.path)) }
    if (-not (Test-Path -LiteralPath $j.export.path -PathType Leaf)) { return ('the CSV Dorico reported is not on disk: {0}' -f $j.export.path) }
    return $true
}

# --------------------------------------------------------------------------------------------------------------
# the one check that really writes a note. Last, so that everything above has already been read once.
# --------------------------------------------------------------------------------------------------------------

Write-Line ''
Write-Line 'NOTE  with -AllowWrite the next check enters C5 at the START of the FIRST FLOW of the active document'
Write-Line '      and then reads the score back. Ctrl+Z in Dorico undoes it. On a build older than the A4 change the'
Write-Line '      read-back half still saves a copy with File.SaveAs and rebinds the document - note.read.source in'
Write-Line '      the JSON below says which route ran (live = no save, no rebinding).'

$insertArguments = @('note', 'insert', '--notes', 'C5', '--duration', 'crotchet', '--from-start', '--read', '--limit', '5')
if ($null -ne $script:ScoreId) { $insertArguments += @('--expect-score', $script:ScoreId) }

Invoke-WriteCheck -Name 'note insert (real write, at the start of the first flow)' -Arguments $insertArguments -NeedsScore `
    -Reason 'it writes a C5 into the open document.' -Assert {
    param($j, $code)

    $ok = Assert-Ok $j $code
    if ($ok -ne $true) { return $ok }

    # --notes C5 is one note, not a chord, and the host only writes `chords` when a group holds more than one
    # pitch (HostServer.Notes.cs: chordCount = events.Count(group => group.Count > 1); if (chordCount > 0) ...).
    # The flat list is always written (payload["notes"] = NotesJson(notes)), so that is where the C5 is asserted.
    if ((Get-ItemCount $j.note.notes) -ne 1) { return ('note.notes carries {0} entries, expected the one pitch that was asked for' -f (Get-ItemCount $j.note.notes)) }
    if ((@($j.note.notes) -join ',') -ne 'C5') { return ('note.notes is {0}, expected C5' -f (@($j.note.notes) -join ',')) }
    if ((Get-ItemCount $j.note.steps) -lt 1) { return 'note.steps is empty: no command was sent' }

    foreach ($step in @($j.note.steps)) {
        if ("$($step.ack)" -ne 'kOK') { return ('{0} answered {1}: Dorico refused a step of the insert' -f (Format-Value $step.name), (Format-Value $step.ack)) }
    }

    if ($null -eq $j.note.read) { return 'note.read is absent: --read must report what the score now holds' }
    if ("$($j.note.read.source)" -notin @('live', 'file')) { return ('note.read.source is {0}, expected live or file' -f (Format-Value $j.note.read.source)) }

    if ("$($j.note.read.source)" -eq 'live') {
        if ($j.note.read.counts.notes -lt 1) { return ('note.read.counts.notes is {0}: the live read found no note after the insert' -f (Format-Value $j.note.read.counts.notes)) }
    }
    elseif ($null -eq $j.note.read.save.file.path) {
        return 'note.read.source is file, but note.read.save.file.path is absent: the copy it rebound the document to is not named'
    }

    return $true
}

# --------------------------------------------------------------------------------------------------------------
# BY HAND: the real dialogs. A check cannot raise one, so this is a checklist rather than a check - it prints
# nothing into the counts above and the run stays green without it. Raise ONE dialog at a time, read it, and
# compare what came back with what the window really shows. Add a line here when a catalogue entry is added.
#
# For each one:  dorico-use ui dialog        (then, to answer it: ui press --dialog <id> --button <id>)
#
#   dorico.export-musicxml   raise it: dorico-use export musicxml --path .\export --wait-ms 120000
#                            expect: title 导出MusicXML, buttons 确定/取消, two labelled 选择全部, one labelled
#                            with a glyph, and pressWith pointing at `cancel`.
#   dorico.open-picker       raise it: dorico-use command send --name Project.ImportLibrary --expect-score <id>
#                            (it needs an open document, and the command does not return until the picker is
#                            answered - measured 45 to 106 s, so read `execution` on whatever comes next)
#                            expect: title 打开, class #32770, owner the document window, blocksQueue true,
#                            buttons accept (name 打开, classic id 1, safe:false) and cancel (取消, classic id 2,
#                            safe:true), and pressWith pointing at cancel.
#                            its controls include edit-1148 (`文件名(N):`, action `value`) and 1136 (`文件类型(T):`).
#                            then:  dorico-use ui press --dialog dorico.open-picker --element edit-1148 --value <a path>
#                            expect: worked.action "value" and the re-read of the same window carrying the path.
#                            to close it:  dorico-use ui press --dialog dorico.open-picker --button cancel
#                            DO NOT press `accept` on a document you care about: accepting an import changes it,
#                            which is why it is not safe.
#   dorico.folder-picker     raise it: ui press --dialog dorico.export-musicxml --button folder-picker
#                            expect: title 选择文件夹, class #32770, buttons accept (classic id 1) and cancel (2).
#                            It has NO file-name box, so ui press --path must refuse PickerNotSettable.
#   dorico.unsaved-changes   raise it: change a scratch document, then close it (File.Close)
#                            NOT MEASURED YET: its caption has never been read, so it is matched on the labels
#                            丢弃 and 取消 alone, and 丢弃 must be refused without --allow-destructive.
#                            Measured on 2026-10-11: File.Close on a document that HAD been edited raised nothing
#                            at all and the document closed, so this prompt may not be reachable this way.
#   dorico.crash-report      raise it: crash Dorico (nothing here does that on purpose)
#                            expect: title 向 Steinberg 发送崩溃报告, buttons 发送到 Steinberg / 不发送 / 不再询问,
#                            all three sharing one automation id, only `dismiss` safe.
#
# BY HAND: the panels. Same rule - a check cannot raise one, and `ui panels` reads a window that is already up.
#
#   dorico.library-manager   raise it: dorico-use command send --name Project.ShowLibraryImportExportDialog
#                            (it needs an open document), then:  dorico-use ui panels --panel dorico.library-manager
#                            expect: title 库管理, class Qt6101QWindowIcon, blocksQueue false, and five elements -
#                            出厂设置 (off), 用户库 (on), Dorico 工程 (off), each action `toggle`; 关闭 and 应用,
#                            each action `invoke`.
#                            then:  dorico-use ui press --panel dorico.library-manager --element dorico
#                            expect: worked.stateBefore "off" and worked.stateAfter.toggle "on", and the panel list
#                            in the same payload showing the new state. NOTE the three collection check boxes are
#                            exclusive: choosing one turns the others off, measured.
#                            to close it:  dorico-use ui press --panel dorico.library-manager --element button-4
#                            Measured: the panel has NO file control - its whole tree is four levels of panes,
#                            labels and groups around those five elements, and its collection rows have no name and
#                            no value - so `Project.ImportLibrary` above is the route to a file, not this panel.
#
#   dorico.document-window   NOT a panel of its own: Dorico's own document window, and the check above reads it.
#                            raise it: nothing - it is always up when a document is open.
#                            expect: `ui panels` listing one window with id dorico.document-window and
#                            document:true, whichever other windows are up (measured: it used to be the library
#                            manager that got that flag, because the probe took Process.MainWindowHandle).
#                            then:  dorico-use ui panels --panel dorico.document-window
#                            expect: the Setup lists - listItem rows for the players, the flows and the layouts,
#                            each action `select`, and exactly one of them `state: "selected"`.
#                            acting on one:  dorico-use ui press --panel dorico.document-window --element <row id>
#                            expect: worked.stateAfter.selected true and the panels array in the same payload
#                            showing the row that was selected before now unselected. Measured: only the rows the
#                            list has realised are there (30 of 49 players, 28 of 45 layouts on one project), and
#                            a row's id depends on what was realised - so read and act in one invocation.
#
# Everything else is reported as unknown and refused, which is the designed answer for a window nobody has read.
# A panel that is not in PanelCatalogue is still listed in full with an `unknown.<hash>` id, and working one of its
# elements needs --allow-unknown-dialog.
# --------------------------------------------------------------------------------------------------------------

# --------------------------------------------------------------------------------------------------------------
# What the run added up to. Anything not run keeps the exit code non-zero, so a green run means every check ran.
# --------------------------------------------------------------------------------------------------------------

Write-Line ''
Write-Line ('SUMMARY  {0} passed, {1} failed, {2} skipped (needs -AllowWrite), {3} not run' -f `
    $script:Passed, $script:Failed, $script:Skipped, $script:NotRun)

if ($script:Failed -gt 0) {
    Write-Line ('EXIT 1  - {0} check(s) FAILED' -f $script:Failed)
    exit 1
}

if ($script:NotRun -gt 0) {
    Write-Line ('EXIT 2  - nothing failed, but {0} check(s) could not be run: {1}' -f $script:NotRun, $script:NotRunReason)
    Write-Line '          a green run must mean every check ran, so this is not success.'
    exit 2
}

Write-Line 'EXIT 0  - every check passed'
exit 0
