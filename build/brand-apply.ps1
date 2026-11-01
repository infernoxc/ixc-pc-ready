param([string]$Src)
# Applies the brand spelling inside string literals of the source files.
# Safe to run many times: once the text is changed there is nothing left to replace.
$utf8 = New-Object System.Text.UTF8Encoding $false
$count = 0
foreach ($f in (Get-ChildItem $Src -Recurse -Include *.cs,*.xaml)) {
  $t = [System.IO.File]::ReadAllText($f.FullName)
  $n = $t
  if ($f.Extension -eq '.cs') {
    $n = [regex]::Replace($t, '"[^"\r\n]*"', {
      param($m)
      $v = $m.Value
      $v = $v -replace 'InfernoXC PC Ready', 'IXC PC Ready'
      $v = $v -replace 'INFERNOXC', 'InFerNoxC'
      $v = $v -replace 'InfernoXC', 'InFerNoxC'
      $v
    })
  } else {
    $n = $t -replace 'InfernoXC PC Ready', 'IXC PC Ready'
  }
  if ($n -ne $t) { [System.IO.File]::WriteAllText($f.FullName, $n, $utf8); $count++ }
}
Write-Host ("brand applied to " + $count + " file(s)")
