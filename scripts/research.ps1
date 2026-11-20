#requires -Version 5.1
[CmdletBinding()]
param(
    [Parameter(Position=0,Mandatory)]
    [ValidateSet('start','status','stop','resume','logs','pilot','demo','sources','claims','review','approve','coverage','configure','migrate')]
    [string]$Action,
    [Parameter(Position=1)][ValidateSet('HA4')][string]$Target = 'HA4',
    [ValidateRange(0.016666,168)][double]$Hours = 8,
    [ValidateSet('codex','brave','crossref')][string]$SearchProvider = 'codex',
    [ValidateRange(0,10000)][int]$SearchLimit = 16,
    [ValidateRange(0,10000)][int]$PageLimit = 12,
    [ValidateRange(0,10000)][int]$AiLimit = 6,
    [string]$Scope, [string]$Publisher, [string]$Basis,
    [ValidateSet('html','text','markdown')][string]$Format = 'html',
    [ValidateRange(0,2147483647)][int]$Offset = 0,
    [ValidateSet('Brave','Database','Postgres')][string]$Setting = 'Brave',
    [switch]$Follow
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$state = Join-Path $root '.local\research\launcher'
$record = Join-Path $state 'ha4-worker.json'
$settings = Join-Path $state 'ha4-settings.json'
$campaign = 'ha4-live'
$binary = Join-Path $root 'KeiBuild.Research\bin\Release\net10.0\KeiBuild.Research.exe'

function Require-Command($name, $help) {
    $found = Get-Command $name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if (!$found) { throw $help }
    return $found.Source
}
function Invoke-Research {
    & $binary @args
    if ($LASTEXITCODE -ne 0) { throw "Research command '$($args[0])' failed. See the diagnostic above." }
}
function Get-LocalSecret($name) {
    $path = Join-Path $state "$name.dpapi"
    if (Test-Path -LiteralPath $path) {
        $secure = Get-Content -Raw -LiteralPath $path | ConvertTo-SecureString
        return [Net.NetworkCredential]::new('', $secure).Password
    }
}
function Save-LocalSecret($name, [Security.SecureString]$value) {
    if (!$value.Length) { throw 'Enter a nonempty value.' }
    $encrypted = ConvertFrom-SecureString $value
    [IO.File]::WriteAllText((Join-Path $state "$name.dpapi"), $encrypted)
}
function Connect-Database {
    if (!$env:KEIBUILD_RESEARCH_DB) { $env:KEIBUILD_RESEARCH_DB = Get-LocalSecret 'Database' }
    if ($env:KEIBUILD_RESEARCH_DB) { return }
    $docker = Require-Command docker.exe 'Install Docker Desktop or configure an external Database connection.'
    $container = @(& $docker ps --all --filter 'label=com.docker.compose.project=keibuild' --filter 'label=com.docker.compose.service=research-db' --format '{{.ID}}')
    if ($LASTEXITCODE -ne 0) { throw 'Start Docker Desktop and wait for the engine to become ready.' }
    if ($container.Count -gt 1) { throw 'Multiple Compose database containers matched. Configure the intended Database connection.' }
    if (!$env:KEIBUILD_POSTGRES_PASSWORD) { $env:KEIBUILD_POSTGRES_PASSWORD = Get-LocalSecret 'Postgres' }
    if ($container.Count -eq 1) {
        $running = & $docker inspect --format '{{.State.Running}}' $container[0]
        if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the research container.' }
        if ($running -ne 'true') {
            # Starting the existing container preserves its configuration and volume.
            & $docker start $container[0] | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Docker could not start the existing research container.' }
        }
        if (!$env:KEIBUILD_POSTGRES_PASSWORD) {
            # Read only the named credential of the existing local Compose service, directly into the child environment.
            $env:KEIBUILD_POSTGRES_PASSWORD = & $docker exec $container[0] printenv POSTGRES_PASSWORD
            if ($LASTEXITCODE -ne 0 -or !$env:KEIBUILD_POSTGRES_PASSWORD) { throw 'Run research.ps1 configure HA4 -Setting Postgres and enter the existing database password.' }
        }
    } else {
        if (!$env:KEIBUILD_POSTGRES_PASSWORD) {
            $volume = & $docker volume ls --filter 'name=keibuild_kei-research-data' --format '{{.Name}}'
            if ($LASTEXITCODE -ne 0) { throw 'Cannot inspect the existing research volume.' }
            if ($volume) {
                $secret = Read-Host 'Existing PostgreSQL volume password' -AsSecureString
            } else {
                $bytes = New-Object byte[] 32
                $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
                try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
                $secret = ConvertTo-SecureString ([Convert]::ToBase64String($bytes)) -AsPlainText -Force
            }
            Save-LocalSecret 'Postgres' $secret
            $env:KEIBUILD_POSTGRES_PASSWORD = [Net.NetworkCredential]::new('', $secret).Password
        }
        & $docker compose -p keibuild -f (Join-Path $root 'compose.yaml') up -d --wait research-db
        if ($LASTEXITCODE -ne 0) { throw 'Docker Compose could not start the research database.' }
    }
    $dbReady = $false
    for ($i=0; $i -lt 30; $i++) {
        & $docker compose -p keibuild -f (Join-Path $root 'compose.yaml') exec -T research-db pg_isready -q -U keibuild -d keibuild
        if ($LASTEXITCODE -eq 0) { $dbReady = $true; break }
        Start-Sleep -Seconds 2
    }
    if (!$dbReady) { throw 'PostgreSQL did not become ready within 60 seconds.' }
    $env:KEIBUILD_RESEARCH_DB = 'Host=127.0.0.1;Port=5444;Database=keibuild;Username=keibuild;Password="{0}"' -f $env:KEIBUILD_POSTGRES_PASSWORD.Replace('"','""')
}
function Test-Codex {
    $exe = $env:KEIBUILD_CODEX_EXE
    if (!$exe) {
        $shim = Get-Command codex.cmd -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($shim) {
            $pkg = Join-Path (Split-Path $shim.Source) 'node_modules\@openai\codex'
            $exe = Get-ChildItem -LiteralPath $pkg -Recurse -Filter codex.exe -File -ErrorAction SilentlyContinue |
                Where-Object { $_.FullName -match ('[\\/]' + $(if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') {'aarch64'} else {'x86_64'}) + '-pc-windows-msvc[\\/]') } |
                Select-Object -First 1 -ExpandProperty FullName
        }
        if (!$exe) { $exe = Require-Command codex.exe 'Install the native Codex CLI or set KEIBUILD_CODEX_EXE.' }
    }
    if (![IO.Path]::IsPathRooted($exe) -or [IO.Path]::GetExtension($exe) -ne '.exe' -or !(Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw 'KEIBUILD_CODEX_EXE must be an absolute path to the installed native codex.exe.'
    }
    $env:KEIBUILD_CODEX_EXE = $exe
    Invoke-Research codex-check
    Write-Host "Using native Codex at $exe"
}
function Get-Worker($metadata) {
    if (!$metadata) { return }
    $process = Get-Process -Id $metadata.pid -ErrorAction SilentlyContinue
    if ($process -and $process.Path -eq $binary -and $process.StartTime.ToUniversalTime().Ticks.ToString() -eq $metadata.startedTicks) { return $process }
}
$envNames = 'KEIBUILD_POSTGRES_PASSWORD','KEIBUILD_RESEARCH_DB','KEIBUILD_CODEX_EXE','BRAVE_SEARCH_API_KEY'
$saved = @{}
foreach ($name in $envNames) { $saved[$name] = [Environment]::GetEnvironmentVariable($name) }
$lock = $null
Push-Location $root
try {
    [IO.Directory]::CreateDirectory($state) | Out-Null
    if ($Action -in 'start','resume','pilot','demo','configure','approve','migrate') {
        try { $lock = [IO.File]::Open((Join-Path $state 'launch.lock'),'OpenOrCreate','ReadWrite','None') }
        catch [IO.IOException] { throw 'Another launcher is starting or updating this campaign. Check status.' }
    }
    if ($Action -eq 'configure') {
        if ($Setting -eq 'Brave') { Write-Host 'Use a Brave Search API subscription permitting persistent storage of results and snippets.' }
        Save-LocalSecret $Setting (Read-Host "Enter $Setting credential for local Windows-user storage" -AsSecureString)
        Write-Host "Saved $Setting for this Windows user."
        return
    }
    $metadata = if (Test-Path -LiteralPath $record) { Get-Content -Raw -LiteralPath $record | ConvertFrom-Json }
    $worker = Get-Worker $metadata
    if ($Action -eq 'logs') {
        if (!$metadata) { Write-Host 'No launcher session has been recorded.'; return }
        Write-Host "Final status and progress logs in $state"
        Get-Content -LiteralPath $metadata.progress -Tail 60 -Wait:$Follow
        return
    }
    if ($worker -and $Action -in 'start','resume','pilot') { Write-Host "Research is already running with PID $($worker.Id)."; return }
    if ($Action -eq 'status') {
        if ($worker) { Write-Host "Launcher worker running, PID $($worker.Id)." } else { Write-Host 'No launcher-managed worker is running.' }
    }
    Connect-Database
    $launch = $Action -in 'start','pilot','resume'
    if (!$worker -and ($launch -or $Action -in 'migrate','demo','approve' -or !(Test-Path -LiteralPath $binary))) {
        $dotnet = Require-Command dotnet.exe 'Install the .NET 10 SDK.'
        & $dotnet build (Join-Path $root 'KeiBuild.Research') -c Release --nologo -v quiet
        if ($LASTEXITCODE -ne 0) { throw 'Research build failed. Install the .NET 10 SDK and restore the project.' }
    }
    if ($launch -or $Action -in 'migrate','demo','approve') { Invoke-Research migrate }
    switch ($Action) {
        'migrate' { return }
        'demo' { Invoke-Research demo ha4-demo; return }
        'review' { Invoke-Research source-review $campaign --offset $Offset; return }
        'claims' { Invoke-Research claims $campaign --offset $Offset; return }
        'sources' { Invoke-Research sources $campaign --offset $Offset; return }
        'approve' {
            if (!$Scope -or !$Basis -or !$Publisher) { throw 'Supply -Scope https://publisher/path/ -Publisher name -Basis documented-permission.' }
            Invoke-Research approve-source $campaign --scope $Scope --publisher $Publisher --basis $Basis --format $Format
            return
        }
        'status' { Invoke-Research status $campaign; return }
        'coverage' { Invoke-Research coverage $campaign; return }
        'stop' { Invoke-Research stop $campaign; return }
    }
    Test-Codex
    if ($SearchProvider -eq 'brave' -and !$env:BRAVE_SEARCH_API_KEY) { $env:BRAVE_SEARCH_API_KEY = Get-LocalSecret 'Brave' }
    Invoke-Research start $campaign | Out-Null
    $limits = [ordered]@{ Hours=$Hours; SearchLimit=$SearchLimit; PageLimit=$PageLimit; AiLimit=$AiLimit }
    if ($Action -eq 'pilot') {
        $pilotCaps = @{ Hours=0.25; SearchLimit=4; PageLimit=3; AiLimit=2 }
        foreach ($key in @($limits.Keys)) {
            $limits[$key] = if ($PSBoundParameters.ContainsKey($key)) { [Math]::Min([double]$limits[$key], [double]$pilotCaps[$key]) } else { $pilotCaps[$key] }
        }
    } else {
        if (Test-Path -LiteralPath $settings) {
            $prior = Get-Content -Raw -LiteralPath $settings | ConvertFrom-Json
            foreach ($key in @($limits.Keys)) {
                if (!$PSBoundParameters.ContainsKey($key)) { $limits[$key] = $prior.$key }
            }
        }
        [IO.File]::WriteAllText($settings, ($limits | ConvertTo-Json))
    }
    $minutes = [int][Math]::Ceiling(60 * $limits.Hours)
    $stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
    $progress = Join-Path $state "$stamp.progress.log"
    $summary = Join-Path $state "$stamp.status.json"
    $budget = if ($SearchProvider -eq 'brave') { '0.20' } else { '0' }
    $workerArgs = @('resume',$campaign,'--search-provider',$SearchProvider,'--provider','codex','--continuous','--max-minutes',$minutes,'--search-limit',$limits.SearchLimit,'--page-limit',$limits.PageLimit,'--ai-limit',$limits.AiLimit,'--budget-usd',$budget)
    $process = Start-Process -FilePath $binary -ArgumentList $workerArgs -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardOutput $summary -RedirectStandardError $progress
    $info = @{ pid=$process.Id; startedTicks=$process.StartTime.ToUniversalTime().Ticks.ToString(); progress=$progress; summary=$summary }
    $temp = "$record.tmp"
    [IO.File]::WriteAllText($temp, ($info | ConvertTo-Json))
    if (Test-Path -LiteralPath $record) { [IO.File]::Replace($temp,$record,"$record.previous") } else { [IO.File]::Move($temp,$record) }
    Write-Host "Started $campaign with PID $($process.Id), up to $minutes minutes. Progress log $progress"
    # Wait through session initialization so an immediately following stop is observed.
    for ($i=0; $i -lt 100; $i++) {
        if ($process.HasExited) {
            Get-Content -LiteralPath $progress -Tail 5
            if ($process.ExitCode -ne 0) { throw 'The worker exited during startup. See the progress log.' }
            return
        }
        if (Select-String -LiteralPath $progress -Pattern '^Campaign .+, extraction' -Quiet) { return }
        Start-Sleep -Milliseconds 100
    }
    Write-Host 'The process is initializing. Use status and logs to check progress.'
} finally {
    if ($lock) { $lock.Dispose() }
    foreach ($name in $envNames) { [Environment]::SetEnvironmentVariable($name,$saved[$name]) }
    Pop-Location
}

