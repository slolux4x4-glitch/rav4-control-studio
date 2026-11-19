<#
.SYNOPSIS
    Guided first test of RAV4 Control Studio with the real adapter and car.

.DESCRIPTION
    Walks through FIRST-CAR-TEST.md one step at a time. For each step, answer:
        p = passed    f = failed    s = skip    q = stop here
    On a failure it shows what to try and asks what you saw. It can check the Wi-Fi adapter's network
    connection itself. At the end it saves the answers to
    Documents\RAV4 Control Studio\Logs\car-test-<date>-<time>.txt and lists the files worth sending back.

    This script never talks to the car. Only the app does, and the app is read-only.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File tools\FirstCarTest.ps1
#>
param(
    [string]$AdapterHost = "192.168.0.10",
    [int]$AdapterPort = 35000
)

$ErrorActionPreference = "Stop"
$dataFolder = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "RAV4 Control Studio"
$logsFolder = Join-Path $dataFolder "Logs"
$started = Get-Date
$results = New-Object System.Collections.Generic.List[object]

function Write-Title([string]$text) {
    Write-Host ""
    Write-Host ("=" * 70) -ForegroundColor DarkCyan
    Write-Host " $text" -ForegroundColor Cyan
    Write-Host ("=" * 70) -ForegroundColor DarkCyan
}

function Ask([string]$prompt, [string[]]$allowed) {
    while ($true) {
        $answer = (Read-Host $prompt).Trim().ToLowerInvariant()
        if ($allowed -contains $answer) { return $answer }
        Write-Host "  Please type one of: $($allowed -join ', ')" -ForegroundColor DarkYellow
    }
}

# Can this PC reach the Wi-Fi adapter? Only opens and closes a TCP connection; sends nothing.
function Test-Adapter {
    $client = New-Object System.Net.Sockets.TcpClient
    try {
        $task = $client.ConnectAsync($AdapterHost, $AdapterPort)
        if ($task.Wait(3000) -and $client.Connected) { return $true }
        return $false
    }
    catch { return $false }
    finally { $client.Dispose() }
}

# Each step: section, number, what to do, what "passed" looks like, and what to try if it fails.
$steps = @(
    @{ Section = "Before you go to the car"; Id = "1"; Do = "Open RAV4 Control Studio. On OBD2 Connection choose Demo, then press Test adapter."
       Pass = "Every line of the test passes."
       Fix = @("Close and reopen the app, then try again.", "If it still fails, the problem is in the app: stop here and send About > Copy details.") }

    @{ Section = "In the car (engine off, ignition on)"; Id = "2"; Do = "Plug the adapter into the OBD-II port under the dashboard (driver's side). Turn the ignition ON but do not start the engine."
       Pass = "The adapter's light comes on."
       Fix = @("Push the adapter fully into the port.", "Check the ignition is on (dashboard lights lit).") }

    @{ Section = "In the car (engine off, ignition on)"; Id = "3"; Do = "In Windows, join the adapter's Wi-Fi network (for example WiFi_OBDII). Windows may say 'No internet'; that is normal."
       Pass = "Windows shows it is connected to the adapter's network."
       Fix = @("Wait 10 seconds after plugging in; the network can take a moment to appear.", "Forget the network in Windows and join it again.", "Turn off any VPN.")
       Check = "wifi" }

    @{ Section = "In the car (engine off, ignition on)"; Id = "4"; Do = "In the app: OBD2 Connection > Wi-Fi, address $AdapterHost, port $AdapterPort (or press Find adapter). Leave the protocol on ISO 15765-4 CAN 11-bit, 500 kbaud. Press Test adapter."
       Pass = "Every line passes, and the voltage is about 12 V."
       Fix = @("Open link fails: the PC is not on the adapter's network, or the address/port is wrong. Try Find adapter.",
               "Voltage low: ignition off, or the adapter is not fully seated.",
               "ECU answers fails: check the ignition is on, then try protocol Automatic.",
               "Press Save session log before changing anything else.") }

    @{ Section = "In the car (engine off, ignition on)"; Id = "5"; Do = "Press Connect ECU, then look at Overview and ECU Information."
       Pass = "The badge says Vehicle Mode. The VIN appears on ECU Information. Coolant shows a sensible temperature and battery about 12 V."
       Fix = @("Press Save session log straight away.", "Readings showing '--' or odd fault codes: note them; the session log shows what the car replied.") }

    @{ Section = "In the car (engine off, ignition on)"; Id = "6"; Do = "Open Diagnostics (DTCs) and press Read codes."
       Pass = "The codes match the dashboard lights or a trusted scan tool (often: No DTCs Found)."
       Fix = @("Write down the codes the app shows and what the other tool shows.", "Press Save session log.") }

    @{ Section = "Engine running"; Id = "7"; Do = "Start the engine. Open Metrics."
       Pass = "Engine speed about 600 to 900 rpm at idle, speed 0, coolant slowly rising, battery about 13.5 to 14.5 V."
       Fix = @("Battery stays near 12 V with the engine running: the charging system may not be charging; have it checked.", "Press Save session log.") }

    @{ Section = "Engine running"; Id = "8"; Do = "Open Data Logging, press Start Logging, wait a minute, press Stop Logging, then open the file."
       Pass = "The CSV opens and has rows with values."
       Fix = @("Check Documents\RAV4 Control Studio\Logs for the newest log-*.csv file.") }

    @{ Section = "Engine running"; Id = "9"; Do = "Open Reports and press Create text report."
       Pass = "A report appears under Saved reports."
       Fix = @("Check Documents\RAV4 Control Studio\Reports.") }

    @{ Section = "Finish"; Id = "10"; Do = "On OBD2 Connection press Save session log (even if everything passed), then Disconnect."
       Pass = "A session-*.txt file is shown in File Explorer."
       Fix = @("Check Documents\RAV4 Control Studio\Logs.") }
)

Clear-Host
Write-Title "RAV4 Control Studio - first car test"
Write-Host " Answer each step:  p = passed   f = failed   s = skip   q = stop here"
Write-Host " Safety: do not drive while using the laptop. Let a passenger run it."
Write-Host " This script never talks to the car; the app does, and it is read-only."
$adapterName = Read-Host "`n Adapter brand or model (optional, press Enter to skip)"

$section = ""
foreach ($step in $steps) {
    if ($step.Section -ne $section) { $section = $step.Section; Write-Title $section }

    Write-Host ""
    Write-Host " Step $($step.Id). $($step.Do)" -ForegroundColor White
    Write-Host "   Passed means: $($step.Pass)" -ForegroundColor Gray

    if ($step.Check -eq "wifi") {
        Write-Host "   Checking the adapter at ${AdapterHost}:${AdapterPort} ..." -NoNewline
        if (Test-Adapter) { Write-Host " reachable." -ForegroundColor Green }
        else { Write-Host " not reachable yet." -ForegroundColor Yellow }
        Write-Host "   (Do this check before pressing Connect ECU in the app: the adapter usually accepts only one connection.)" -ForegroundColor DarkGray
    }

    $answer = Ask "   Result [p/f/s/q]" @("p", "f", "s", "q")
    if ($answer -eq "q") { $results.Add([pscustomobject]@{ Step = $step.Id; Result = "stopped"; Note = "" }); break }

    $note = ""
    if ($answer -eq "f") {
        Write-Host "   Things to try:" -ForegroundColor Yellow
        foreach ($fix in $step.Fix) { Write-Host "     - $fix" -ForegroundColor Yellow }
        $note = Read-Host "   What did you see? (a few words)"
        $retry = Ask "   Try the step again? [y/n]" @("y", "n")
        if ($retry -eq "y") {
            $again = Ask "   Result after trying again [p/f]" @("p", "f")
            if ($again -eq "p") { $answer = "p"; $note = "passed on retry; first time: $note" }
        }
    }

    $label = @{ p = "passed"; f = "FAILED"; s = "skipped" }[$answer]
    $results.Add([pscustomobject]@{ Step = $step.Id; Result = $label; Note = $note })
    if ($answer -eq "f") {
        $go = Ask "   Carry on with the next step anyway? [y/n]" @("y", "n")
        if ($go -eq "n") { break }
    }
}

# ----- Save the answers
New-Item -ItemType Directory -Force -Path $logsFolder | Out-Null
$file = Join-Path $logsFolder ("car-test-{0:yyyyMMdd-HHmmss}.txt" -f $started)
$lines = @(
    "RAV4 Control Studio - first car test",
    "Started:  $($started.ToString('yyyy-MM-dd HH:mm'))",
    "Adapter:  $(if ($adapterName) { $adapterName } else { '(not given)' }), Wi-Fi ${AdapterHost}:${AdapterPort}",
    ""
)
foreach ($r in $results) {
    $text = $steps | Where-Object { $_.Id -eq $r.Step } | Select-Object -First 1
    $lines += ("Step {0,-3} {1,-8} {2}" -f $r.Step, $r.Result, $text.Do)
    if ($r.Note) { $lines += "             note: $($r.Note)" }
}
$lines | Set-Content -Path $file -Encoding UTF8

Write-Title "Summary"
foreach ($r in $results) {
    $colour = switch ($r.Result) { "passed" { "Green" } "FAILED" { "Red" } default { "Yellow" } }
    Write-Host (" Step {0,-3} {1}" -f $r.Step, $r.Result) -ForegroundColor $colour -NoNewline
    if ($r.Note) { Write-Host "  ($($r.Note))" } else { Write-Host "" }
}
Write-Host ""
Write-Host " Answers saved to: $file" -ForegroundColor Cyan

# ----- Files worth sending back (anything made since the test started)
$recent = @()
if (Test-Path $logsFolder) {
    $recent = Get-ChildItem $logsFolder -File | Where-Object { $_.LastWriteTime -ge $started } | Sort-Object LastWriteTime
}
Write-Host ""
Write-Host " Files from this test to send back:" -ForegroundColor Cyan
foreach ($f in $recent) { Write-Host "   $($f.FullName)" }
if (-not ($recent | Where-Object Name -like "session-*")) {
    Write-Host "   (no session log yet: press Save session log on the OBD2 Connection page before closing the app)" -ForegroundColor Yellow
}
Write-Host ""
Read-Host " Press Enter to close" | Out-Null
