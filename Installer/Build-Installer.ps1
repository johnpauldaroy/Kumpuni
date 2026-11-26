[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$')]
    [string]$Version,

    [string]$UpdateFeed = '',

    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'releases'),

    [ValidatePattern('^[A-Fa-f0-9]{40}$')]
    [string]$SigningCertificateThumbprint
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$output = [IO.Path]::GetFullPath($OutputDirectory)
$publish = Join-Path ([IO.Path]::GetTempPath()) ('Kumpuni-publish-' + [Guid]::NewGuid().ToString('N'))

if ($UpdateFeed) {
    $uri = $null
    if (![Uri]::TryCreate($UpdateFeed, [UriKind]::Absolute, [ref]$uri) -or
        $uri.Scheme -ne 'https' -or !$uri.Host -or $uri.UserInfo -or $uri.Query -or $uri.Fragment) {
        throw 'UpdateFeed must be a public HTTPS directory or GitHub repository URL without credentials, queries, or fragments.'
    }
    if ($uri.IdnHost -eq 'github.com' -and
        (!$uri.IsDefaultPort -or $uri.AbsolutePath.Trim('/').Split('/').Count -ne 2)) {
        throw 'For GitHub Releases use the public repository URL ending in owner/repository.'
    }
    $UpdateFeed = $uri.AbsoluteUri
}

Push-Location -LiteralPath $project
try {
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw "dotnet tool restore exited with code $LASTEXITCODE." }

    $build = @(
        'publish', 'Kumpuni.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true', '-o', $publish,
        "-p:Version=$Version", "-p:KumpuniUpdateFeed=$UpdateFeed", '-p:PublishSingleFile=true',
        '-p:EnableSingleFileAnalyzer=false', '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None'
    )
    & dotnet @build
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish exited with code $LASTEXITCODE." }

    $arguments = @(
        'pack', '--packId', 'Kumpuni.Desktop', '--packTitle', 'Kumpuni', '--packAuthors', 'Innovatech Hub', '--packVersion', $Version,
        '--channel', 'win-x64', '--runtime', 'win-x64', '--framework', 'webview2',
        '--packDir', $publish, '--outputDir', $output, '--mainExe', 'Kumpuni.exe',
        '--icon', (Join-Path $project 'assets\kumpuni.ico'), '--noPortable'
    )
    if ($SigningCertificateThumbprint) {
        $arguments += @('--signParams', "/sha1 $SigningCertificateThumbprint /fd sha256 /tr http://timestamp.digicert.com /td sha256")
    }

    & dotnet tool run vpk -- @arguments
    if ($LASTEXITCODE -ne 0) { throw "vpk pack exited with code $LASTEXITCODE." }
    "Installer and update feed: $output"
}
finally {
    Pop-Location
}
