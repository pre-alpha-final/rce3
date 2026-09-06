# Original geometric TabletUI icon. Run from any directory with PowerShell on Windows.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$outputDirectory = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../wwwroot'))
foreach ($size in @(192, 512)) {
    $bitmap = [Drawing.Bitmap]::new($size, $size)
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.ScaleTransform(($size / 512.0), ($size / 512.0))
    $graphics.Clear([Drawing.ColorTranslator]::FromHtml('#f4df91'))
    $ink = [Drawing.Pen]::new([Drawing.ColorTranslator]::FromHtml('#263d3b'), 14)
    $ink.LineJoin = [Drawing.Drawing2D.LineJoin]::Round
    $paper = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#fffdf5'))
    $teal = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#17645a'))
    $yellow = [Drawing.SolidBrush]::new([Drawing.ColorTranslator]::FromHtml('#f4df91'))
    try {
        $remote = [Drawing.PointF[]]@([Drawing.PointF]::new(141,84),[Drawing.PointF]::new(370,84),[Drawing.PointF]::new(381,418),[Drawing.PointF]::new(134,430))
        $graphics.FillPolygon($paper, $remote)
        $graphics.DrawPolygon($ink, $remote)
        $screen = [Drawing.PointF[]]@([Drawing.PointF]::new(164,124),[Drawing.PointF]::new(343,119),[Drawing.PointF]::new(348,258),[Drawing.PointF]::new(160,263))
        $graphics.FillPolygon($teal, $screen)
        $graphics.DrawPolygon($ink, $screen)
        $play = [Drawing.PointF[]]@([Drawing.PointF]::new(231,151),[Drawing.PointF]::new(296,191),[Drawing.PointF]::new(231,233))
        $graphics.FillPolygon($yellow, $play)
        $ink.Width = 10
        $graphics.FillRectangle($yellow,168,304,55,39)
        $graphics.DrawRectangle($ink,168,304,55,39)
        $graphics.FillRectangle($yellow,285,302,54,39)
        $graphics.DrawRectangle($ink,285,302,54,39)
        $graphics.DrawLine($ink,187,382,319,382)
        $bitmap.Save((Join-Path $outputDirectory "icon-$size.png"), [Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $ink.Dispose(); $paper.Dispose(); $teal.Dispose(); $yellow.Dispose()
        $graphics.Dispose(); $bitmap.Dispose()
    }
}
