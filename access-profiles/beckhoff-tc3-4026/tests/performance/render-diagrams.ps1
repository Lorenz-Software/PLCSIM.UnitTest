# Regenerate documentation SVGs from compact CSV evidence; no runtime access.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$culture = [Globalization.CultureInfo]::InvariantCulture
$root = (Resolve-Path "$PSScriptRoot/../../docs/performance").Path
$points = @(Import-Csv "$root/data/configuration-summary.csv")
$bins = @(Import-Csv "$root/data/read-size-summary.csv")
function Number($value) { [double]::Parse($value, $culture) }
function Node($name, $attributes, $content = '') {
    $node = $doc.CreateElement($name, $doc.DocumentElement.NamespaceURI)
    foreach ($key in $attributes.Keys) { $node.SetAttribute($key, [Convert]::ToString($attributes[$key], $culture)) }
    if ($content) { $node.InnerText = $content }
    [void]$doc.DocumentElement.AppendChild($node)
}
function Label($x, $y, $content, $size = 13, $anchor = 'start') {
    Node 'text' @{x=$x; y=$y; 'font-family'='Arial, sans-serif'; 'font-size'=$size; 'text-anchor'=$anchor; fill='#333333'} $content
}
function Canvas($title, $subtitle) {
    Node 'title' @{} $title
    Node 'rect' @{width=1040; height=650; fill='white'}
    Label 45 35 $title 22
    Label 45 61 $subtitle
}
foreach ($sweep in 'monitoring','batch') {
$doc = [xml]'<svg xmlns="http://www.w3.org/2000/svg" width="1040" height="650" viewBox="0 0 1040 650"/>'
if ($sweep -eq 'monitoring') {
    $ordered = @($points | Where-Object { [int]$_.batchLimit -eq 32 } | Sort-Object { [int]$_.monitoringMs })
    $field='monitoringMs'; $reference=10
    $axis='Monitoring interval (ms)'
    Canvas 'Observed backlog versus monitoring interval' 'Fixed: batch limit 32, drain 1 ms, PLC task 1 ms. 10 measured runs per setting.'
} else {
    $ordered = @($points | Where-Object { [int]$_.monitoringMs -eq 10 } | Sort-Object { [int]$_.batchLimit })
    $field='batchLimit'; $reference=32
    $axis='Configured batch limit (events)'
    Canvas 'Observed backlog versus batch limit' 'Fixed: monitoring 10 ms, drain 1 ms, PLC task 1 ms. 10 measured runs per setting.'
}
$left=80; $top=100; $height=390; $width=900; $ymax=270
foreach ($tick in 0,50,100,150,200,250) {
    $y=$top+$height-$tick*$height/$ymax
    Node 'line' @{x1=$left; y1=$y; x2=$left+$width; y2=$y; stroke='#dddddd'}
    Label ($left-10) ($y+4) "$tick" 12 'end'
}
$y=$top+$height-255*$height/$ymax
Node 'line' @{x1=$left; y1=$y; x2=$left+$width; y2=$y; stroke='#b84940'; 'stroke-dasharray'='6 4'}
Label ($left+$width) ($y-8) 'Capacity 255' 12 'end'
for ($i=0; $i -lt $ordered.Count; $i++) {
    $p=$ordered[$i]; $x=$left+($i+.5)*$width/$ordered.Count
    $max=Number $p.maxBacklog; $mean=Number $p.meanRunMaxBacklog
    Node 'rect' @{x=$x-28; y=$top+$height-$max*$height/$ymax; width=56; height=$max*$height/$ymax; fill='#16798b'}
    Node 'line' @{x1=$x-31; x2=$x+31; y1=$top+$height-$mean*$height/$ymax; y2=$top+$height-$mean*$height/$ymax; stroke='#efb849'; 'stroke-width'=4}
    Label $x ($top+$height-$max*$height/$ymax-9) "$max" 13 'middle'
    Label $x 518 "$($p.$field)" 13 'middle'
    if ([int]$p.$field -eq $reference) { Label $x 535 'default' 11 'middle' }
}
Label 530 560 $axis 14 'middle'
Label 45 585 'Bars: worst observed backlog. Gold markers: mean of per-run maxima.'
Label 45 611 $(if ($sweep -eq 'batch') {'Sampled peaks, not guaranteed limits. Batch 1 reached a PLC timeout and is excluded.'} else {'Sampled peaks, not guaranteed limits. Polling + Sum Read; identical workload at every setting.'})
$doc.Save("$root/diagrams/capacity-$sweep.svg")
}

$doc = [xml]'<svg xmlns="http://www.w3.org/2000/svg" width="1040" height="650" viewBox="0 0 1040 650"/>'
Canvas 'TwinCAT polling + Sum Read: observed batch costs' '90 measured runs, 9 configurations; 1 ms PLC task. Points: means; crosses: P95.'
$panel=0
foreach ($metric in 'payload','capture') {
    $left=75+500*$panel; $top=110; $width=410; $height=380
    $ymax=if ($metric -eq 'payload') {15} else {50}
    $title=if ($metric -eq 'payload') {'Payload read + decode (ms)'} else {'Capture through watermark write (ms)'}
    Label $left 92 $title 17
    foreach ($tick in 0..5) {
        $y=$top+$height-$tick*$height/5
        Node 'line' @{x1=$left; y1=$y; x2=$left+$width; y2=$y; stroke='#dddddd'}
        Label ($left-10) ($y+4) ($tick*$ymax/5).ToString($culture) 12 'end'
    }
    foreach ($tick in 0,16,32,48,64,80) { Label ($left+$tick*$width/80) 512 "$tick" 12 'middle' }
    foreach ($b in $bins) {
        $x=$left+(Number $b.events)*$width/80
        $color=if ([int]$b.samples -ge 20) {'#16798b'} else {'#999999'}
        $mean=Number $b."${metric}MeanMs"; $p95=Number $b."${metric}P95Ms"
        if ($mean -gt $ymax -or $p95 -gt $ymax -or [int]$b.events -gt 80) { throw 'Chart range exceeded' }
        $y=$top+$height-$mean*$height/$ymax; $yp=$top+$height-$p95*$height/$ymax
        Node 'circle' @{cx=$x; cy=$y; r=3; fill=$color}
        Node 'line' @{x1=$x-3; y1=$yp; x2=$x+3; y2=$yp; stroke=$color}
        Node 'line' @{x1=$x; y1=$yp-3; x2=$x; y2=$yp+3; stroke=$color}
    }
    Label ($left+$width/2) 540 'Actual events read' 14 'middle'
    $panel++
}
Label 45 580 'Teal: at least 20 samples per size; grey: sparse bins. Samples within runs are correlated.'
Label 45 605 'Natural batches, not controlled size probes. No extrapolation to 128/255-event reads.'
$doc.Save("$root/diagrams/observed-batch-cost.svg")
Write-Output 'Regenerated three performance diagrams from retained CSV summaries.'
