param(
    [Parameter(Mandatory)][string]$Executable,
    [Parameter(Mandatory)][string]$DeviceName,
    [ValidateRange(1,600)][int]$BackgroundSeconds = 66
)

$ErrorActionPreference = 'Stop'
if (Get-Process BatteryDesk -ErrorAction SilentlyContinue) {
    throw 'Exit BatteryDesk before running the verification.'
}
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes
$exe = (Resolve-Path $Executable).Path
$devicePattern = [regex]::Escape($DeviceName) + '.*\d+%'
$clock = [Diagnostics.Stopwatch]::StartNew()
$app = Start-Process -WindowStyle Hidden -FilePath $exe -ArgumentList '--show' -PassThru
try {
    $window = $null
    $openedAt = $null
    $batteryAt = $null
    $rowName = $null
    $refreshButton = $null
    $scope = [Windows.Automation.TreeScope]::Descendants
    $root = [Windows.Automation.AutomationElement]::RootElement
    $filter = [Windows.Automation.AndCondition]::new(
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::ProcessIdProperty, [int]$app.Id),
        [Windows.Automation.PropertyCondition]::new([Windows.Automation.AutomationElement]::NameProperty, 'BatteryDesk'))
    do {
        Start-Sleep -Milliseconds 100
        $app.Refresh()
        if ($app.HasExited) { throw "App exited with code $($app.ExitCode)" }
        $window = $root.FindFirst([Windows.Automation.TreeScope]::Children, $filter)
        if ($null -eq $window) { continue }
        if ($null -eq $openedAt) { $openedAt = $clock.ElapsedMilliseconds }
        try {
            foreach ($element in $window.FindAll($scope, [Windows.Automation.Condition]::TrueCondition)) {
                $name = $element.Current.Name
                if ($name -match $devicePattern) { $rowName = $name }
                if ($name -eq 'Làm mới') { $refreshButton = $element }
            }
            if ($rowName -and $null -ne $refreshButton -and $refreshButton.Current.IsEnabled) {
                $batteryAt = $clock.ElapsedMilliseconds
                break
            }
        } catch [System.Management.Automation.MethodInvocationException], [System.Management.Automation.GetValueInvocationException] {
            # The app may have replaced its list while UI Automation was enumerating it.
            $refreshButton = $null
            $rowName = $null
        }
    } while ($clock.Elapsed.TotalSeconds -lt 18)
    if ($null -eq $batteryAt) { throw 'No completed hardware reading in the accessible window.' }
    Write-Output "Startup popup: ${openedAt} ms; confirmed tray row: ${batteryAt} ms"
    Write-Output $rowName
    $invoke = $refreshButton.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern)
    $invoke.Invoke()
    $manualAt = $clock.ElapsedMilliseconds
    do {
        Start-Sleep -Milliseconds 250
    } while (!$refreshButton.Current.IsEnabled -and $clock.ElapsedMilliseconds - $manualAt -lt 8000)
    $app.Refresh()
    if ($app.HasExited -or !$refreshButton.Current.IsEnabled) { throw 'Manual refresh failed to complete' }
    Write-Output 'Manual refresh completed.'
    $window.GetCurrentPattern([Windows.Automation.WindowPattern]::Pattern).Close()
    Start-Sleep -Milliseconds 300
    $app.Refresh()
    if ($app.HasExited -or $null -ne $root.FindFirst([Windows.Automation.TreeScope]::Children, $filter)) {
        throw 'Close did not leave app running in tray'
    }
    Write-Output 'Close leaves app running in tray.'
    $beforeCpu = $app.TotalProcessorTime.TotalMilliseconds
    $beforeTime = $clock.Elapsed.TotalSeconds
    Start-Sleep -Seconds $BackgroundSeconds
    $app.Refresh()
    $secs = $clock.Elapsed.TotalSeconds - $beforeTime
    $cpu = $app.TotalProcessorTime.TotalMilliseconds - $beforeCpu
    Write-Output ('Background, {0:N1} s: CPU {1:N1} ms, working set {2:N1} MiB, private bytes {3:N1} MiB' -f $secs, $cpu, ($app.WorkingSet64 / 1MB), ($app.PrivateMemorySize64 / 1MB))
    $second = Start-Process -WindowStyle Hidden -FilePath $exe -PassThru
    if (!$second.WaitForExit(5000)) { throw 'Second launch stayed resident' }
    $reopenStart = $clock.ElapsedMilliseconds
    do {
        $window = $root.FindFirst([Windows.Automation.TreeScope]::Children, $filter)
        if ($null -ne $window) { break }
        Start-Sleep -Milliseconds 20
    } while ($clock.ElapsedMilliseconds - $reopenStart -lt 3000)
    if ($null -eq $window) { throw 'Second launch did not reopen the first window' }
    $window.FindAll($scope,[Windows.Automation.Condition]::TrueCondition) |
        ForEach-Object { $_.Current.Name } | Where-Object { $_ -match $devicePattern }
    Write-Output 'Second launch reopened the original app.'
} finally {
    if (!$app.HasExited) { $app.Kill(); $app.WaitForExit() }
}
