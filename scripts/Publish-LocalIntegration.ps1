[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [string]$InstallRoot = (Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TaskbarQuota-Integration'),
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'

function Get-NormalizedPath {
    param([Parameter(Mandatory = $true)][string]$Path)

    return [System.IO.Path]::GetFullPath($Path).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
}

function Assert-SafeInstallRoot {
    param([Parameter(Mandatory = $true)][string]$Path)

    $normalized = Get-NormalizedPath $Path
    $pathRoot = [System.IO.Path]::GetPathRoot($normalized).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
    $userProfile = Get-NormalizedPath ([Environment]::GetFolderPath('UserProfile'))
    $localAppData = Get-NormalizedPath ([Environment]::GetFolderPath('LocalApplicationData'))

    if ($normalized -eq $pathRoot -or $normalized -eq $userProfile -or $normalized -eq $localAppData) {
        throw "InstallRoot is too broad for safe replacement: $normalized"
    }

    return $normalized
}

function Assert-DirectChild {
    param(
        [Parameter(Mandatory = $true)][string]$Parent,
        [Parameter(Mandatory = $true)][string]$Child
    )

    $normalizedParent = Get-NormalizedPath $Parent
    $normalizedChild = Get-NormalizedPath $Child
    $expectedPrefix = $normalizedParent + [System.IO.Path]::DirectorySeparatorChar
    if (-not $normalizedChild.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside InstallRoot: $normalizedChild"
    }
}

$repoRoot = Get-NormalizedPath (Join-Path $PSScriptRoot '..')
$projectPath = Join-Path $repoRoot 'src\TaskbarQuota.App\TaskbarQuota.App.csproj'
$installRootPath = Assert-SafeInstallRoot $InstallRoot
$currentPath = Join-Path $installRootPath 'current'
$previousPath = Join-Path $installRootPath 'previous'
$stagingPath = Join-Path $installRootPath ('.staging-' + [Guid]::NewGuid().ToString('N'))

Assert-DirectChild $installRootPath $currentPath
Assert-DirectChild $installRootPath $previousPath
Assert-DirectChild $installRootPath $stagingPath

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet) {
    throw 'dotnet was not found in PATH.'
}

$branch = (& git -C $repoRoot branch --show-current).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Could not read the current Git branch.'
}
if ($branch -ne 'codex/integration-all-features') {
    throw "Run this script from codex/integration-all-features, not $branch."
}

$status = @(& git -C $repoRoot status --porcelain --untracked-files=normal)
if ($LASTEXITCODE -ne 0) {
    throw 'Could not read the Git working tree status.'
}
if ($status.Count -gt 0) {
    throw 'The integration working tree must be clean before publishing.'
}

$commit = (& git -C $repoRoot rev-parse --short=12 HEAD).Trim()
if ($LASTEXITCODE -ne 0) {
    throw 'Could not resolve the integration commit.'
}

$running = @(Get-Process -Name 'TaskbarQuota' -ErrorAction SilentlyContinue)
if ($running.Count -gt 0) {
    $runningPaths = $running |
        ForEach-Object {
            try { $_.Path } catch { "PID $($_.Id)" }
        }
    throw "Quit every running TaskbarQuota instance before publishing:`n$($runningPaths -join [Environment]::NewLine)"
}

$operation = "Publish commit $commit and replace the local integration build"
if (-not $PSCmdlet.ShouldProcess($installRootPath, $operation)) {
    return
}

$stagingPromoted = $false
try {
    New-Item -ItemType Directory -Path $stagingPath -Force | Out-Null

    & $dotnet.Source publish $projectPath `
        -c Release `
        -p:Platform=x64 `
        -p:WindowsPackageType=None `
        -r win-x64 `
        --self-contained true `
        -o $stagingPath
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE."
    }

    $buildRoot = Join-Path $repoRoot 'src\TaskbarQuota.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64'
    $xbfFiles = @(Get-ChildItem -LiteralPath $buildRoot -Filter '*.xbf' -Recurse -File)
    if ($xbfFiles.Count -eq 0) {
        throw "No XBF resources were produced under $buildRoot."
    }

    foreach ($xbf in $xbfFiles) {
        $relativePath = $xbf.FullName.Substring($buildRoot.Length).TrimStart('\', '/')
        $destination = Join-Path $stagingPath $relativePath
        $destinationDirectory = Split-Path -Parent $destination
        if (-not (Test-Path -LiteralPath $destinationDirectory)) {
            New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
        }
        Copy-Item -LiteralPath $xbf.FullName -Destination $destination -Force
    }

    $requiredFiles = @(
        'TaskbarQuota.exe',
        'TaskbarQuota.pri',
        'MainWindow.xbf',
        'FlyoutWindow.xbf',
        'Views\DashboardPage.xbf',
        'Controls\WidgetSummary.xbf'
    )
    foreach ($relativePath in $requiredFiles) {
        $requiredPath = Join-Path $stagingPath $relativePath
        if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
            throw "Publish output is missing $relativePath."
        }
    }

    $buildInfo = @(
        "Branch: $branch"
        "Commit: $commit"
        "PublishedUtc: $([DateTime]::UtcNow.ToString('O'))"
    )
    Set-Content -LiteralPath (Join-Path $stagingPath 'integration-build.txt') -Value $buildInfo -Encoding UTF8

    if (Test-Path -LiteralPath $previousPath) {
        Remove-Item -LiteralPath $previousPath -Recurse -Force
    }

    $movedCurrent = $false
    if (Test-Path -LiteralPath $currentPath) {
        Move-Item -LiteralPath $currentPath -Destination $previousPath
        $movedCurrent = $true
    }

    try {
        Move-Item -LiteralPath $stagingPath -Destination $currentPath
        $stagingPromoted = $true
    }
    catch {
        if ($movedCurrent -and -not (Test-Path -LiteralPath $currentPath)) {
            Move-Item -LiteralPath $previousPath -Destination $currentPath
        }
        throw
    }

    $executable = Join-Path $currentPath 'TaskbarQuota.exe'
    Write-Host "Published integration commit $commit to $currentPath"
    if (Test-Path -LiteralPath $previousPath) {
        Write-Host "Previous build retained at $previousPath"
    }

    if (-not $NoLaunch) {
        Start-Process -FilePath $executable -WorkingDirectory $currentPath
        Write-Host "Started $executable"
    }
}
finally {
    if (-not $stagingPromoted -and (Test-Path -LiteralPath $stagingPath)) {
        Remove-Item -LiteralPath $stagingPath -Recurse -Force
    }
}
