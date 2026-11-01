param([switch]$NoTest)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$root = Split-Path -Parent $here
$src = Join-Path $root 'src'
$out = Join-Path $here 'out'
$log = Join-Path $here 'build-log.txt'
"Build started $(Get-Date)" | Out-File $log -Encoding ascii
function L($m) { $m | Out-File $log -Append -Encoding ascii; Write-Host $m }

try {
  New-Item -ItemType Directory -Force -Path $out | Out-Null
  & (Join-Path $here 'brand-apply.ps1') -Src $src | ForEach-Object { L $_ }

  # --- icon (generated, no binary files in the repo) ---
  Add-Type -AssemblyName System.Drawing
  $sz = 256
  $bmp = New-Object System.Drawing.Bitmap $sz, $sz
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = 'AntiAlias'; $g.TextRenderingHint = 'AntiAliasGridFit'
  $path = New-Object System.Drawing.Drawing2D.GraphicsPath
  $r = 56; $d = $r * 2
  $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($sz - $d, 0, $d, $d, 270, 90)
  $path.AddArc($sz - $d, $sz - $d, $d, $d, 0, 90); $path.AddArc(0, $sz - $d, $d, $d, 90, 90); $path.CloseFigure()
  $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(0, 0)), ([System.Drawing.Point]::new($sz, $sz)), ([System.Drawing.Color]::FromArgb(255, 24, 28, 64)), ([System.Drawing.Color]::FromArgb(255, 92, 54, 196))
  $g.FillPath($br, $path)
  # storm cloud (soft, behind the bolt)
  $cl = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(70, 200, 210, 255))
  $g.FillEllipse($cl, 26, 54, 96, 66); $g.FillEllipse($cl, 78, 26, 112, 92); $g.FillEllipse($cl, 140, 52, 92, 64)
  # glow
  $glow = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(60, 255, 228, 92))
  $bolt = [System.Drawing.PointF[]]@(
    (New-Object System.Drawing.PointF 150, 30), (New-Object System.Drawing.PointF 66, 150), (New-Object System.Drawing.PointF 118, 150),
    (New-Object System.Drawing.PointF 98, 230), (New-Object System.Drawing.PointF 192, 104), (New-Object System.Drawing.PointF 138, 104))
  $gp = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(70, 255, 228, 92)), 16
  $gp.LineJoin = 'Round'
  $g.DrawPolygon($gp, $bolt)
  $bb = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.Point]::new(100, 30)), ([System.Drawing.Point]::new(140, 230)), ([System.Drawing.Color]::FromArgb(255, 255, 255, 255)), ([System.Drawing.Color]::FromArgb(255, 255, 214, 64))
  $g.FillPolygon($bb, $bolt)
  $g.Dispose()
  $png = Join-Path $out 'app.png'
  $bmp.Save($png, [System.Drawing.Imaging.ImageFormat]::Png)
  $pngBytes = [System.IO.File]::ReadAllBytes($png)
  $ico = Join-Path $out 'app.ico'
  $ms = New-Object System.IO.MemoryStream
  $bw = New-Object System.IO.BinaryWriter $ms
  $bw.Write([uint16]0); $bw.Write([uint16]1); $bw.Write([uint16]1)
  $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0); $bw.Write([byte]0)
  $bw.Write([uint16]1); $bw.Write([uint16]32); $bw.Write([uint32]$pngBytes.Length); $bw.Write([uint32]22)
  $bw.Write($pngBytes); $bw.Flush()
  [System.IO.File]::WriteAllBytes($ico, $ms.ToArray())
  L "icon ok"

  # --- compile ---
  $fw = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
  $csc = Join-Path $fw 'csc.exe'
  if (-not (Test-Path $csc)) { throw "csc.exe not found: $csc" }
  $wpf = Join-Path $fw 'WPF'
  $asms = 'PresentationFramework', 'PresentationCore', 'WindowsBase', 'System.Xaml', 'System', 'System.Core', 'System.Management', 'System.Windows.Forms', 'System.Drawing', 'System.ServiceProcess', 'System.Web.Extensions', 'Microsoft.CSharp'
  $refs = foreach ($a in $asms) { if (Test-Path (Join-Path $wpf "$a.dll")) { '/r:' + (Join-Path $wpf "$a.dll") } else { "/r:$a.dll" } }
  $exe = Join-Path $out 'IXC-PC-Ready.exe'
  Get-Process -Name 'IXC-PC-Ready' -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $exe } | Stop-Process -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 700
  if (Test-Path $exe) { Remove-Item $exe -Force }
  $files = Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }
  $res = @()
  foreach ($x in Get-ChildItem (Join-Path $src 'Resources') -Filter *.xaml) { $res += ('/resource:"' + $x.FullName + '",' + $x.Name) }
  $res += ('/resource:"' + $png + '",app.png')
  $argList = @('/nologo', '/target:winexe', '/platform:x64', '/optimize+', '/debug-', '/warn:3', ('/out:"' + $exe + '"'), ('/win32manifest:"' + (Join-Path $here 'app.manifest') + '"'), ('/win32icon:"' + $ico + '"')) + $refs + $res + ($files | ForEach-Object { '"' + $_ + '"' })
  $rsp = Join-Path $out 'build.rsp'
  [System.IO.File]::WriteAllLines($rsp, [string[]]$argList, (New-Object System.Text.UTF8Encoding $false))
  L ("csc @" + $rsp)
  $outTxt = & $csc ('@' + $rsp) 2>&1 | Out-String
  L $outTxt
  if (-not (Test-Path $exe)) { throw "COMPILE FAILED (see messages above)" }
  L ("BUILD OK: " + $exe + "  " + [math]::Round((Get-Item $exe).Length / 1KB) + " KB")

  if (-not $NoTest) {
    $st = Join-Path $out 'selftest'
    if (Test-Path $st) { Remove-Item $st -Recurse -Force }
    L "running selftest..."
    $p = Start-Process -FilePath $exe -ArgumentList '--selftest' -PassThru
    if (-not $p.WaitForExit(330000)) { try { $p.Kill() } catch {} ; L "selftest TIMEOUT (killed)" } else { L ("selftest exit " + $p.ExitCode) }
    if (Test-Path (Join-Path $st 'selftest.txt')) { Copy-Item (Join-Path $st 'selftest.txt') (Join-Path $here 'selftest-report.txt') -Force; L "selftest report copied" }
    $se = Join-Path $out 'startup-error.txt'
    if (Test-Path $se) { Copy-Item $se (Join-Path $here 'startup-error.txt') -Force; L "STARTUP ERROR file found" }
  }
  # --- release package ---
  $dist = Join-Path $root 'dist'
  if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
  New-Item -ItemType Directory -Force -Path $dist | Out-Null
  $rel = Join-Path $dist 'IXC-PC-Ready.exe'
  Copy-Item $exe $rel
  ((Get-FileHash $rel -Algorithm SHA256).Hash + '  IXC-PC-Ready.exe') | Out-File ($rel + '.sha256') -Encoding ascii
  L ("release file ok: " + $rel)
  L "DONE"
}
catch {
  L ("FAILED: " + $_.Exception.Message)
}
