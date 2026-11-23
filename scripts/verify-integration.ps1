<#
.SYNOPSIS
  PerfSnap integration checks (needs administrator rights).

.DESCRIPTION
  Runs the checks that the automated tests cannot cover, using the real wpr.exe / logman.exe:
    B. Each recording profile starts, marks and saves
    C. How many seconds the flight recorder (memory mode) keeps
    D. CPU overhead of the flight recorder
    E. logman counter collection (English counter names) and reading a live .blg
    F. PerfSnap.exe smoke test: start, idle CPU/memory, hotkey save, crash recovery

  Results are written to <OutDir>\results.txt. Traces are written under <OutDir> and are NOT for Git.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\verify-integration.ps1 -OutDir C:\temp\perfsnap-verify -Exe publish\PerfSnap.exe
#>
param(
    [Parameter(Mandatory = $true)][string]$OutDir,
    [Parameter(Mandatory = $true)][string]$Exe,
    [int]$IdleSeconds = 120,
    # Which sections to run, e.g. 'F,G'
    [string]$Sections = 'B,C,D,E,F,G',
    # For section H: the bundled profile template and the buffer sizes (MB) to try
    [string]$WprpTemplate = (Join-Path $PSScriptRoot '..\src\PerfSnap\Resources\PerfSnap.wprp'),
    [string]$BufferSizes = '128,256',
    [int]$RetentionSeconds = 180,
    # Optional: PerfSnap.dll of a framework-dependent build, used when the exe is blocked
    [string]$HostDll = ''
)
$run = $Sections.Split(',') | ForEach-Object { $_.Trim().ToUpper() }

$ErrorActionPreference = 'Continue'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
$results = Join-Path $OutDir 'results.txt'
$wpr = Join-Path $env:WINDIR 'System32\wpr.exe'
$logman = Join-Path $env:WINDIR 'System32\logman.exe'
$instance = 'PerfSnap'

function Out-Result([string]$text) {
    $line = '{0:HH:mm:ss} {1}' -f (Get-Date), $text
    Write-Host $line
    Add-Content -Path $results -Value $line -Encoding UTF8
}

function Invoke-Tool([string]$exePath, [string[]]$arguments, [int]$timeoutSec = 180) {
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $psi = New-Object Diagnostics.ProcessStartInfo $exePath
    $psi.Arguments = ($arguments | ForEach-Object { if ($_ -match '\s') { '"' + $_ + '"' } else { $_ } }) -join ' '
    $psi.UseShellExecute = $false
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $psi.CreateNoWindow = $true
    $p = [Diagnostics.Process]::Start($psi)
    $out = $p.StandardOutput.ReadToEndAsync()
    $err = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit($timeoutSec * 1000)) { $p.Kill(); return [pscustomobject]@{ Code = -1; Text = 'TIMEOUT'; Ms = $sw.ElapsedMilliseconds } }
    $text = ($out.Result + ' ' + $err.Result) -replace '\s+', ' '
    [pscustomobject]@{ Code = $p.ExitCode; Text = $text.Trim(); Ms = $sw.ElapsedMilliseconds }
}

function Invoke-Wpr([string[]]$arguments, [int]$timeoutSec = 180) {
    Invoke-Tool $wpr ($arguments + @('-instancename', $instance)) $timeoutSec
}

Add-Type -TypeDefinition @"
using System;
using System.Runtime.InteropServices;
public static class SysTimes {
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    public static long[] Now() { long i, k, u; GetSystemTimes(out i, out k, out u); return new long[] { i, k, u }; }
    // percentage of total CPU in use between two samples
    public static double Busy(long[] a, long[] b) {
        double idle = b[0] - a[0], total = (b[1] - a[1]) + (b[2] - a[2]);
        return total <= 0 ? 0 : (1.0 - idle / total) * 100.0;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PERFINFO {
        public int cb; public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
            KernelTotal, KernelPaged, KernelNonpaged, PageSize; public int HandleCount, ProcessCount, ThreadCount;
    }
    [DllImport("psapi.dll")] static extern bool GetPerformanceInfo(out PERFINFO p, int size);
    // MB: available physical, kernel nonpaged, commit total
    public static double[] Memory() {
        PERFINFO p; GetPerformanceInfo(out p, Marshal.SizeOf(typeof(PERFINFO)));
        double pg = (double)p.PageSize.ToUInt64() / 1048576.0;
        return new double[] { p.PhysicalAvailable.ToUInt64() * pg, p.KernelNonpaged.ToUInt64() * pg, p.CommitTotal.ToUInt64() * pg };
    }
}
"@
function Format-Mem($m) { 'availMB={0:N0} nonpagedMB={1:N0} commitMB={2:N0}' -f $m[0], $m[1], $m[2] }

function Measure-SystemCpu([int]$seconds) {
    $a = [SysTimes]::Now(); Start-Sleep -Seconds $seconds; $b = [SysTimes]::Now()
    [math]::Round([SysTimes]::Busy($a, $b), 2)
}

function Get-FirstEventTime([string]$etl) {
    try {
        $q = New-Object Diagnostics.Eventing.Reader.EventLogQuery($etl, [Diagnostics.Eventing.Reader.PathType]::FilePath)
        $r = New-Object Diagnostics.Eventing.Reader.EventLogReader($q)
        for ($i = 0; $i -lt 200; $i++) {
            $e = $r.ReadEvent()
            if ($null -eq $e) { break }
            if ($e.TimeCreated) { return $e.TimeCreated }
        }
    } catch { return "unreadable: $($_.Exception.Message)" }
    return $null
}

$profiles = @{
    'input-latency' = @('CPU', 'DiskIO', 'Minifilter', 'DesktopComposition')
    'detailed'      = @('CPU', 'DiskIO', 'Minifilter', 'DesktopComposition', 'FileIO', 'Registry')
}
function StartArgs([string[]]$names, [switch]$FileMode) {
    $a = @(); foreach ($n in $names) { $a += '-start'; $a += $n }; if ($FileMode) { $a += '-filemode' }; $a
}

Set-Content -Path $results -Value "PerfSnap integration check" -Encoding UTF8
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
$os = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
Out-Result "A. admin=$isAdmin os=$($os.DisplayVersion) build=$($os.CurrentBuild).$($os.UBR) ui=$((Get-UICulture).Name) cpu=$env:NUMBER_OF_PROCESSORS"
if (-not $isAdmin) { Out-Result 'ABORT: not running as administrator'; exit 1 }

# Leftovers from earlier runs
Invoke-Wpr @('-cancel') 30 | Out-Null

# ---------------- B. profiles ----------------
if ($run -contains 'B') {
foreach ($name in @('input-latency', 'detailed')) {
    $r = Invoke-Wpr (StartArgs $profiles[$name]) 60
    Out-Result "B. $name start(memory): code=$($r.Code) ms=$($r.Ms) $($r.Text)"
    if ($r.Code -ne 0) {
        Invoke-Wpr @('-cancel') 30 | Out-Null
        foreach ($p in $profiles[$name]) {
            $single = Invoke-Wpr @('-start', $p) 60
            Out-Result "B.   single $p : code=$($single.Code) $($single.Text)"
            Invoke-Wpr @('-cancel') 30 | Out-Null
        }
        continue
    }
    Start-Sleep -Seconds 10
    $m = Invoke-Wpr @('-marker', 'PerfSnap_verify') 30
    Out-Result "B. $name marker: code=$($m.Code) $($m.Text)"
    $etl = Join-Path $OutDir "B_$name.etl"
    $s = Invoke-Wpr @('-stop', $etl, 'PerfSnap verify') 300
    $size = if (Test-Path $etl) { [math]::Round((Get-Item $etl).Length / 1MB, 1) } else { 0 }
    Out-Result "B. $name stop: code=$($s.Code) ms=$($s.Ms) sizeMB=$size $($s.Text)"
}

# File mode (timed capture)
$r = Invoke-Wpr (StartArgs $profiles['input-latency'] -FileMode) 60
Out-Result "B. input-latency start(filemode): code=$($r.Code) $($r.Text)"
if ($r.Code -eq 0) {
    Start-Sleep -Seconds 30
    $etl = Join-Path $OutDir 'B_filemode.etl'
    $s = Invoke-Wpr @('-stop', $etl, 'PerfSnap verify') 300
    $size = if (Test-Path $etl) { [math]::Round((Get-Item $etl).Length / 1MB, 1) } else { 0 }
    Out-Result "B. filemode 30s stop: code=$($s.Code) ms=$($s.Ms) sizeMB=$size"
}

}
# ---------------- C. retention ----------------
if ($run -contains 'C') {
foreach ($name in @('input-latency', 'detailed')) {
    $r = Invoke-Wpr (StartArgs $profiles[$name]) 60
    if ($r.Code -ne 0) { Out-Result "C. $name skipped (start failed)"; continue }
    $started = Get-Date
    Start-Sleep -Seconds 120
    $stopAt = Get-Date
    $etl = Join-Path $OutDir "C_$name.etl"
    $s = Invoke-Wpr @('-stop', $etl, 'PerfSnap verify') 300
    $first = Get-FirstEventTime $etl
    $kept = if ($first -is [datetime]) { [math]::Round(($stopAt - $first).TotalSeconds, 0) } else { "?" }
    $size = if (Test-Path $etl) { [math]::Round((Get-Item $etl).Length / 1MB, 1) } else { 0 }
    Out-Result "C. $name after 120s: stop code=$($s.Code) ms=$($s.Ms) sizeMB=$size firstEvent=$first keptSeconds=$kept (recording started $([math]::Round(($stopAt-$started).TotalSeconds,0))s before stop)"
}

}
# ---------------- D. overhead ----------------
if ($run -contains 'D') {
$base = Measure-SystemCpu 60
Out-Result "D. baseline system CPU (60s, no tracing): $base %"
foreach ($name in @('input-latency', 'detailed')) {
    $r = Invoke-Wpr (StartArgs $profiles[$name]) 60
    if ($r.Code -ne 0) { Out-Result "D. $name skipped"; continue }
    Start-Sleep -Seconds 5
    $busy = Measure-SystemCpu 60
    Invoke-Wpr @('-cancel') 60 | Out-Null
    Out-Result "D. $name flight recorder system CPU (60s): $busy % (baseline $base %)"
}
$base2 = Measure-SystemCpu 60
Out-Result "D. baseline again (60s): $base2 %"

}
# ---------------- E. logman ----------------
if ($run -contains 'E') {
$cf = Join-Path $OutDir 'counters.txt'
@(
    '\Processor(_Total)\% Processor Time', '\Processor(_Total)\% DPC Time', '\Processor(_Total)\% Interrupt Time',
    '\System\Processor Queue Length', '\Memory\Available MBytes', '\Memory\Pages/sec',
    '\PhysicalDisk(_Total)\% Idle Time', '\PhysicalDisk(_Total)\Avg. Disk Queue Length',
    '\Process(*)\% Processor Time', '\Process(*)\IO Data Bytes/sec', '\Process(*)\Working Set'
) | Set-Content -Path $cf -Encoding ASCII
$blgBase = Join-Path $OutDir 'counters\PerfSnap_Verify'
New-Item -ItemType Directory -Force -Path (Split-Path $blgBase) | Out-Null
Invoke-Tool $logman @('stop', 'PerfSnap_Verify') | Out-Null
Invoke-Tool $logman @('delete', 'PerfSnap_Verify') | Out-Null
$c = Invoke-Tool $logman @('create', 'counter', 'PerfSnap_Verify', '-cf', $cf, '-si', '00:00:05', '-f', 'bincirc', '-max', '64', '-o', $blgBase, '-ow')
Out-Result "E. logman create: code=$($c.Code) $($c.Text)"
$st = Invoke-Tool $logman @('start', 'PerfSnap_Verify')
Out-Result "E. logman start: code=$($st.Code) $($st.Text)"
Start-Sleep -Seconds 30
$blg = Get-ChildItem (Split-Path $blgBase) -Filter '*.blg' | Select-Object -First 1
if ($blg) {
    $copy = Join-Path $OutDir 'E_live_copy.blg'
    try { Copy-Item $blg.FullName $copy -ErrorAction Stop; Out-Result "E. live .blg copy: ok ($($blg.Name), $([math]::Round($blg.Length/1KB,0)) KB)" }
    catch { Out-Result "E. live .blg copy: FAILED $($_.Exception.Message)" }
    $relog = Invoke-Tool (Join-Path $env:WINDIR 'System32\relog.exe') @($blg.FullName, '-q')
    Out-Result "E. relog -q on live file: code=$($relog.Code) $($relog.Text.Substring(0, [Math]::Min(400, $relog.Text.Length)))"
} else { Out-Result 'E. no .blg file produced' }
$sp = Invoke-Tool $logman @('stop', 'PerfSnap_Verify')
Out-Result "E. logman stop: code=$($sp.Code)"
if ($blg) {
    $relog2 = Invoke-Tool (Join-Path $env:WINDIR 'System32\relog.exe') @($blg.FullName, '-q')
    Out-Result "E. relog -q after stop: code=$($relog2.Code) $($relog2.Text.Substring(0, [Math]::Min(400, $relog2.Text.Length)))"
}
Invoke-Tool $logman @('delete', 'PerfSnap_Verify') | Out-Null

}
# ---------------- F. PerfSnap.exe ----------------
if ($run -contains 'F') {
$exeFull = (Resolve-Path $Exe).Path
$captures = Join-Path $env:LOCALAPPDATA 'PerfSnap\captures'
$before = @(Get-ChildItem $captures -Directory -ErrorAction SilentlyContinue | ForEach-Object Name)
# Start the exe; if Smart App Control blocks it, fall back to running the framework-dependent build
# through the signed dotnet host (tests the app logic, not the distributable file).
function Start-PerfSnap {
    $p = $null
    try { $p = Start-Process -FilePath $exeFull -PassThru -ErrorAction Stop } catch { Out-Result "F. exe could not start: $($_.Exception.Message)" }
    Start-Sleep -Seconds 20
    if ($p) { $p.Refresh() }
    if ($p -and -not $p.HasExited) { return $p }
    if ($HostDll) {
        Out-Result "F. falling back to: dotnet $HostDll"
        $p = Start-Process -FilePath (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe') -ArgumentList "`"$HostDll`"" -PassThru
        Start-Sleep -Seconds 20
        $p.Refresh()
    }
    return $p
}
$proc = Start-PerfSnap
$alive = $proc -and -not $proc.HasExited
Out-Result "F. app started: running=$alive pid=$(if ($proc) { $proc.Id })"
if ($alive) {
    $cpu0 = $proc.TotalProcessorTime; $t0 = Get-Date
    Start-Sleep -Seconds $IdleSeconds
    $proc.Refresh()
    $cpuPct = ($proc.TotalProcessorTime - $cpu0).TotalMilliseconds / ((Get-Date) - $t0).TotalMilliseconds / [Environment]::ProcessorCount * 100
    $pws = (Get-CimInstance Win32_PerfRawData_PerfProc_Process -Filter "IDProcess=$($proc.Id)").WorkingSetPrivate
    Out-Result ("F. idle {0}s: PerfSnap CPU={1:N3} % (of all cores)  workingSet={2:N1} MB  privateWorkingSet(TaskManager)={5:N1} MB  privateBytes={3:N1} MB  threads={4}" -f $IdleSeconds, $cpuPct, ($proc.WorkingSet64 / 1MB), ($proc.PrivateMemorySize64 / 1MB), $proc.Threads.Count, ($pws / 1MB))

    # Hotkey save (Ctrl+Alt+Shift+P via SendInput)
    Add-Type -AssemblyName System.Windows.Forms
    [System.Windows.Forms.SendKeys]::SendWait('^%+p')
    $deadline = (Get-Date).AddSeconds(240); $newDir = $null
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 5
        $now = @(Get-ChildItem $captures -Directory -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Name -and $_.Name -ne 'counters' })
        foreach ($d in $now) {
            $metaPath = Join-Path $d.FullName 'meta.json'
            if (Test-Path $metaPath) {
                $meta = Get-Content $metaPath -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($meta.result -ne 'saving') { $newDir = $d; break }
            }
        }
        if ($newDir) { break }
    }
    if ($newDir) {
        $meta = Get-Content (Join-Path $newDir.FullName 'meta.json') -Raw -Encoding UTF8 | ConvertFrom-Json
        $files = (Get-ChildItem $newDir.FullName | ForEach-Object { '{0}({1:N1}MB)' -f $_.Name, ($_.Length / 1MB) }) -join ' '
        Out-Result "F. hotkey save: folder=$($newDir.Name) result=$($meta.result) durationMs=$($meta.durationMs) files=$files"
    } else { Out-Result 'F. hotkey save: NO capture folder within 240s' }

    $st = Invoke-Wpr @('-status') 30
    Out-Result "F. wpr status after save (should be recording again): code=$($st.Code) $($st.Text.Substring(0, [Math]::Min(300, $st.Text.Length)))"
    $lq = Invoke-Tool $logman @('query', 'PerfSnap_Light')
    Out-Result "F. logman query PerfSnap_Light: code=$($lq.Code) $($lq.Text.Substring(0, [Math]::Min(300, $lq.Text.Length)))"

    # Crash recovery: kill, then start again and check the log for the cleanup
    Stop-Process -Id $proc.Id -Force
    Start-Sleep -Seconds 3
    $st2 = Invoke-Wpr @('-status') 30
    Out-Result "F. after kill, wpr status (still recording is expected): code=$($st2.Code)"
    $proc2 = Start-PerfSnap
    Start-Sleep -Seconds 5
    $alive2 = $proc2 -and -not $proc2.HasExited
    Out-Result "F. restart after kill: running=$alive2"
    if ($proc2) { Stop-Process -Id $proc2.Id -Force -ErrorAction SilentlyContinue }
    Start-Sleep -Seconds 3
}

}
# ---------------- G. memory held by the flight recorder, stop options ----------------
if ($run -contains 'G') {
    Invoke-Wpr @('-cancel') 30 | Out-Null
    Start-Sleep -Seconds 5
    $m0 = [SysTimes]::Memory()
    Out-Result "G. before monitoring: $(Format-Mem $m0)"
    foreach ($opt in @(@('-skipPdbGen'), @('-skipPdbGen', '-compress'))) {
        $r = Invoke-Wpr (StartArgs $profiles['input-latency']) 60
        if ($r.Code -ne 0) { Out-Result "G. start failed: $($r.Text)"; continue }
        Start-Sleep -Seconds 10
        $m1 = [SysTimes]::Memory()
        Start-Sleep -Seconds 110
        $m2 = [SysTimes]::Memory()
        $tag = ($opt -join '') -replace '-', ''
        $etl = Join-Path $OutDir "G_$tag.etl"
        $s = Invoke-Wpr (@('-stop', $etl, 'PerfSnap verify') + $opt) 300
        $size = if (Test-Path $etl) { [math]::Round((Get-Item $etl).Length / 1MB, 1) } else { 0 }
        Out-Result "G. monitoring 10s: $(Format-Mem $m1)"
        Out-Result "G. monitoring 120s: $(Format-Mem $m2)"
        Out-Result "G. stop $($opt -join ' '): code=$($s.Code) ms=$($s.Ms) sizeMB=$size"
        Start-Sleep -Seconds 5
    }
}
# ---------------- H. bundled profile with bounded buffers ----------------
if ($run -contains 'H') {
    Invoke-Wpr @('-cancel') 30 | Out-Null
    $template = Get-Content $WprpTemplate -Raw -Encoding UTF8
    foreach ($mb in ($BufferSizes.Split(',') | ForEach-Object { [int]$_ })) {
        $sysBuf = [int][math]::Round($mb * 0.75); $evtBuf = $mb - $sysBuf
        $wprp = Join-Path $OutDir "PerfSnap_$mb.wprp"
        [IO.File]::WriteAllText($wprp, $template.Replace('{SYSTEM_BUFFERS}', "$sysBuf").Replace('{EVENT_BUFFERS}', "$evtBuf"), (New-Object Text.UTF8Encoding($false)))
        foreach ($prof in @('PerfSnap_InputLatency', 'PerfSnap_Detailed')) {
            Start-Sleep -Seconds 5
            $m0 = [SysTimes]::Memory()
            $r = Invoke-Wpr @('-start', "$wprp!$prof") 60
            Out-Result "H. $prof ${mb}MB start: code=$($r.Code) $($r.Text)"
            if ($r.Code -ne 0) { Invoke-Wpr @('-cancel') 30 | Out-Null; continue }
            Start-Sleep -Seconds 10
            $m1 = [SysTimes]::Memory()
            $c0 = [SysTimes]::Now(); Start-Sleep -Seconds ($RetentionSeconds - 10); $c1 = [SysTimes]::Now()
            $stopAt = Get-Date
            $etl = Join-Path $OutDir "H_${prof}_$mb.etl"
            $s = Invoke-Wpr @('-stop', $etl, 'PerfSnap verify', '-skipPdbGen', '-compress') 300
            $first = Get-FirstEventTime $etl
            $kept = if ($first -is [datetime]) { [math]::Round(($stopAt - $first).TotalSeconds, 0) } else { "?" }
            $size = if (Test-Path $etl) { [math]::Round((Get-Item $etl).Length / 1MB, 1) } else { 0 }
            Out-Result ("H. {0} {1}MB: nonpaged +{2:N0} MB, available -{3:N0} MB, system CPU {4:N2} %, stop code={5} ms={6} sizeMB={7} keptSeconds={8} of {9}" -f `
                $prof, $mb, ($m1[1] - $m0[1]), ($m0[0] - $m1[0]), [SysTimes]::Busy($c0, $c1), $s.Code, $s.Ms, $size, $kept, $RetentionSeconds)
        }
    }
}

# Final cleanup so nothing keeps recording
Invoke-Wpr @('-cancel') 60 | Out-Null
Invoke-Tool $logman @('stop', 'PerfSnap_Light') | Out-Null
Invoke-Tool $logman @('delete', 'PerfSnap_Light') | Out-Null
$logPath = Join-Path $captures 'perfsnap.log'
if (Test-Path $logPath) { Copy-Item $logPath (Join-Path $OutDir 'perfsnap.log') }
Out-Result 'DONE (wpr cancelled, PerfSnap_Light counter collector stopped and removed)'
