[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$ImagePath)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$taskRoot = Split-Path -Parent $PSScriptRoot
$taskAssets = Join-Path $taskRoot 'src\TranslatorAnywhere\Assets'
$taskSourcePath = (Resolve-Path -LiteralPath $ImagePath).Path
$taskSizes = @(16, 20, 24, 32, 48, 64, 128, 256)
$taskFrames = [System.Collections.Generic.List[byte[]]]::new()
# Decode into an owned bitmap, so importing the saved source itself never locks the output file.
$taskSourceImage = [System.Drawing.Image]::FromFile($taskSourcePath)
try { $taskSource = [System.Drawing.Bitmap]::new($taskSourceImage) }
finally { $taskSourceImage.Dispose() }
try {
    if ($taskSource.Width -ne $taskSource.Height) { throw '请使用正方形图稿，避免图标被拉伸。' }
    foreach ($taskSize in $taskSizes) {
        $taskBitmap = [System.Drawing.Bitmap]::new($taskSize, $taskSize, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $taskGraphics = [System.Drawing.Graphics]::FromImage($taskBitmap)
        $taskStream = [System.IO.MemoryStream]::new()
        $taskAttributes = [System.Drawing.Imaging.ImageAttributes]::new()
        try {
            $taskGraphics.Clear([System.Drawing.Color]::Transparent)
            $taskGraphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
            $taskGraphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
            $taskGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $taskGraphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $taskAttributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
            $taskGraphics.DrawImage($taskSource, [System.Drawing.Rectangle]::new(0, 0, $taskSize, $taskSize), 0, 0, $taskSource.Width, $taskSource.Height, [System.Drawing.GraphicsUnit]::Pixel, $taskAttributes)
            $taskBitmap.Save($taskStream, [System.Drawing.Imaging.ImageFormat]::Png)
            $taskFrames.Add($taskStream.ToArray())
        }
        finally { $taskAttributes.Dispose(); $taskStream.Dispose(); $taskGraphics.Dispose(); $taskBitmap.Dispose() }
    }
    $taskOutput = [System.IO.MemoryStream]::new()
    $taskWriter = [System.IO.BinaryWriter]::new($taskOutput)
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
            $taskWriter.Write([uint32]$taskFrames[$taskIndex].Length)
            $taskWriter.Write([uint32]$taskOffset)
            $taskOffset += $taskFrames[$taskIndex].Length
        }
        foreach ($taskFrame in $taskFrames) { $taskWriter.Write($taskFrame) }
        $taskWriter.Flush()
        [System.IO.File]::WriteAllBytes((Join-Path $taskAssets 'app.ico'), $taskOutput.ToArray())
    }
    finally { $taskWriter.Dispose(); $taskOutput.Dispose() }
    $taskSource.Save((Join-Path $taskAssets 'app-source.png'), [System.Drawing.Imaging.ImageFormat]::Png)
    [System.IO.File]::WriteAllBytes((Join-Path $taskAssets 'app-256.png'), $taskFrames[$taskFrames.Count - 1])
    Write-Output "已导入图标，尺寸：$($taskSizes -join ', ') 像素。"
}
finally { $taskSource.Dispose() }