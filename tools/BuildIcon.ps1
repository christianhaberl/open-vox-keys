Add-Type -AssemblyName System.Drawing
$root=Join-Path (Split-Path $PSScriptRoot -Parent) 'assets'
$image=[System.Drawing.Image]::FromFile("$root\app-icon.png")
$sizes=@(16,20,24,32,40,48,64,128,256)
$frames=New-Object 'System.Collections.Generic.List[byte[]]'
foreach($size in $sizes){
 $bitmap=New-Object System.Drawing.Bitmap($size,$size,[System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
 $g=[System.Drawing.Graphics]::FromImage($bitmap)
 $g.CompositingMode=[System.Drawing.Drawing2D.CompositingMode]::SourceCopy
 $g.InterpolationMode=[System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
 $g.DrawImage($image,0,0,$size,$size)
 $stream=New-Object System.IO.MemoryStream
 $bitmap.Save($stream,[System.Drawing.Imaging.ImageFormat]::Png)
 $frames.Add($stream.ToArray())
 $stream.Dispose();$g.Dispose();$bitmap.Dispose()
}
$image.Dispose()
$file=[System.IO.File]::Create("$root\app.ico")
$writer=New-Object System.IO.BinaryWriter($file)
$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count)
$offset=6+16*$sizes.Count
for($i=0;$i -lt $sizes.Count;$i++){
 $n=if($sizes[$i] -eq 256){0}else{$sizes[$i]}
 $writer.Write([byte]$n);$writer.Write([byte]$n);$writer.Write([byte]0);$writer.Write([byte]0)
 $writer.Write([uint16]1);$writer.Write([uint16]32)
 $writer.Write([uint32]$frames[$i].Length);$writer.Write([uint32]$offset)
 $offset+=$frames[$i].Length
}
foreach($frame in $frames){$writer.Write($frame)}
$writer.Dispose();$file.Dispose()
$icon=New-Object System.Drawing.Icon("$root\app.ico",32,32)
Write-Output "ICO loaded: $($icon.Width)x$($icon.Height); $($sizes.Count) sizes; original PNG unchanged"
$icon.Dispose()
