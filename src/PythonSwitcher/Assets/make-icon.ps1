# Erzeugt PythonSwitcher.ico (Symbol "Weiche": ein Gleis teilt sich, der aktive Zweig ist gelb).
# Aufruf: powershell -NoProfile -ExecutionPolicy Bypass -File make-icon.ps1 [-Vorschau <Ordner>]
# Enthaelt PNG-Bilder in 16-256 Pixel; bei kleinen Groessen werden Linien und Punkte etwas kraeftiger.
param([string]$Vorschau)

Add-Type -AssemblyName System.Drawing
$ziel = Join-Path $PSScriptRoot "PythonSwitcher.ico"
$groessen = 16, 20, 24, 32, 40, 48, 64, 128, 256

$blau = [Drawing.Color]::FromArgb(255, 0x2F, 0x6C, 0xA6)
$gelb = [Drawing.Color]::FromArgb(255, 0xFF, 0xD4, 0x3B)
# Inaktiver Zweig: deckende Mischfarbe (Weiss zu 35 % auf Blau) – Transparenz wuerde an Ueberlappungen heller werden
$weissBlass = [Drawing.Color]::FromArgb(255, 0x78, 0xA0, 0xC5)
$weiss = [Drawing.Color]::White

function Zeichne([int]$s) {
    $bmp = New-Object Drawing.Bitmap $s, $s, ([Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.PixelOffsetMode = [Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([Drawing.Color]::Transparent)

    # Entwurf ist auf 150 Einheiten angelegt
    $k = $s / 150.0
    function P([double]$x, [double]$y) { New-Object Drawing.PointF ([float]($x * $k)), ([float]($y * $k)) }

    # Abgerundetes Quadrat
    $r = 30 * $k * 2
    $pfad = New-Object Drawing.Drawing2D.GraphicsPath
    $pfad.AddArc(0, 0, $r, $r, 180, 90)
    $pfad.AddArc($s - $r - 0.01, 0, $r, $r, 270, 90)
    $pfad.AddArc($s - $r - 0.01, $s - $r - 0.01, $r, $r, 0, 90)
    $pfad.AddArc(0, $s - $r - 0.01, $r, $r, 90, 90)
    $pfad.CloseFigure()
    $g.FillPath((New-Object Drawing.SolidBrush $blau), $pfad)

    # Linienstaerke: 12/150 des Symbols, bei kleinen Groessen mindestens gut sichtbar
    $dicke = [Math]::Max(12 * $k, [Math]::Min(2.4, $s / 8.0))
    if ($s -le 24) { $dicke = $s / 7.0 }
    $punkt = [Math]::Max(9 * $k, $dicke * 0.75)

    function Stift($farbe) {
        $p = New-Object Drawing.Pen $farbe, ([float]$dicke)
        $p.StartCap = [Drawing.Drawing2D.LineCap]::Round
        $p.EndCap = [Drawing.Drawing2D.LineCap]::Round
        return $p
    }

    # Stamm (unten) und beide Zweige; der linke blass (inaktiv), der rechte gelb (aktiv)
    $g.DrawLine((Stift $weiss), (P 75 128), (P 75 82))
    $g.DrawBezier((Stift $weissBlass), (P 75 82), (P 75 55), (P 47 48), (P 39 24))
    $g.DrawBezier((Stift $gelb), (P 75 82), (P 75 55), (P 103 48), (P 111 24))
    if ($s -ge 32) {
        foreach ($e in @(@(39, $weissBlass), @(111, $gelb))) {
            $c = P $e[0] 24
            $g.FillEllipse((New-Object Drawing.SolidBrush $e[1]), $c.X - $punkt, $c.Y - $punkt, 2 * $punkt, 2 * $punkt)
        }
    }
    $g.Dispose()
    return $bmp
}

# PNG-Daten je Groesse sammeln
$bilder = foreach ($s in $groessen) {
    $bmp = Zeichne $s
    $ms = New-Object IO.MemoryStream
    $bmp.Save($ms, [Drawing.Imaging.ImageFormat]::Png)
    if ($Vorschau) { New-Item -ItemType Directory -Force $Vorschau | Out-Null; $bmp.Save((Join-Path $Vorschau "icon-$s.png")) }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $s; Data = $ms.ToArray() }
}

# ICO-Datei schreiben: Kopf, Verzeichnis, dann die PNG-Daten
$fs = [IO.File]::Create($ziel)
$w = New-Object IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$bilder.Count)
$offset = 6 + 16 * $bilder.Count
foreach ($b in $bilder) {
    $dim = if ($b.Size -ge 256) { 0 } else { $b.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$b.Data.Length); $w.Write([uint32]$offset)
    $offset += $b.Data.Length
}
foreach ($b in $bilder) { $w.Write($b.Data) }
$w.Close()
"Geschrieben: $ziel ($((Get-Item $ziel).Length) Bytes, $($bilder.Count) Groessen)"
