$ErrorActionPreference = 'Stop'
function Write-Log([string]$message) {
    Add-Content -LiteralPath $config.log -Value "$(Get-Date -Format o) $message" -Encoding UTF8
}
function Get-MatchingProcess($identity) {
    $candidate = Get-Process -Id $identity.Id -ErrorAction SilentlyContinue
    if ($null -eq $candidate) { return $null }
    # A PID can be reused while the helper is waiting. Never terminate a new
    # instance, or a different executable that happens to have the same name.
    $matchesIdentity = $false
    try {
        $matchesIdentity = $candidate.StartTime.ToUniversalTime().Ticks -eq $identity.StartTimeUtcTicks -and
            [string]::Equals($candidate.MainModule.FileName, $identity.Path, [StringComparison]::OrdinalIgnoreCase)
        if (-not $matchesIdentity) { return $null }
    } catch {
        if ($candidate.HasExited) { return $null }
        throw
    } finally {
        if (-not $matchesIdentity) { $candidate.Dispose() }
    }
    return $candidate
}
$mutex = New-Object Threading.Mutex($false, $config.mutexName)
$ownsMutex = $false
$accepted = $false
try {
    try { $ownsMutex = $mutex.WaitOne(0) }
    catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another update helper is already running.' }
    if (-not (Test-Path -LiteralPath $config.installer)) { throw 'Installer package is missing.' }
    Write-Log "Update helper ready; waiting up to $($config.shutdownTimeoutMilliseconds) ms for application shutdown."
    $ready = [Threading.EventWaitHandle]::OpenExisting($config.readyEvent)
    try { [void]$ready.Set() } finally { $ready.Dispose() }
    $accepted = $true
    $timer = [Diagnostics.Stopwatch]::StartNew()
    do {
        $remaining = @()
        foreach ($identity in $config.processes) {
            $candidate = Get-MatchingProcess $identity
            if ($null -ne $candidate) {
                $remaining += $identity
                $candidate.Dispose()
            }
        }
        if ($remaining.Count -eq 0) { break }
        Start-Sleep -Milliseconds 100
    } while ($timer.ElapsedMilliseconds -lt $config.shutdownTimeoutMilliseconds)
    foreach ($identity in $remaining) {
        $candidate = Get-MatchingProcess $identity
        if ($null -eq $candidate) { continue }
        try {
            Write-Log "Shutdown timed out; terminating verified application process $($identity.Id)."
            # Kill the verified Process object, never a newly looked-up PID or an
            # entire process tree (the independent helper is a child of the app).
            try { $candidate.Kill() }
            catch {
                # Exiting normally between verification and Kill is successful
                # shutdown too. Other termination failures must abort installation.
                if (-not $candidate.HasExited) { throw }
            }
            if (-not $candidate.WaitForExit(5000)) { throw 'Application process did not terminate.' }
        } finally { $candidate.Dispose() }
    }
    Write-Log 'Starting installer.'
    $setupLog = Join-Path ([IO.Path]::GetDirectoryName($config.log)) 'update-setup.log'
    $arguments = '/NORESTART /CLOSEAPPLICATIONS /NORESTARTAPPLICATIONS /LOG="' + $setupLog + '"'
    $setup = Start-Process -FilePath $config.installer -ArgumentList $arguments -WindowStyle Normal `
        -WorkingDirectory ([IO.Path]::GetDirectoryName($config.installer)) -PassThru
    # Start-Process -Wait also waits for descendants, including the app launched
    # by the installer's Finish page. Wait only for the installer itself.
    try {
        $setup.WaitForExit()
        $exitCode = $setup.ExitCode
    } finally { $setup.Dispose() }
    Write-Log "Installer exit code: $exitCode. Detailed log: $setupLog"
    if ($exitCode -ne 0) { throw "Installer exited with code $exitCode. See $setupLog" }
    if (-not (Test-Path -LiteralPath $config.targetExe)) { throw 'Updated application executable was not found.' }
    Start-Process -FilePath $config.targetExe -ArgumentList '--show-settings' -WindowStyle Hidden `
        -WorkingDirectory ([IO.Path]::GetDirectoryName($config.targetExe))
    Write-Log 'Updated application restarted.'
    foreach ($path in @($config.installer, $config.partial, $config.metadata)) {
        Remove-Item -LiteralPath $path -Force -ErrorAction SilentlyContinue
    }
    Write-Log 'Successful update package and download state removed.'
} catch {
    Write-Log "Update failed: $($_.Exception.Message) $($_.InvocationInfo.PositionMessage)"
    # A rejected second helper must not restart the app during the first install.
    if ($accepted -and (Test-Path -LiteralPath $config.fallbackExe)) {
        Start-Process -FilePath $config.fallbackExe -ArgumentList '--show-settings' -WindowStyle Hidden `
            -WorkingDirectory ([IO.Path]::GetDirectoryName($config.fallbackExe))
        Write-Log 'Previous application restarted as fallback; update package retained.'
    }
    exit 1
} finally {
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
