# Builds 3D models from lines/<id>.json.
#   models/<id>.glb       real site coordinates (used by ar.html alignment)
#   models/<id>-view.glb  on the floor, centered (model list / AR Quick Look / Scene Viewer)
#   models/<id>.json      metadata (name, reference points, run length vs BOM, branches)
#   models/index.json     list of all built lines (read by index.html)
# Usage: powershell -ExecutionPolicy Bypass -File tools\build-line.ps1 [ids...]
#
# A line is either { "components": [...] } (raw primitives, mm, [E, N, EL]) or
# { "header": {...} } written in drawing order, e.g. segments ["150 drain", "4400 support", "500 branch:PA-00012", "2800 rise"].
param([string[]]$Ids)
$ErrorActionPreference = 'Stop'
$root = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
Add-Type -Path (Join-Path $PSScriptRoot 'GlbBuilder.cs')
$utf8 = New-Object System.Text.UTF8Encoding($false)

# Nominal size -> OD, flange OD/thickness (ASME 150), long-radius elbow radius, flanged valve face-to-face (mm)
$SIZES = @{
    '3/4'   = @{ od = 26.7;  fl = 99;  ft = 14;   br = 38;  vl = 90 }
    '1-1/2' = @{ od = 48.3;  fl = 127; ft = 17.5; br = 57;  vl = 165 }
    '2'     = @{ od = 60.3;  fl = 152; ft = 19;   br = 76;  vl = 178 }
    '6'     = @{ od = 168.3; fl = 279; ft = 25;   br = 229; vl = 403 }
}

function D($v) { [double[]]@($v | ForEach-Object { [double]$_ }) }
function Add3($a, $b, $s) { D @(($a[0] + $b[0] * $s), ($a[1] + $b[1] * $s), ($a[2] + $b[2] * $s)) }

# Expand a drawing-order header spec into primitives. Returns metadata.
function Build-Header($b, $h) {
    $S = $SIZES[$h.size]; if (-not $S) { throw "Unknown size '$($h.size)'" }
    $BS = $SIZES[$(if ($h.branchSize) { $h.branchSize } else { $h.size })]
    $T = $SIZES['3/4']
    $r = $S.od / 2
    $E = D 1, 0, 0; $UP = D 0, 0, 1
    $segs = @($h.segments | ForEach-Object { $p = "$_".Trim() -split '\s+', 2; [pscustomobject]@{ len = [double]$p[0]; mark = $(if ($p.Count -gt 1) { $p[1] } else { '' }) } })
    $rise = ($segs | Where-Object { $_.mark -eq 'rise' } | Measure-Object -Property len -Sum).Sum
    if (-not $rise) { $rise = 0 }
    # BOP elevation is given for the first run, or for the last run when bopRun = "last"
    $cl0 = $h.bop + $r - $(if ($h.bopRun -eq 'last') { $rise } else { 0 })

    $cur = D 0, 0, $cl0
    $pts = New-Object System.Collections.Generic.List[double[]]; $pts.Add($cur)
    $supports = @(); $branches = @(); $runLen = 0

    # blind-flanged start
    $b.Flange((Add3 $cur $E ($S.ft / 2)), $E, $S.fl, $S.ft, $S.od - 6)
    $b.Flange((Add3 $cur $E (-$S.ft / 2)), $E, $S.fl, $S.ft, 0)

    # NOTE: PowerShell variable names are case-insensitive; don't reuse $S/$BS/$T/$b names here.
    foreach ($seg in $segs) {
        $runLen += $seg.len
        if ($seg.mark -eq 'rise') {
            if (($pts[$pts.Count - 1][0] -ne $cur[0]) -or ($pts[$pts.Count - 1][2] -ne $cur[2])) { $pts.Add($cur) }
            $cur = Add3 $cur $UP $seg.len; $pts.Add($cur); continue
        }
        $cur = Add3 $cur $E $seg.len
        $m = $seg.mark
        if ($m -eq 'drain' -or $m -eq 'vent') {
            $sgn = $(if ($m -eq 'drain') { -1 } else { 1 })
            $b.Run((D ($cur + (Add3 $cur $UP ($sgn * 330)))), $T.od, $T.br)
            $b.Valve((Add3 $cur $UP ($sgn * ($r + 120))), $UP, (D 0, 1, 0), $T.od, $T.vl)
            $b.Cap((Add3 $cur $UP ($sgn * 335)), (D 0, 0, $sgn), $T.od)
        } elseif ($m -eq 'support') {
            $supports += , $cur
            $w = [Math]::Max(100, $S.od + 40); $sh = $(if ($S.od -gt 100) { 100 } else { 60 })
            $top = $cur[2] - $r
            $b.Box('support', (D ($cur[0] - 50), (-$w / 2), ($top - $sh)), (D ($cur[0] + 50), ($w / 2), $top))
            $b.Box('steel', (D ($cur[0] - 75), -500, ($top - $sh - 150)), (D ($cur[0] + 75), 500, ($top - $sh)))
        } elseif ($m -like 'branch*') {
            $label = ($m -split ':', 2)[1]
            $branches += $label
            $f1 = $cur[2] + $r + 250
            $len = $f1 - $cur[2] + 2 * $BS.ft + $BS.vl + 400
            $b.Run((D ($cur + (Add3 $cur $UP $len))), $BS.od, $BS.br)
            $b.Flange((D $cur[0], 0, ($f1 + $BS.ft / 2)), $UP, $BS.fl, $BS.ft, $BS.od - 6)
            $b.Valve((D $cur[0], 0, ($f1 + $BS.ft + $BS.vl / 2)), $UP, (D 0, 1, 0), $BS.od, $BS.vl)
            $b.Flange((D $cur[0], 0, ($f1 + 1.5 * $BS.ft + $BS.vl)), $UP, $BS.fl, $BS.ft, $BS.od - 6)
        } elseif ($m -and $m -ne 'end') { throw "Unknown mark '$m'" }
    }
    # end: elbow down, 1 m stub towards the continuation sheet
    if (($pts[$pts.Count - 1][0] -ne $cur[0]) -or ($pts[$pts.Count - 1][2] -ne $cur[2])) { $pts.Add($cur) }
    $pts.Add((Add3 $cur $UP -1000))
    $flat = D @($pts | ForEach-Object { $_ })
    $b.Run($flat, $S.od, $S.br)

    $refs = @()
    if ($supports.Count -ge 2) {
        $a = $supports[0]; $z = $supports[$supports.Count - 1]
        $refs = @(
            [ordered]@{ label = "첫 서포트 아래 바닥 (E $([int]$a[0]))"; at = @($a[0], 0, 0) },
            [ordered]@{ label = "마지막 서포트 아래 바닥 (E $([int]$z[0]))"; at = @($z[0], 0, 0) })
    }
    return @{ refPoints = $refs; runLengthM = [Math]::Round($runLen / 1000, 2); branches = $branches }
}

$files = if ($Ids) { $Ids | ForEach-Object { Join-Path $root "lines\$_.json" } } else { Get-ChildItem (Join-Path $root 'lines\*.json') | ForEach-Object FullName }
New-Item -ItemType Directory -Force -Path (Join-Path $root 'models') | Out-Null

foreach ($f in $files) {
    $line = [System.IO.File]::ReadAllText($f, $utf8) | ConvertFrom-Json
    $id = [System.IO.Path]::GetFileNameWithoutExtension($f)
    $b = New-Object GlbBuilder
    $meta = @{ refPoints = @($line.refPoints); runLengthM = $null; branches = @() }
    if ($line.header) { $meta = Build-Header $b $line.header }
    foreach ($c in $line.components) {
        switch ($c.type) {
            'run'    { $b.Run((D $c.points), $c.od, $c.bendRadius) }
            'flange' { $b.Flange((D $c.at), (D $c.axis), $c.od, $c.thick, $c.bore) }
            'valve'  { $b.Valve((D $c.at), (D $c.axis), (D $c.stem), $c.pipeOd, $c.length) }
            'cap'    { $b.Cap((D $c.at), (D $c.axis), $c.od) }
            'box'    { $b.Box($c.material, (D $c.min), (D $c.max)) }
            default  { throw "Unknown component type '$($c.type)' in $f" }
        }
    }
    if ($line.refPoints) { $meta.refPoints = @($line.refPoints) }
    $b.Save((Join-Path $root "models\$id.glb"), $line.name, $false)
    $b.Save((Join-Path $root "models\$id-view.glb"), $line.name, $true)

    $out = [ordered]@{
        id = $id; name = $line.name; service = $line.service; confidence = $line.confidence
        notes = @($line.notes); refPoints = @($meta.refPoints); branches = @($meta.branches)
        runLengthM = $meta.runLengthM; bomPipeM = $line.bomPipeM
    }
    [System.IO.File]::WriteAllText((Join-Path $root "models\$id.json"), ($out | ConvertTo-Json -Depth 6), $utf8)
    $chk = if ($line.bomPipeM -and $meta.runLengthM) { " | run {0} m vs BOM {1} m" -f $meta.runLengthM, $line.bomPipeM } else { '' }
    Write-Output ("{0,-24} -> models\{1}.glb{2}" -f $line.name, $id, $chk)
}

# index of every built line
$all = Get-ChildItem (Join-Path $root 'models\*.json') | Where-Object { $_.Name -ne 'index.json' } |
    ForEach-Object { [System.IO.File]::ReadAllText($_.FullName, $utf8) | ConvertFrom-Json } |
    Sort-Object id | ForEach-Object { [ordered]@{ id = $_.id; name = $_.name; service = $_.service; confidence = $_.confidence } }
[System.IO.File]::WriteAllText((Join-Path $root 'models\index.json'), (ConvertTo-Json @($all) -Depth 4), $utf8)
