# Verifies that the published app can actually boot with no network.
#
# "Offline-first" is a claim about the shell as much as about the data. A local-first design is
# worthless if the browser cannot load the app at all when the connection is down, and the way that
# happens is quiet: a new file type is added, the service worker's cache list is a pattern list that
# does not cover it, and nothing fails until a shop's internet drops.
#
# This checks the contract the service worker depends on, against a real publish:
#
#   * the published worker is the offline one, and its asset manifest was generated;
#   * the cache patterns are read OUT OF the service worker rather than copied here, so editing the
#     worker changes what is verified;
#   * everything needed to boot -- the shell, the runtime, blazor.boot.json, every assembly, the
#     app's own CSS and JS, the manifest and its icons -- is covered by those patterns;
#   * whatever is NOT covered is named, and asserted to be source maps, which browsers fetch only
#     for devtools and never need offline.
#
# It does not prove the browser's service-worker runtime behaves; that needs a browser. What it
# proves is the part that silently regresses: that the set of files the app ships is still a subset
# of the set the worker caches.
#
# The other half is tools/verify-service-worker.mjs, which runs the worker's install, activate and
# fetch handlers against a synthetic manifest. This script checks the real published manifest against
# the worker's patterns; that one checks the worker's behaviour against representative data. The
# split is deliberate: one of them needs a publish and the other needs a VM, and merging them would
# make the slow one the only one anybody ran.
#
# Run from the repository root:  pwsh -NoProfile -File tools/verify-offline.ps1

$ErrorActionPreference = 'Stop'

$repo = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $repo '.publish/offline-check'
$wwwroot = Join-Path $publishDir 'wwwroot'

$step = 0
function Show([string]$label, [string]$detail) {
    $script:step++
    Write-Output ("{0,2}. {1,-36} {2}" -f $script:step, $label, $detail)
}

function Fail([string]$message) {
    throw "Offline check failed: $message"
}

Write-Output 'Publishing...'

if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

& dotnet publish (Join-Path $repo 'src/Pos.Web/Pos.Web.csproj') `
    -c Release -o $publishDir --nologo 2>&1 | Out-Null

if ($LASTEXITCODE -ne 0) { Fail 'publish failed.' }

if (-not (Test-Path $wwwroot)) { Fail "no wwwroot in the publish output at $wwwroot." }

Write-Output ''

# --- the worker itself ---------------------------------------------------------

$workerPath = Join-Path $wwwroot 'service-worker.js'
$publishedWorkerPath = Join-Path $repo 'src/Pos.Web/wwwroot/service-worker.published.js'

if (-not (Test-Path $workerPath)) { Fail 'the publish output has no service-worker.js.' }

$worker = Get-Content $workerPath -Raw

if ($worker -notmatch 'assetsManifest') {
    # The development worker does no caching at all. If this one is in the publish output, the app
    # ships with a service worker that installs and then caches nothing.
    Fail 'the published service-worker.js is the development one, so nothing is cached offline.'
}

Show 'published worker caches assets' 'offline worker is in place'

$assetsPath = Join-Path $wwwroot 'service-worker-assets.js'

if (-not (Test-Path $assetsPath)) {
    Fail 'service-worker-assets.js is missing, so the worker cannot install.'
}

$raw = Get-Content $assetsPath -Raw
$manifest = ($raw -replace '(?s)^.*?=\s*', '' -replace ';\s*$', '') | ConvertFrom-Json

Show 'asset manifest generated' "$($manifest.assets.Count) asset(s), version $($manifest.version)"

# --- the cache patterns, read from the worker ----------------------------------

# Deliberately parsed rather than restated. A copy here would drift from the worker, and the whole
# point of this check is to catch a worker whose patterns no longer cover what the app ships.
$source = Get-Content $publishedWorkerPath -Raw

$includeMatch = [regex]::Match($source, 'offlineAssetsInclude\s*=\s*\[(.*?)\]', 'Singleline')
$excludeMatch = [regex]::Match($source, 'offlineAssetsExclude\s*=\s*\[(.*?)\]', 'Singleline')

if (-not $includeMatch.Success) { Fail 'could not read offlineAssetsInclude out of the service worker.' }

function Get-Patterns([string]$block) {
    return [regex]::Matches($block, '/((?:[^/\\]|\\.)+)/') |
        ForEach-Object { $_.Groups[1].Value }
}

$include = Get-Patterns $includeMatch.Groups[1].Value
$exclude = if ($excludeMatch.Success) { Get-Patterns $excludeMatch.Groups[1].Value } else { @() }

if ($include.Count -eq 0) { Fail 'the service worker caches nothing: offlineAssetsInclude is empty.' }

Show 'cache patterns read from worker' "$($include.Count) include, $($exclude.Count) exclude"

function Test-Cached([string]$url) {
    foreach ($pattern in $exclude) {
        if ($url -match $pattern) { return $false }
    }

    foreach ($pattern in $include) {
        if ($url -match $pattern) { return $true }
    }

    return $false
}

# --- what has to be cached to boot --------------------------------------------

$cached = @($manifest.assets | Where-Object { Test-Cached $_.url })
$skipped = @($manifest.assets | Where-Object { -not (Test-Cached $_.url) })
$urls = @($cached | ForEach-Object { $_.url })

function Get-Missing([scriptblock]$Predicate, [string]$what) {
    $wanted = @($manifest.assets | Where-Object $Predicate)
    if ($wanted.Count -eq 0) { Fail "the published app has no $what, which cannot be right." }

    $absent = @($wanted | Where-Object { -not (Test-Cached $_.url) })
    if ($absent.Count -gt 0) {
        Fail "$($absent.Count) $what file(s) would be missing offline: $($absent[0].url)"
    }

    return $wanted.Count
}

# The shell. Without this a navigation offline has nothing to serve.
$shell = Get-Missing { $_.url -eq 'index.html' } 'application shell'
Show 'shell is cached' "index.html, plus $shell entry"

# The runtime. .NET 10 no longer publishes a blazor.boot.json: the loader and the runtime are
# several scripts, and the list of assemblies the app needs lives inside them. A missing one of these
# is a blank screen rather than a degraded one, so each is required by name. Every one carries a
# fingerprint, so the patterns match the bare name plus that fingerprint rather than the whole name.
$runtime = Get-Missing { $_.url -match 'blazor\.webassembly\.[a-z0-9]+\.js$' } 'Blazor boot script'
$loader = Get-Missing { $_.url -match '_framework/dotnet\.[a-z0-9]+\.js$' } 'runtime loader'
$native = Get-Missing { $_.url -match 'dotnet\.native\.[a-z0-9]+\.js$' } 'native runtime'
$managed = Get-Missing { $_.url -match 'dotnet\.runtime\.[a-z0-9]+\.js$' } 'managed runtime'
Show 'runtime is cached' "$loader loader, $native native, $managed runtime, $runtime boot"

# Every assembly, in whatever form the build emits them. Counted rather than named, because the
# names carry fingerprints and change every publish.
$core = Get-Missing { $_.url -match '_framework/.*\.(wasm|dll)$' } 'assembly'
Show 'every assembly is cached' "$core assembly file(s)"

# The app's own static files: the stylesheets, the JS interop modules, and the scoped styles the
# build generates. These are what a new feature adds, and therefore what is most likely to be
# forgotten in a pattern list.
$appCss = Get-Missing { $_.url -match '\.css$' } 'stylesheet'
$appJs = Get-Missing { $_.url -match '\.js$' } 'script'
Show 'app styles and scripts cached' "$appCss stylesheet(s), $appJs script(s)"

# ICU: currency and date formatting. A till that cannot format a price is not a till.
$icu = Get-Missing { $_.url -match '\.dat$' } 'globalization data'
Show 'globalization data cached' "$icu data file(s)"

# --- the manifest and its icons ------------------------------------------------

$manifestSrc = Join-Path $wwwroot 'manifest.webmanifest'

if (-not (Test-Path $manifestSrc)) { Fail 'the published app has no manifest.webmanifest.' }

$pwa = Get-Content $manifestSrc -Raw | ConvertFrom-Json

if (-not (Test-Cached 'manifest.webmanifest')) { Fail 'the web manifest is not cached.' }

$maskable = @($pwa.icons | Where-Object { $_.purpose -eq 'maskable' })

if ($maskable.Count -eq 0) {
    Fail 'the manifest has no maskable icon, so Android will letterbox or crop the installed icon.'
}

if ($pwa.display -ne 'standalone') {
    Fail "display is '$($pwa.display)'; an installed till should be standalone."
}

foreach ($icon in $pwa.icons) {
    $iconPath = Join-Path $wwwroot $icon.src

    if (-not (Test-Path $iconPath)) {
        Fail "the manifest names an icon that is not published: $($icon.src)."
    }
}

# Template branding is a real defect rather than a cosmetic one: it is what the shop sees on the
# tablet it just installed.
if ($pwa.name -match '^Pos\.Web$' -or $pwa.short_name -match '^Pos\.Web$') {
    Fail "the app still calls itself '$($pwa.name)', which is the project name rather than the product."
}

Show 'installable manifest' "$($pwa.name) / $($pwa.short_name), $($pwa.icons.Count) icon(s), standalone"

# --- what is not cached, and why that is allowed -------------------------------

if ($skipped.Count -gt 0) {
    $unexpected = @($skipped | Where-Object { $_.url -notmatch '\.map$' })

    if ($unexpected.Count -gt 0) {
        Fail ("$($unexpected.Count) asset(s) are shipped but never cached, and are not source maps: " +
            (($unexpected | Select-Object -First 3 | ForEach-Object { $_.url }) -join ', '))
    }

    Show 'uncached assets are source maps' "$($skipped.Count) file(s), devtools only"
}
else {
    Show 'uncached assets' 'none'
}

# --- what a shop actually downloads --------------------------------------------

$transfer = 0L
foreach ($asset in $cached) {
    $path = $asset.url -replace '\?.*$', ''
    $full = Join-Path $wwwroot $path

    # Precompressed where the host can serve it, which is what a browser receives.
    if (Test-Path "$full.br") { $transfer += (Get-Item "$full.br").Length }
    elseif (Test-Path $full) { $transfer += (Get-Item $full).Length }
}

Show 'first load over the wire' ("{0:N2} MB brotli, then served from cache" -f ($transfer / 1MB))

Write-Output ''
Write-Output 'Offline check passed.'
