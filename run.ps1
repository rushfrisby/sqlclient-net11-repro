# Builds the repro for net10.0 and net11.0 and runs them alternately, each pinned to the same CPUs.
#   ./run.ps1                                   SqlClient 7.1.1, 3 rounds, 4 and 16 workers
#   ./run.ps1 -SqlClientVersion 8.0.0-preview1.26313.4
#   ./run.ps1 -ManagedSni
param(
    [string]$SqlClientVersion = '7.1.1',
    [int]$Rounds = 3,
    [int[]]$Concurrency = @(4, 16),
    [int]$Seconds = 15,
    [long]$Affinity = 0xFF,     # logical CPUs 0-7; on a hybrid Intel CPU keep this on performance cores
    [switch]$ManagedSni,
    [switch]$Mars,
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$project = Join-Path $root 'SqlClientCpuRepro.csproj'
$out = Join-Path $root "bin/repro-$SqlClientVersion"
if (-not $IsWindows -and -not $IsLinux) {
    throw 'This runner supports Windows and Linux.'
}
if ($IsLinux) {
    $null = Get-Command taskset -ErrorAction Stop
}
if (-not $NoRestore) {
    dotnet restore $project -p:SqlClientVersion=$SqlClientVersion -nologo -v minimal
    if ($LASTEXITCODE -ne 0) { throw 'restore failed' }
}
foreach ($tfm in 'net10.0', 'net11.0') {
    dotnet build $project --no-restore -c Release -f $tfm -p:SqlClientVersion=$SqlClientVersion -o (Join-Path $out $tfm) -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "build failed for $tfm" }
}
$env:REPRO_MANAGED_SNI = if ($ManagedSni) { '1' } else { '0' }
$env:REPRO_MARS = if ($Mars) { '1' } else { '0' }

if ($IsWindows) {
    $self = [System.Diagnostics.Process]::GetCurrentProcess()
    $all = $self.ProcessorAffinity
}
for ($r = 1; $r -le $Rounds; $r++) {
    foreach ($c in $Concurrency) {
        foreach ($tfm in 'net10.0', 'net11.0') {
            $dll = Join-Path $out "$tfm/SqlClientCpuRepro.dll"
            # Set affinity before runtime startup so GC and thread pool sizing use the selected CPUs.
            if ($IsLinux) {
                taskset ([Convert]::ToString($Affinity, 16)) dotnet $dll $c $Seconds
                if ($LASTEXITCODE -ne 0) { throw "run failed for $tfm" }
            } else {
                $self.ProcessorAffinity = [IntPtr]$Affinity
                try {
                    dotnet $dll $c $Seconds
                    if ($LASTEXITCODE -ne 0) { throw "run failed for $tfm" }
                } finally {
                    $self.ProcessorAffinity = $all
                }
            }
        }
    }
}
