#Requires -Version 7.2
<#
.SYNOPSIS
    Repeats simulated cross-module reliability tests with isolated data and real local IPC.
.DESCRIPTION
    Runs only ReliabilityIntegrationTests. Never installs, starts, stops or upgrades the real
    LinkFlow service; never launches hardware diagnostics, the real app, or an installer.
    Each iteration gets its own TEMP/TMP, LINKFLOW_DATA_DIR and LINKFLOW_PIPE.
    Results, logs and a machine-readable summary remain in the results directory.
.EXAMPLE
    .\scripts\soak.ps1 -Repeat 5 -NoBuild
.EXAMPLE
    .\scripts\soak.ps1 -DurationSeconds 600 -NoBuild -IterationTimeoutSeconds 120
#>
[CmdletBinding(DefaultParameterSetName = 'Repeat')]
param(
    [Parameter(ParameterSetName = 'Repeat')][ValidateRange(1, 100000)][int]$Repeat = 5,
    [Parameter(Mandatory, ParameterSetName = 'Duration')][ValidateRange(1, 604800)][int]$DurationSeconds,
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [ValidateRange(15, 1800)][int]$IterationTimeoutSeconds = 120,
    [switch]$NoBuild,
    [string]$ResultsDirectory
)

$ErrorActionPreference = 'Stop'
$soakRepository = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).ProviderPath
$soakProject = Join-Path $soakRepository 'tests\LinkFlow.Tests\LinkFlow.Tests.csproj'
$soakDotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop).Source
$soakTemporaryParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$soakTemporaryRoot = Join-Path $soakTemporaryParent ('LinkFlow-soak-' + [Guid]::NewGuid().ToString('N'))
if ([string]::IsNullOrWhiteSpace($ResultsDirectory)) {
    $ResultsDirectory = Join-Path $soakRepository ('artifacts\soak\' + [DateTime]::Now.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
}
$soakResults = [IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Path $soakResults -Force | Out-Null
New-Item -ItemType Directory -Path $soakTemporaryRoot | Out-Null
$soakOwnedProcesses = [Collections.Generic.List[Diagnostics.Process]]::new()
$soakRuns = [Collections.Generic.List[object]]::new()
$soakStarted = [DateTimeOffset]::UtcNow
$soakClock = [Diagnostics.Stopwatch]::StartNew()
$soakSucceeded = $false
$soakFailure = $null
$soakExpectedTestCount = $null
$soakAssemblyHashes = [ordered]@{}

function Stop-SoakProcess {
    param([Diagnostics.Process]$Process)
    try {
        if (-not $Process.HasExited) {
            # Only a Process created by this script is passed here; never enumerate/kill unrelated test hosts.
            $Process.Kill($true)
            $cleanupClock = [Diagnostics.Stopwatch]::StartNew()
            while (-not $Process.WaitForExit(250) -and $cleanupClock.Elapsed.TotalSeconds -lt 5) { }
        }
    }
    catch [InvalidOperationException] { }
    catch [System.ComponentModel.Win32Exception] { }
}

function Invoke-SoakDotnet {
    param(
        [string[]]$Arguments,
        [string]$IsolationDirectory,
        [string]$LogPrefix,
        [int]$TimeoutSeconds
    )
    $temporaryDirectory = Join-Path $IsolationDirectory 'temp'
    $dataDirectory = Join-Path $IsolationDirectory 'data'
    New-Item -ItemType Directory -Path $temporaryDirectory, $dataDirectory -Force | Out-Null
    $start = [Diagnostics.ProcessStartInfo]::new($soakDotnet)
    $start.WorkingDirectory = $soakRepository
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    foreach ($argument in $Arguments) { $start.ArgumentList.Add($argument) }
    $start.Environment['TEMP'] = $temporaryDirectory
    $start.Environment['TMP'] = $temporaryDirectory
    $start.Environment['LINKFLOW_DATA_DIR'] = $dataDirectory
    $start.Environment['LINKFLOW_PIPE'] = 'LinkFlow.Soak.' + [Guid]::NewGuid().ToString('N')
    $start.Environment['LINKFLOW_SIMULATE'] = '1'
    $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $start
    $soakOwnedProcesses.Add($process)
    $elapsed = [Diagnostics.Stopwatch]::StartNew()
    if (-not $process.Start()) { throw 'Unable to start the isolated dotnet process.' }
    $stdoutTask = $process.StandardOutput.ReadToEndAsync()
    $stderrTask = $process.StandardError.ReadToEndAsync()
    $timedOut = $false
    $peakWorkingSet = 0L
    try {
        while (-not $process.WaitForExit(250)) {
            try { $peakWorkingSet = [Math]::Max($peakWorkingSet, $process.PeakWorkingSet64) }
            catch [InvalidOperationException] { }
            if ($elapsed.Elapsed.TotalSeconds -ge $TimeoutSeconds) {
                $timedOut = $true
                Stop-SoakProcess -Process $process
                break
            }
        }
        if (-not $process.HasExited) { throw 'The owned test process did not exit after termination.' }
        $stdout = $stdoutTask.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
        $stderr = $stderrTask.WaitAsync([TimeSpan]::FromSeconds(5)).GetAwaiter().GetResult()
        [IO.File]::WriteAllText($LogPrefix + '.stdout.txt', $stdout, [Text.UTF8Encoding]::new($false))
        [IO.File]::WriteAllText($LogPrefix + '.stderr.txt', $stderr, [Text.UTF8Encoding]::new($false))
        return [pscustomobject]@{
            ExitCode = $process.ExitCode
            TimedOut = $timedOut
            DurationSeconds = [Math]::Round($elapsed.Elapsed.TotalSeconds, 3)
            PeakWorkingSetBytes = $peakWorkingSet
            StandardOutput = $stdout
            StandardError = $stderr
        }
    }
    finally {
        Stop-SoakProcess -Process $process
        $soakOwnedProcesses.Remove($process) | Out-Null
        $process.Dispose()
    }
}

try {
    if (-not $NoBuild) {
        Write-Host 'Building the reliability test project once...' -ForegroundColor Cyan
        $build = Invoke-SoakDotnet -Arguments @('build', $soakProject, '--configuration', $Configuration, '-warnaserror', '--nologo') `
            -IsolationDirectory (Join-Path $soakTemporaryRoot 'build') -LogPrefix (Join-Path $soakResults 'build') -TimeoutSeconds 300
        if ($build.TimedOut -or $build.ExitCode -ne 0) { throw 'Build failed or timed out. See build.stdout.txt and build.stderr.txt.' }
    }
    $testAssembly = Join-Path $soakRepository "tests\LinkFlow.Tests\bin\$Configuration\net10.0-windows\LinkFlow.Tests.dll"
    if (-not (Test-Path -LiteralPath $testAssembly -PathType Leaf)) { throw 'Test assembly is missing. Build first or omit -NoBuild.' }
    foreach ($assemblyName in 'LinkFlow.Tests.dll', 'LinkFlow.Core.dll', 'LinkFlow.App.dll') {
        $assemblyPath = Join-Path (Split-Path -Parent $testAssembly) $assemblyName
        if (-not (Test-Path -LiteralPath $assemblyPath -PathType Leaf)) { throw "Required test dependency is missing: $assemblyName" }
        $soakAssemblyHashes[$assemblyName] = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash
    }
    $soakTestingClock = [Diagnostics.Stopwatch]::StartNew()
    $soakIteration = 0
    do {
        $soakIteration++
        $iterationName = 'iteration-{0:D5}' -f $soakIteration
        $iterationResults = Join-Path $soakResults $iterationName
        New-Item -ItemType Directory -Path $iterationResults | Out-Null
        Write-Host "$iterationName : simulated safety, recovery, command truth, persistence and bounded queues" -ForegroundColor Cyan
        $run = Invoke-SoakDotnet -Arguments @('test', $soakProject, '--configuration', $Configuration, '--no-build', '--no-restore',
            '--filter', 'FullyQualifiedName~ReliabilityIntegrationTests', '--logger', 'console;verbosity=minimal',
            '--logger', 'trx;LogFileName=soak.trx', '--results-directory', $iterationResults) `
            -IsolationDirectory (Join-Path $soakTemporaryRoot $iterationName) -LogPrefix (Join-Path $iterationResults 'test') -TimeoutSeconds $IterationTimeoutSeconds
        $runtimeLogDirectory = Join-Path $iterationResults 'runtime-logs'
        $runtimeLogBytes = 0L
        $runtimeLogIndex = 0
        Get-ChildItem -LiteralPath (Join-Path $soakTemporaryRoot $iterationName) -Filter '*.log' -File -Recurse -ErrorAction SilentlyContinue |
            Where-Object { -not ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) } | Select-Object -First 32 | ForEach-Object {
                if ($_.Length -le 2MB -and $runtimeLogBytes + $_.Length -le 8MB) {
                    New-Item -ItemType Directory -Path $runtimeLogDirectory -Force | Out-Null
                    $runtimeLogIndex++
                    Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $runtimeLogDirectory ('{0:D2}-{1}' -f $runtimeLogIndex, $_.Name))
                    $runtimeLogBytes += $_.Length
                }
            }
        $trxPath = Join-Path $iterationResults 'soak.trx'
        $total = 0; $passed = 0; $failed = 0; $skipped = 0
        if (Test-Path -LiteralPath $trxPath -PathType Leaf) {
            [xml]$trx = Get-Content -LiteralPath $trxPath -Raw
            $counters = $trx.TestRun.ResultSummary.Counters
            $total = [int]$counters.total
            $passed = [int]$counters.passed
            $failed = [int]$counters.failed + [int]$counters.error + [int]$counters.aborted
            $skipped = [int]$counters.notExecuted
        }
        $record = [pscustomobject]@{
            Iteration = $soakIteration; ExitCode = $run.ExitCode; TimedOut = $run.TimedOut
            Tests = $total; Passed = $passed; Failed = $failed; Skipped = $skipped
            DurationSeconds = $run.DurationSeconds; PeakDotnetWorkingSetBytes = $run.PeakWorkingSetBytes
            ResultsDirectory = $iterationResults
        }
        $soakRuns.Add($record)
        if ($run.TimedOut -or $run.ExitCode -ne 0 -or $total -eq 0 -or $failed -ne 0 -or $skipped -ne 0 -or $passed -ne $total) {
            throw "$iterationName failed validation (exit=$($run.ExitCode), timeout=$($run.TimedOut), total=$total, passed=$passed, failed=$failed, skipped=$skipped). See its TRX and logs."
        }
        if ($null -eq $soakExpectedTestCount) { $soakExpectedTestCount = $total }
        elseif ($soakExpectedTestCount -ne $total) { throw 'The selected test count changed during the soak. Freeze source/builds and run again.' }
        foreach ($assemblyName in $soakAssemblyHashes.Keys) {
            $currentHash = (Get-FileHash -LiteralPath (Join-Path (Split-Path -Parent $testAssembly) $assemblyName) -Algorithm SHA256).Hash
            if ($currentHash -cne $soakAssemblyHashes[$assemblyName]) { throw "The compiled assembly $assemblyName changed during the soak. Build once and freeze binaries before repeating." }
        }
        Write-Host ("PASS: {0}/{1} checks in {2:n1}s" -f $passed, $total, $run.DurationSeconds) -ForegroundColor Green
        $continue = if ($PSCmdlet.ParameterSetName -eq 'Duration') { $soakTestingClock.Elapsed.TotalSeconds -lt $DurationSeconds } else { $soakIteration -lt $Repeat }
    } while ($continue)
    $soakSucceeded = $true
}
catch {
    $soakFailure = $_.Exception.Message
    throw
}
finally {
    foreach ($ownedProcess in $soakOwnedProcesses) { Stop-SoakProcess -Process $ownedProcess; $ownedProcess.Dispose() }
    $summary = [ordered]@{
        SchemaVersion = 1; StartedAtUtc = $soakStarted.ToString('O'); FinishedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        Succeeded = $soakSucceeded; Failure = $soakFailure; ElapsedSeconds = [Math]::Round($soakClock.Elapsed.TotalSeconds, 3)
        Configuration = $Configuration; Filter = 'FullyQualifiedName~ReliabilityIntegrationTests'
        AssemblySha256 = $soakAssemblyHashes
        Scope = 'Repeated simulated fault/recovery checks with real isolated IPC and persistence; not physical hardware or uninterrupted single-process endurance qualification.'
        Runs = @($soakRuns.ToArray())
    }
    [IO.File]::WriteAllText((Join-Path $soakResults 'summary.json'), ($summary | ConvertTo-Json -Depth 6), [Text.UTF8Encoding]::new($false))
    # Only remove the exact random directory created by this script, after validating its resolved parent.
    $resolvedTemporary = (Resolve-Path -LiteralPath $soakTemporaryRoot -ErrorAction SilentlyContinue).ProviderPath
    if ($resolvedTemporary -eq $soakTemporaryRoot -and $resolvedTemporary.StartsWith($soakTemporaryParent, [StringComparison]::OrdinalIgnoreCase) -and
        (Split-Path -Leaf $resolvedTemporary) -match '^LinkFlow-soak-[0-9a-f]{32}$') {
        Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
    }
    Write-Host "Soak results: $soakResults"
}
