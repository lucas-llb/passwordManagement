<#
.SYNOPSIS
    Derives every icon asset of the Windows app from the two art masters.

.DESCRIPTION
    Inputs are art/icon-master.png (the full symbol) and art/glyph-master.png (the same symbol simplified, for 16 to 32 px).
    Outputs are the 77 PNGs and AppIcon.ico of src/PasswordManager.App/Assets, overwritten on every run.

    The derivation is placement and resizing only, never drawing: each master is trimmed to the bounding box of its pixels with
    alpha above 8, fitted and centered on a transparent canvas, and resized with GDI+ HighQualityBicubic. Nothing recolors,
    thresholds or filters the art; a bad master goes back to the generator, not into this script.

    Every size is written out in the tables below, never computed, because [Math]::Round(62.5) is 62 and the Microsoft list says 63.
    The tables are the same list the E2E tests expect (tests/PasswordManager.App.E2E.Tests/Branding/AppAssetCatalog.cs).

.PARAMETER Preview
    Optional folder for contact sheets of the small sizes on a light (#F3F3F3) and a dark (#202020) taskbar, at 1x and 8x
    nearest-neighbor. Nothing else is written there, and it must not be inside the repository.

.PARAMETER MastersDir
    Folder holding icon-master.png and glyph-master.png. Defaults to this script's folder (art/).

.PARAMETER OutDir
    Folder that receives the assets. Defaults to src/PasswordManager.App/Assets.

.EXAMPLE
    pwsh -NoProfile -File art/Build-AppAssets.ps1

.EXAMPLE
    pwsh -NoProfile -File art/Build-AppAssets.ps1 -Preview $env:TEMP\w27-previews
#>
[CmdletBinding()]
param(
    [string]$Preview,
    [string]$MastersDir = $PSScriptRoot,
    [string]$OutDir = (Join-Path $PSScriptRoot '..\src\PasswordManager.App\Assets')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

# Small helpers that would be slow as PowerShell loops: the alpha bounding box and the ico container.
if (-not ('IconKit' -as [type])) {
    $referenced = @([System.Drawing.Bitmap].Assembly.Location, [System.Drawing.Rectangle].Assembly.Location)
    Add-Type -ReferencedAssemblies $referenced -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

public static class IconKit
{
    // The box of the pixels with an alpha above the threshold (right and bottom exclusive), or an empty rectangle.
    public static Rectangle AlphaBounds(Bitmap bitmap, int threshold)
    {
        int width = bitmap.Width, height = bitmap.Height;
        byte[] bgra = ReadBgra(bitmap);
        int left = width, top = height, right = -1, bottom = -1;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (bgra[((y * width + x) * 4) + 3] <= threshold) continue;
                if (x < left) left = x;
                if (x > right) right = x;
                if (y < top) top = y;
                if (y > bottom) bottom = y;
            }
        }
        return right < 0 ? Rectangle.Empty : Rectangle.FromLTRB(left, top, right + 1, bottom + 1);
    }

    static byte[] ReadBgra(Bitmap bitmap)
    {
        int width = bitmap.Width, height = bitmap.Height, rowBytes = width * 4;
        BitmapData data = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte[] bgra = new byte[rowBytes * height];
            for (int row = 0; row < height; row++)
                Marshal.Copy(IntPtr.Add(data.Scan0, row * data.Stride), bgra, row * rowBytes, rowBytes);
            return bgra;
        }
        finally { bitmap.UnlockBits(data); }
    }

    // An .ico: ICONDIR, one 16-byte entry per frame, then the frames. A frame below 256 is a 32-bit BGRA DIB
    // (BITMAPINFOHEADER with biHeight = 2N, rows bottom-up, then a 1-bit AND mask set where A = 0); the 256 frame is the PNG.
    public static byte[] BuildIco(int[] sizes, Bitmap[] bitmaps, byte[] png256)
    {
        int count = sizes.Length;
        byte[][] frames = new byte[count][];
        for (int i = 0; i < count; i++)
            frames[i] = sizes[i] == 256 ? png256 : BuildDib(bitmaps[i], sizes[i]);

        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)count);
            int offset = 6 + (16 * count);
            for (int i = 0; i < count; i++)
            {
                byte side = sizes[i] >= 256 ? (byte)0 : (byte)sizes[i];
                writer.Write(side);
                writer.Write(side);
                writer.Write((byte)0);
                writer.Write((byte)0);
                writer.Write((ushort)1);
                writer.Write((ushort)32);
                writer.Write(frames[i].Length);
                writer.Write(offset);
                offset += frames[i].Length;
            }
            for (int i = 0; i < count; i++) writer.Write(frames[i]);
            writer.Flush();
            return stream.ToArray();
        }
    }

    static byte[] BuildDib(Bitmap bitmap, int size)
    {
        byte[] bgra = ReadBgra(bitmap);
        int maskStride = ((size + 31) / 32) * 4;
        using (MemoryStream stream = new MemoryStream())
        using (BinaryWriter writer = new BinaryWriter(stream))
        {
            writer.Write(40);          // biSize
            writer.Write(size);        // biWidth
            writer.Write(size * 2);    // biHeight: image plus AND mask
            writer.Write((ushort)1);   // biPlanes
            writer.Write((ushort)32);  // biBitCount
            writer.Write(0);           // biCompression = BI_RGB
            writer.Write(0);           // biSizeImage
            writer.Write(0);           // biXPelsPerMeter
            writer.Write(0);           // biYPelsPerMeter
            writer.Write(0);           // biClrUsed
            writer.Write(0);           // biClrImportant
            for (int y = size - 1; y >= 0; y--)
                writer.Write(bgra, y * size * 4, size * 4);
            byte[] row = new byte[maskStride];
            for (int y = size - 1; y >= 0; y--)
            {
                Array.Clear(row, 0, row.Length);
                for (int x = 0; x < size; x++)
                    if (bgra[((y * size + x) * 4) + 3] == 0) row[x / 8] |= (byte)(0x80 >> (x % 8));
                writer.Write(row);
            }
            writer.Flush();
            return stream.ToArray();
        }
    }
}
'@
}

# ---- The tables: every name and size is written out ----

$SymbolAlpha = 8                       # a pixel is part of the symbol above this alpha
$GlyphMaxSize = 32                     # targetsize N up to this uses the glyph master, the rest the full icon master
$TargetSizes = 16, 20, 24, 30, 32, 36, 40, 48, 60, 64, 72, 80, 96, 256
$Altforms = '', '_altform-unplated', '_altform-lightunplated'
$TargetShare = 0.875

# Family, share of the canvas short side the symbol takes, then the (width, height) at scale 100, 125, 150, 200, 400.
$ScaleNames = 100, 125, 150, 200, 400
$ScaleFamilies = @(
    @{ Name = 'Square44x44Logo';    Share = 0.75; Sizes = @(@(44, 44), @(55, 55), @(66, 66), @(88, 88), @(176, 176)) }
    @{ Name = 'SmallTile';          Share = 0.60; Sizes = @(@(71, 71), @(89, 89), @(107, 107), @(142, 142), @(284, 284)) }
    @{ Name = 'Square150x150Logo';  Share = 0.60; Sizes = @(@(150, 150), @(188, 188), @(225, 225), @(300, 300), @(600, 600)) }
    @{ Name = 'Wide310x150Logo';    Share = 0.60; Sizes = @(@(310, 150), @(388, 188), @(465, 225), @(620, 300), @(1240, 600)) }
    @{ Name = 'LargeTile';          Share = 0.50; Sizes = @(@(310, 310), @(388, 388), @(465, 465), @(620, 620), @(1240, 1240)) }
    @{ Name = 'StoreLogo';          Share = 0.90; Sizes = @(@(50, 50), @(63, 63), @(75, 75), @(100, 100), @(200, 200)) }
    @{ Name = 'SplashScreen';       Share = 0.50; Sizes = @(@(620, 300), @(775, 375), @(930, 450), @(1240, 600), @(2480, 1200)) }
)

# ---- Masters ----

function Read-Master {
    param([string]$Name)

    $path = Join-Path $MastersDir $Name
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Master not found: $path"
    }

    $bytes = [System.IO.File]::ReadAllBytes($path)
    $stream = [System.IO.MemoryStream]::new($bytes)
    try {
        $loaded = [System.Drawing.Bitmap]::new($stream)
        try {
            if ($loaded.Width -ne $loaded.Height) {
                throw "$Name is $($loaded.Width)x$($loaded.Height): a master must be square."
            }
            if ($loaded.Width -lt 1024) {
                throw "$Name is $($loaded.Width) px wide: a master must be at least 1024."
            }

            $bounds = [IconKit]::AlphaBounds($loaded, $SymbolAlpha)
            if ($bounds.IsEmpty) {
                throw "$Name has no pixel with alpha above $SymbolAlpha."
            }

            # The symbol alone, in premultiplied alpha so the resize does not blend in the color of transparent pixels.
            $trimmed = [System.Drawing.Bitmap]::new($bounds.Width, $bounds.Height, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
            $trimmed.SetResolution(96, 96)
            $graphics = [System.Drawing.Graphics]::FromImage($trimmed)
            try {
                $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
                $graphics.DrawImage($loaded, [System.Drawing.Rectangle]::new(0, 0, $bounds.Width, $bounds.Height), $bounds, [System.Drawing.GraphicsUnit]::Pixel)
            }
            finally {
                $graphics.Dispose()
            }

            return $trimmed
        }
        finally {
            $loaded.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }
}

# ---- Placement and resize ----

# The trimmed symbol, scaled to fit a square of Share * the canvas short side, centered on a transparent Width x Height canvas.
function New-Asset {
    param(
        [System.Drawing.Bitmap]$Symbol,
        [int]$Width,
        [int]$Height,
        [double]$Share
    )

    $box = $Share * [Math]::Min($Width, $Height)
    $scale = $box / [Math]::Max($Symbol.Width, $Symbol.Height)
    $drawWidth = [Math]::Max(1, [int][Math]::Round($Symbol.Width * $scale, [MidpointRounding]::AwayFromZero))
    $drawHeight = [Math]::Max(1, [int][Math]::Round($Symbol.Height * $scale, [MidpointRounding]::AwayFromZero))
    $left = [int][Math]::Floor(($Width - $drawWidth) / 2)
    $top = [int][Math]::Floor(($Height - $drawHeight) / 2)

    $canvas = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppPArgb)
    $canvas.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($canvas)
    $attributes = [System.Drawing.Imaging.ImageAttributes]::new()
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceOver
        $graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
        $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
        $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
        $attributes.SetWrapMode([System.Drawing.Drawing2D.WrapMode]::TileFlipXY)
        $graphics.DrawImage(
            $Symbol,
            [System.Drawing.Rectangle]::new($left, $top, $drawWidth, $drawHeight),
            0, 0, $Symbol.Width, $Symbol.Height,
            [System.Drawing.GraphicsUnit]::Pixel,
            $attributes)
    }
    finally {
        $attributes.Dispose()
        $graphics.Dispose()
    }

    # Back to straight alpha, the format PNG and the ico store.
    $straight = [System.Drawing.Bitmap]::new($Width, $Height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $straight.SetResolution(96, 96)
    $graphics = [System.Drawing.Graphics]::FromImage($straight)
    try {
        $graphics.CompositingMode = [System.Drawing.Drawing2D.CompositingMode]::SourceCopy
        $graphics.DrawImageUnscaled($canvas, 0, 0)
    }
    finally {
        $graphics.Dispose()
        $canvas.Dispose()
    }

    return $straight
}

function Save-Png {
    param([System.Drawing.Bitmap]$Bitmap, [string]$Path)

    $Bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
}

# ---- Build ----

$assets = [System.IO.Path]::GetFullPath($OutDir)
$MastersDir = [System.IO.Path]::GetFullPath($MastersDir)
New-Item -ItemType Directory -Force -Path $assets | Out-Null

$icon = Read-Master 'icon-master.png'
$glyph = Read-Master 'glyph-master.png'

$written = 0
$targetBitmaps = @{}
try {
    # Square44x44Logo.targetsize-N, in the three altforms, are byte-identical copies of one bitmap per N.
    foreach ($size in $TargetSizes) {
        $symbol = if ($size -le $GlyphMaxSize) { $glyph } else { $icon }
        $bitmap = New-Asset -Symbol $symbol -Width $size -Height $size -Share $TargetShare
        $targetBitmaps[$size] = $bitmap

        $first = Join-Path $assets "Square44x44Logo.targetsize-$size.png"
        Save-Png -Bitmap $bitmap -Path $first
        $written++
        foreach ($altform in $Altforms | Where-Object { $_ -ne '' }) {
            Copy-Item -LiteralPath $first -Destination (Join-Path $assets "Square44x44Logo.targetsize-$size$altform.png") -Force
            $written++
        }
    }

    foreach ($family in $ScaleFamilies) {
        for ($i = 0; $i -lt $ScaleNames.Count; $i++) {
            $width = $family.Sizes[$i][0]
            $height = $family.Sizes[$i][1]
            $bitmap = New-Asset -Symbol $icon -Width $width -Height $height -Share $family.Share
            try {
                Save-Png -Bitmap $bitmap -Path (Join-Path $assets "$($family.Name).scale-$($ScaleNames[$i]).png")
                $written++
            }
            finally {
                $bitmap.Dispose()
            }
        }
    }

    # AppIcon.ico: the same 14 bitmaps as targetsize-N, in the same order.
    $frameBitmaps = [System.Drawing.Bitmap[]]($TargetSizes | ForEach-Object { $targetBitmaps[$_] })
    $png256 = [System.IO.File]::ReadAllBytes((Join-Path $assets 'Square44x44Logo.targetsize-256.png'))
    $ico = [IconKit]::BuildIco([int[]]$TargetSizes, $frameBitmaps, $png256)
    [System.IO.File]::WriteAllBytes((Join-Path $assets 'AppIcon.ico'), $ico)
    $written++

    # ---- Preview sheets: the small sizes on both taskbars at 1x and 8x, outside the repository ----
    if ($Preview) {
        $previewDirectory = [System.IO.Path]::GetFullPath($Preview)
        New-Item -ItemType Directory -Force -Path $previewDirectory | Out-Null

        $previewSizes = 16, 20, 24, 30, 32, 36, 40, 48
        $backdrops = [ordered]@{
            light = [System.Drawing.Color]::FromArgb(255, 0xF3, 0xF3, 0xF3)
            dark  = [System.Drawing.Color]::FromArgb(255, 0x20, 0x20, 0x20)
        }

        foreach ($zoom in 1, 8) {
            $gap = 8 * $zoom
            $width = $gap
            foreach ($size in $previewSizes) { $width += ($size * $zoom) + $gap }
            $height = (48 * $zoom) + (2 * $gap)

            foreach ($name in $backdrops.Keys) {
                $sheet = [System.Drawing.Bitmap]::new($width, $height, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
                $graphics = [System.Drawing.Graphics]::FromImage($sheet)
                try {
                    $graphics.Clear($backdrops[$name])
                    $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::NearestNeighbor
                    $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::Half
                    $x = $gap
                    foreach ($size in $previewSizes) {
                        $y = $gap + [int][Math]::Floor(((48 - $size) * $zoom) / 2)
                        $graphics.DrawImage(
                            $targetBitmaps[$size],
                            [System.Drawing.Rectangle]::new($x, $y, $size * $zoom, $size * $zoom),
                            0, 0, $size, $size,
                            [System.Drawing.GraphicsUnit]::Pixel)
                        $x += ($size * $zoom) + $gap
                    }
                }
                finally {
                    $graphics.Dispose()
                }

                Save-Png -Bitmap $sheet -Path (Join-Path $previewDirectory "preview-$name-${zoom}x.png")
                $sheet.Dispose()
            }
        }

        Write-Host "Preview sheets (sizes $($previewSizes -join ', ') left to right): $previewDirectory"
    }
}
finally {
    foreach ($bitmap in $targetBitmaps.Values) { $bitmap.Dispose() }
    $icon.Dispose()
    $glyph.Dispose()
}

Write-Host "Wrote $written files to $assets"
