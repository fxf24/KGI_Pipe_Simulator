# Builds models/<id>.glb from lines/<id>.json (coordinates in mm as [E, N, EL]).
# Usage: powershell -ExecutionPolicy Bypass -File tools\build-line.ps1            (all lines)
#        powershell -ExecutionPolicy Bypass -File tools\build-line.ps1 PA-00011   (one line)
param([string[]]$Ids)
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Add-Type -Path (Join-Path $PSScriptRoot 'GlbBuilder.cs')

function ConvertTo-Doubles($v) { [double[]]@($v | ForEach-Object { $_ } | ForEach-Object { [double]$_ }) }

$files = if ($Ids) { $Ids | ForEach-Object { Join-Path $root "lines\$_.json" } } else { Get-ChildItem (Join-Path $root 'lines\*.json') | ForEach-Object FullName }
New-Item -ItemType Directory -Force -Path (Join-Path $root 'models') | Out-Null

foreach ($f in $files) {
    $line = [System.IO.File]::ReadAllText($f, [System.Text.Encoding]::UTF8) | ConvertFrom-Json
    $b = New-Object GlbBuilder
    foreach ($c in $line.components) {
        switch ($c.type) {
            'run'    { $b.Run((ConvertTo-Doubles $c.points), $c.od, $c.bendRadius) }
            'flange' { $b.Flange((ConvertTo-Doubles $c.at), (ConvertTo-Doubles $c.axis), $c.od, $c.thick, $c.bore) }
            'valve'  { $b.Valve((ConvertTo-Doubles $c.at), (ConvertTo-Doubles $c.axis), (ConvertTo-Doubles $c.stem), $c.pipeOd, $c.length) }
            'cap'    { $b.Cap((ConvertTo-Doubles $c.at), (ConvertTo-Doubles $c.axis), $c.od) }
            'box'    { $b.Box($c.material, (ConvertTo-Doubles $c.min), (ConvertTo-Doubles $c.max)) }
            default  { throw "Unknown component type '$($c.type)' in $f" }
        }
    }
    $id = [System.IO.Path]::GetFileNameWithoutExtension($f)
    # <id>.glb keeps real site coordinates (used by ar.html alignment);
    # <id>-view.glb sits on the floor, centered (used by the model list / AR Quick Look / Scene Viewer).
    $out = Join-Path $root "models\$id.glb"
    $b.Save($out, $line.name, $false)
    $b.Save((Join-Path $root "models\$id-view.glb"), $line.name, $true)
    Write-Output ("{0} -> models\{1}.glb + {1}-view.glb ({2:N0} bytes)" -f $line.name, $id, (Get-Item $out).Length)
}
