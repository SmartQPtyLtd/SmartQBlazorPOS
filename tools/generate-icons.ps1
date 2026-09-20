# Generates the PWA icons.
#
# The template ships the .NET Foundation's logo, so a shop that installs the till on a tablet gets
# a purple "@" labelled "Pos.Web" on its home screen. This draws the app's own mark instead: a
# receipt on the till's darkest surface colour, which reads as "till" at 192 px and is honest about
# what the app is rather than borrowing someone else's brand.
#
# Kept as a script rather than committed binaries alone, so the mark can be regenerated if the
# palette changes. Run from the repository root:
#
#     pwsh -NoProfile -File tools/generate-icons.ps1

[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root 'src/Pos.Web/wwwroot'

# Straight from pos.css, so the installed icon matches the app.
$background = [System.Drawing.ColorTranslator]::FromHtml('#10151c')
$surface = [System.Drawing.ColorTranslator]::FromHtml('#1a2230')
$accent = [System.Drawing.ColorTranslator]::FromHtml('#4c8dff')
$text = [System.Drawing.ColorTranslator]::FromHtml('#e8edf5')

function New-Icon {
    param(
        [int]$Size,
        [string]$Path,

        # A maskable icon is cropped to whatever shape the platform likes, so everything
        # meaningful has to sit inside the middle 80% or it can be cut off.
        [switch]$Maskable
    )

    $bitmap = New-Object System.Drawing.Bitmap($Size, $Size)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    # Full-bleed background for maskable; a rounded tile for the ordinary icon, so it sits
    # comfortably next to other home-screen icons rather than looking like a square blob.
    if ($Maskable) {
        $g.FillRectangle((New-Object System.Drawing.SolidBrush($background)), 0, 0, $Size, $Size)
    }
    else {
        $radius = [int]($Size * 0.22)
        $path2 = New-Object System.Drawing.Drawing2D.GraphicsPath
        $d = $radius * 2
        $path2.AddArc(0, 0, $d, $d, 180, 90)
        $path2.AddArc($Size - $d, 0, $d, $d, 270, 90)
        $path2.AddArc($Size - $d, $Size - $d, $d, $d, 0, 90)
        $path2.AddArc(0, $Size - $d, $d, $d, 90, 90)
        $path2.CloseFigure()
        $g.FillPath((New-Object System.Drawing.SolidBrush($background)), $path2)
    }

    # A receipt: a tall rounded rectangle with a torn bottom edge, ruled lines, and a total bar.
    # Below about 48 px the rules and the tear stop resolving, so the small sizes get the silhouette
    # and the accent bar only.
    $detailed = $Size -ge 48

    $scale = if ($Maskable) { 0.52 } else { 0.62 }
    if (-not $detailed) { $scale = 0.78 }

    $w = [int]($Size * $scale * 0.62)
    $h = [int]($Size * $scale * 1.18)
    $x = [int](($Size - $w) / 2)
    $y = [int](($Size - $h) / 2)

    $paper = New-Object System.Drawing.SolidBrush($text)
    $g.FillRectangle($paper, $x, $y, $w, $h)

    # Torn edge: triangles cut out of the bottom, which is what makes it read as a receipt
    # rather than as a plain rectangle.
    if ($detailed) {
        $teeth = 5
        $toothWidth = [double]$w / $teeth
        $toothHeight = [int]($toothWidth * 0.5)
        $bgBrush = New-Object System.Drawing.SolidBrush($background)

        for ($i = 0; $i -lt $teeth; $i++) {
            $tx = [int]($x + ($i * $toothWidth))
            $points = @(
                (New-Object System.Drawing.Point($tx, ($y + $h))),
                (New-Object System.Drawing.Point(($tx + [int]($toothWidth / 2)), ($y + $h - $toothHeight))),
                (New-Object System.Drawing.Point(($tx + [int]$toothWidth), ($y + $h)))
            )
            $g.FillPolygon($bgBrush, $points)
        }
    }

    $inset = [int]($w * 0.16)
    $lineWidth = $w - ($inset * 2)

    if ($detailed) {
        # Ruled lines, then a thicker accent bar standing in for the total.
        $lineBrush = New-Object System.Drawing.SolidBrush($surface)
        $lineHeight = [Math]::Max(2, [int]($Size * 0.018))

        for ($i = 0; $i -lt 3; $i++) {
            $ly = $y + [int]($h * (0.22 + ($i * 0.16)))
            $g.FillRectangle($lineBrush, ($x + $inset), $ly, $lineWidth, $lineHeight)
        }
    }

    $accentBrush = New-Object System.Drawing.SolidBrush($accent)
    $ay = if ($detailed) { $y + [int]($h * 0.72) } else { $y + [int]($h * 0.42) }
    $g.FillRectangle($accentBrush, ($x + $inset), $ay, $lineWidth, ([Math]::Max(3, [int]($Size * 0.028))))

    $g.Dispose()
    $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()

    Write-Output ("  {0,-34} {1,7:N0} bytes" -f (Split-Path -Leaf $Path), (Get-Item $Path).Length)
}

Write-Output 'Generating PWA icons:'

New-Icon -Size 192 -Path (Join-Path $outDir 'icon-192.png')
New-Icon -Size 512 -Path (Join-Path $outDir 'icon-512.png')
New-Icon -Size 512 -Path (Join-Path $outDir 'icon-maskable-512.png') -Maskable

# The browser-tab icon. Small enough that the ruled lines and the torn edge turn to mud, so it is
# drawn from the same mark with the detail dropped: the accent bar and the paper silhouette are what
# survive at 32 px, and they are what make it recognisable next to a dozen other tabs.
New-Icon -Size 32 -Path (Join-Path $outDir 'favicon.png')
New-Icon -Size 64 -Path (Join-Path $outDir 'favicon-64.png')

Write-Output 'Done.'
