<#
  Generates src/FolderNotes.App/Assets/FolderNotes.ico (T2.2): a folder with a note card on it.
  Drawn with WPF vector geometry on a 256-unit canvas and rendered at each icon size, so small
  sizes stay crisp. Run with Windows PowerShell 5.1:  powershell -File eng\make-icon.ps1
  Colours here are the icon's own artwork, not UI tokens (specs/05 § Design tokens covers UI).
#>
param([string]$Out = (Join-Path $PSScriptRoot '..\src\FolderNotes.App\Assets\FolderNotes.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName PresentationCore, WindowsBase

function Brush([string]$hex) { $b = [Windows.Media.SolidColorBrush]([Windows.Media.ColorConverter]::ConvertFromString($hex)); $b.Freeze(); $b }
function Geo([string]$data) { [Windows.Media.Geometry]::Parse($data) }
function Grad([string]$a, [string]$b) {
    $g = New-Object Windows.Media.LinearGradientBrush(
        [Windows.Media.ColorConverter]::ConvertFromString($a),
        [Windows.Media.ColorConverter]::ConvertFromString($b), 90.0)
    $g.Freeze(); $g
}

function Render([int]$size) {
    $visual = New-Object Windows.Media.DrawingVisual
    $dc = $visual.RenderOpen()
    $s = $size / 256.0
    $dc.PushTransform((New-Object Windows.Media.ScaleTransform($s, $s)))
    $small = $size -le 24

    # Folder back with tab.
    $dc.DrawGeometry((Brush '#FFC98A0A'), $null, (Geo 'M 28,52 Q 28,36 44,36 L 100,36 Q 110,36 117,44 L 128,56 L 212,56 Q 228,56 228,72 L 228,196 Q 228,212 212,212 L 44,212 Q 28,212 28,196 Z'))
    # Folder front.
    $dc.DrawGeometry((Grad '#FFFFD25C' '#FFF2B126'), $null, (Geo 'M 28,92 Q 28,78 42,78 L 214,78 Q 228,78 228,92 L 228,198 Q 228,214 212,214 L 44,214 Q 28,214 28,198 Z'))

    # Note card, slightly rotated, on the lower right.
    $dc.PushTransform((New-Object Windows.Media.RotateTransform(-6, 170, 170)))
    $shadow = Brush '#33000000'
    $dc.DrawRoundedRectangle($shadow, $null, (New-Object Windows.Rect(110, 108, 124, 136)), 14, 14)
    $dc.DrawRoundedRectangle((Brush '#FFFFFFFF'), (New-Object Windows.Media.Pen((Brush '#FFD6D6D6'), 2)), (New-Object Windows.Rect(104, 102, 124, 136)), 14, 14)
    # Accent bar (blue, the app's default note accent).
    $dc.DrawRoundedRectangle((Brush '#FF0078D4'), $null, (New-Object Windows.Rect(116, 118, 10, 104)), 5, 5)
    if ($small) {
        $dc.DrawRoundedRectangle((Brush '#FF8A8A8A'), $null, (New-Object Windows.Rect(138, 128, 76, 18)), 9, 9)
        $dc.DrawRoundedRectangle((Brush '#FFB5B5B5'), $null, (New-Object Windows.Rect(138, 168, 60, 18)), 9, 9)
    } else {
        $dc.DrawRoundedRectangle((Brush '#FF5C5C5C'), $null, (New-Object Windows.Rect(138, 122, 74, 12)), 6, 6)
        foreach ($y in 150, 174, 198) {
            $w = if ($y -eq 198) { 46 } else { 74 }
            $dc.DrawRoundedRectangle((Brush '#FFBDBDBD'), $null, (New-Object Windows.Rect(138, $y, $w, 10)), 5, 5)
        }
    }
    $dc.Pop()
    $dc.Pop()
    $dc.Close()

    $bmp = New-Object Windows.Media.Imaging.RenderTargetBitmap($size, $size, 96, 96, [Windows.Media.PixelFormats]::Pbgra32)
    $bmp.Render($visual)
    $enc = New-Object Windows.Media.Imaging.PngBitmapEncoder
    $enc.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp))
    $ms = New-Object IO.MemoryStream
    $enc.Save($ms)
    , $ms.ToArray()
}

# ICO container with PNG-compressed entries (supported since Windows Vista).
$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = @($sizes | ForEach-Object { , (Render $_) })
$ico = New-Object IO.MemoryStream
$w = New-Object IO.BinaryWriter($ico)
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$images[$i].Length); $w.Write([uint32]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $w.Write([byte[]]$img) }
$w.Flush()
[IO.File]::WriteAllBytes((Resolve-Path -LiteralPath (Split-Path $Out)).Path + '\' + (Split-Path $Out -Leaf), $ico.ToArray())
Write-Host "Wrote $Out ($($ico.Length) bytes, sizes $($sizes -join ', '))"

# Also drop a 256 px PNG preview next to the script output for review (not shipped).
if ($env:FN_ICON_PREVIEW) { [IO.File]::WriteAllBytes($env:FN_ICON_PREVIEW, $images[-1]); [IO.File]::WriteAllBytes($env:FN_ICON_PREVIEW + '.32.png', $images[3]) ; [IO.File]::WriteAllBytes($env:FN_ICON_PREVIEW + '.16.png', $images[0]) }
