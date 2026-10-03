[CmdletBinding()]
param(
    [string]$Label = '翻',
    [ValidatePattern('^#[0-9A-Fa-f]{6}$')][string]$Color = '#3390EC',
    [string]$FontFamily = 'Microsoft YaHei UI'
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskAssets = Join-Path $taskRoot 'src\TranslatorAnywhere\Assets'
[System.IO.Directory]::CreateDirectory($taskAssets) | Out-Null
$taskSizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$taskBackground = [System.Drawing.Drawing2D.GraphicsPath]::new()
$taskGlyph = [System.Drawing.Drawing2D.GraphicsPath]::new()
$taskFamily = [System.Drawing.FontFamily]::new($FontFamily)
$taskTransform = [System.Drawing.Drawing2D.Matrix]::new()
$taskBlue = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml($Color))
$taskMaster = [System.Drawing.Bitmap]::new(1024, 1024, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)

try {
    # The 256-unit vector geometry is also used for every raster resolution.
    $taskBackground.AddArc(8, 8, 96, 96, 180, 90)
    $taskBackground.AddArc(152, 8, 96, 96, 270, 90)
    $taskBackground.AddArc(152, 152, 96, 96, 0, 90)
    $taskBackground.AddArc(8, 152, 96, 96, 90, 90)
    $taskBackground.CloseFigure()
    $taskGlyph.AddString($Label, $taskFamily, [int][System.Drawing.FontStyle]::Bold, 160,
        [System.Drawing.PointF]::new(0, 0), [System.Drawing.StringFormat]::GenericTypographic)
    $taskBounds = $taskGlyph.GetBounds()
    if ($taskBounds.Width -le 0 -or $taskBounds.Height -le 0) { throw '图标文字没有可绘制的字形。' }
    $taskFit = [Math]::Min(176.0 / $taskBounds.Width, 176.0 / $taskBounds.Height)
    $taskTransform.Translate(-$taskBounds.Left, -$taskBounds.Top)
    $taskGlyph.Transform($taskTransform)
    $taskTransform.Reset()
    $taskTransform.Scale([single]$taskFit, [single]$taskFit)
    $taskGlyph.Transform($taskTransform)
    $taskTransform.Reset()
    $taskTransform.Translate([single]((256 - $taskBounds.Width * $taskFit) / 2),
        [single]((256 - $taskBounds.Height * $taskFit) / 2))
    $taskGlyph.Transform($taskTransform)

    $taskGraphics = [System.Drawing.Graphics]::FromImage($taskMaster)
    try {
        $taskGraphics.Clear([System.Drawing.Color]::Transparent)
        $taskGraphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $taskGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $taskGraphics.ScaleTransform(4, 4)
        $taskGraphics.FillPath($taskBlue, $taskBackground)
        $taskGraphics.FillPath([System.Drawing.Brushes]::White, $taskGlyph)
    }
    finally { $taskGraphics.Dispose() }

    # Export text as outline paths, so the SVG does not require the generation font at runtime.
    $taskPoints = $taskGlyph.PathPoints
    $taskTypes = $taskGlyph.PathTypes
    $taskSvgPath = [System.Text.StringBuilder]::new()
    function Format-IconNumber([single]$value) { $value.ToString('0.##', [System.Globalization.CultureInfo]::InvariantCulture) }
    for ($taskIndex = 0; $taskIndex -lt $taskPoints.Length; $taskIndex++) {
        $taskPoint = $taskPoints[$taskIndex]
        $taskType = $taskTypes[$taskIndex] -band 7
        if ($taskType -eq 0) {
            [void]$taskSvgPath.Append("M$(Format-IconNumber $taskPoint.X) $(Format-IconNumber $taskPoint.Y) ")
        }
        elseif ($taskType -eq 1) {
            [void]$taskSvgPath.Append("L$(Format-IconNumber $taskPoint.X) $(Format-IconNumber $taskPoint.Y) ")
        }
        elseif ($taskType -eq 3) {
            $taskSecond = $taskPoints[$taskIndex + 1]
            $taskThird = $taskPoints[$taskIndex + 2]
            [void]$taskSvgPath.Append("C$(Format-IconNumber $taskPoint.X) $(Format-IconNumber $taskPoint.Y) $(Format-IconNumber $taskSecond.X) $(Format-IconNumber $taskSecond.Y) $(Format-IconNumber $taskThird.X) $(Format-IconNumber $taskThird.Y) ")
            $taskIndex += 2
        }
        else { throw '无法导出图标轮廓。' }
        if (($taskTypes[$taskIndex] -band 128) -ne 0) { [void]$taskSvgPath.Append('Z ') }
    }
    $taskSvg = @"
<svg xmlns="http://www.w3.org/2000/svg" width="256" height="256" viewBox="0 0 256 256" role="img" aria-label="Anywhere Translator">
  <title>Anywhere Translator</title>
  <rect x="8" y="8" width="240" height="240" rx="48" fill="$Color" />
  <path d="$taskSvgPath" fill="#FFFFFF" fill-rule="evenodd" />
</svg>
"@
    [System.IO.File]::WriteAllText((Join-Path $taskAssets 'app.svg'), $taskSvg, [System.Text.UTF8Encoding]::new($false))

    $taskImages = [System.Collections.Generic.List[byte[]]]::new()
    foreach ($taskSize in $taskSizes) {
        $taskBitmap = [System.Drawing.Bitmap]::new($taskSize, $taskSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $taskImageStream = [System.IO.MemoryStream]::new()
        $taskResize = [System.Drawing.Graphics]::FromImage($taskBitmap)
        try {
            $taskResize.Clear([System.Drawing.Color]::Transparent)
            $taskResize.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $taskResize.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $taskResize.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $taskResize.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $taskResize.DrawImage($taskMaster, [System.Drawing.Rectangle]::new(0, 0, $taskSize, $taskSize),
                0, 0, 1024, 1024, [System.Drawing.GraphicsUnit]::Pixel)
            $taskBitmap.Save($taskImageStream, [System.Drawing.Imaging.ImageFormat]::Png)
            $taskImages.Add($taskImageStream.ToArray())
            if ($taskSize -eq 256) {
                [System.IO.File]::WriteAllBytes((Join-Path $taskAssets 'app-256.png'), $taskImageStream.ToArray())
            }
        }
        finally { $taskResize.Dispose(); $taskBitmap.Dispose(); $taskImageStream.Dispose() }
    }

    $taskIconStream = [System.IO.MemoryStream]::new()
    $taskWriter = [System.IO.BinaryWriter]::new($taskIconStream)
    try {
        $taskWriter.Write([uint16]0)
        $taskWriter.Write([uint16]1)
        $taskWriter.Write([uint16]$taskSizes.Length)
        $taskOffset = 6 + 16 * $taskSizes.Length
        for ($taskIndex = 0; $taskIndex -lt $taskSizes.Length; $taskIndex++) {
            $taskDimension = if ($taskSizes[$taskIndex] -eq 256) { 0 } else { $taskSizes[$taskIndex] }
            $taskWriter.Write([byte]$taskDimension)
            $taskWriter.Write([byte]$taskDimension)
            $taskWriter.Write([byte]0)
            $taskWriter.Write([byte]0)
            $taskWriter.Write([uint16]1)
            $taskWriter.Write([uint16]32)
            $taskWriter.Write([uint32]$taskImages[$taskIndex].Length)
            $taskWriter.Write([uint32]$taskOffset)
            $taskOffset += $taskImages[$taskIndex].Length
        }
        foreach ($taskImage in $taskImages) { $taskWriter.Write($taskImage) }
        $taskWriter.Flush()
        [System.IO.File]::WriteAllBytes((Join-Path $taskAssets 'app.ico'), $taskIconStream.ToArray())
    }
    finally { $taskWriter.Dispose(); $taskIconStream.Dispose() }
    Write-Output "图标已生成：$taskAssets\app.ico（$($taskSizes -join ', ') 像素）"
    Write-Output "矢量源：$taskAssets\app.svg"
}
finally {
    $taskBackground.Dispose()
    $taskGlyph.Dispose()
    $taskFamily.Dispose()
    $taskTransform.Dispose()
    $taskBlue.Dispose()
    $taskMaster.Dispose()
}
