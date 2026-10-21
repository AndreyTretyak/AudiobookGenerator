param(
    [string]$OutputPath
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $repoRoot = (Resolve-Path (Join-Path (Join-Path $PSScriptRoot '..') '..')).Path
    $OutputPath = Join-Path (Join-Path (Join-Path $repoRoot 'artifacts') 'smoke') 'portable-smoke.epub'
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$outputDirectory = Split-Path -Parent $OutputPath
if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

if (Test-Path $OutputPath) {
    Remove-Item -Force $OutputPath
}

$timestamp = [DateTimeOffset]::Parse('2024-01-01T00:00:00Z')
$utf8 = [System.Text.UTF8Encoding]::new($false)
$ascii = [System.Text.Encoding]::ASCII
$coverBytes = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAIAAAACCAIAAAD91JpzAAAACXBIWXMAAAABAAAAAQBPJcTWAAAAEElEQVR4nGP4y8AARAwQCgAfrgP19hgqWQAAAABJRU5ErkJggg==')

$entries = @(
    @{
        Path = 'mimetype'
        Compression = [System.IO.Compression.CompressionLevel]::NoCompression
        Bytes = $ascii.GetBytes('application/epub+zip')
    },
    @{
        Path = 'META-INF/container.xml'
        Compression = [System.IO.Compression.CompressionLevel]::Optimal
        Bytes = $utf8.GetBytes(@'
<?xml version="1.0"?>
<container version="1.0" xmlns="urn:oasis:names:tc:opendocument:xmlns:container">
  <rootfiles>
    <rootfile full-path="OEBPS/content.opf" media-type="application/oebps-package+xml"/>
  </rootfiles>
</container>
'@)
    },
    @{
        Path = 'OEBPS/content.opf'
        Compression = [System.IO.Compression.CompressionLevel]::Optimal
        Bytes = $utf8.GetBytes(@'
<?xml version="1.0" encoding="utf-8"?>
<package xmlns="http://www.idpf.org/2007/opf" version="2.0" unique-identifier="book-id">
  <metadata xmlns:dc="http://purl.org/dc/elements/1.1/">
    <dc:identifier id="book-id">portable-smoke-book</dc:identifier>
    <dc:title>Portable Smoke Book</dc:title>
    <dc:creator>Smoke Author</dc:creator>
    <dc:language>en</dc:language>
    <dc:description>Deterministic EPUB fixture for portable console smoke tests.</dc:description>
    <meta name="cover" content="cover"/>
  </metadata>
  <manifest>
    <item id="chapter" href="Text/chapter.xhtml" media-type="application/xhtml+xml"/>
    <item id="cover" href="Images/cover.png" media-type="image/png"/>
    <item id="ncx" href="toc.ncx" media-type="application/x-dtbncx+xml"/>
  </manifest>
  <spine toc="ncx">
    <itemref idref="chapter"/>
  </spine>
</package>
'@)
    },
    @{
        Path = 'OEBPS/toc.ncx'
        Compression = [System.IO.Compression.CompressionLevel]::Optimal
        Bytes = $utf8.GetBytes(@'
<?xml version="1.0" encoding="utf-8"?>
<ncx xmlns="http://www.daisy.org/z3986/2005/ncx/" version="2005-1">
  <head>
    <meta name="dtb:uid" content="portable-smoke-book"/>
  </head>
  <docTitle>
    <text>Portable Smoke Book</text>
  </docTitle>
  <navMap>
    <navPoint id="chapter-1" playOrder="1">
      <navLabel><text>Chapter One</text></navLabel>
      <content src="Text/chapter.xhtml"/>
    </navPoint>
  </navMap>
</ncx>
'@)
    },
    @{
        Path = 'OEBPS/Text/chapter.xhtml'
        Compression = [System.IO.Compression.CompressionLevel]::Optimal
        Bytes = $utf8.GetBytes(@'
<?xml version="1.0" encoding="utf-8"?>
<html xmlns="http://www.w3.org/1999/xhtml">
  <head><title>Chapter One</title></head>
  <body>
    <h1>Chapter One</h1>
    <p>This is a short deterministic fixture chapter.</p>
    <p><img src="../Images/cover.png" alt="Tiny smoke-test cover"/></p>
  </body>
</html>
'@)
    },
    @{
        Path = 'OEBPS/Images/cover.png'
        Compression = [System.IO.Compression.CompressionLevel]::Optimal
        Bytes = $coverBytes
    }
)

$stream = [System.IO.File]::Open($OutputPath, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
try {
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($entryDefinition in $entries) {
            $entry = $archive.CreateEntry($entryDefinition.Path, $entryDefinition.Compression)
            $entry.LastWriteTime = $timestamp
            $entryStream = $entry.Open()
            try {
                $entryBytes = $entryDefinition.Bytes
                $entryStream.Write($entryBytes, 0, $entryBytes.Length)
            }
            finally {
                $entryStream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}
finally {
    $stream.Dispose()
}

Write-Output $OutputPath
