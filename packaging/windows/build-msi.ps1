param(
    [Parameter(Mandatory = $true)][string]$Version,
    [Parameter(Mandatory = $true)][string]$PublishDirectory,
    [Parameter(Mandatory = $true)][string]$GtkRuntimeDirectory,
    [Parameter(Mandatory = $true)][string]$OutputDirectory
)

$ErrorActionPreference = "Stop"
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ("bandhub-msi-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Path $staging | Out-Null
try {
    Copy-Item (Join-Path $PublishDirectory "*") $staging -Recurse
    Copy-Item (Join-Path $GtkRuntimeDirectory "bin\*.dll") $staging
    New-Item -ItemType Directory -Path (Join-Path $staging "share\glib-2.0") -Force | Out-Null
    Copy-Item (Join-Path $GtkRuntimeDirectory "share\glib-2.0\schemas") `
        (Join-Path $staging "share\glib-2.0") -Recurse
    New-Item -ItemType Directory -Path (Join-Path $staging "lib\gdk-pixbuf-2.0") -Force | Out-Null
    Copy-Item (Join-Path $GtkRuntimeDirectory "lib\gdk-pixbuf-2.0\*") `
        (Join-Path $staging "lib\gdk-pixbuf-2.0") -Recurse

    $loaderDirectory = Get-ChildItem `
        (Join-Path $staging "lib\gdk-pixbuf-2.0") `
        -Directory -Recurse | Where-Object Name -eq "loaders" | Select-Object -First 1
    if ($null -eq $loaderDirectory) {
        throw "GTK gdk-pixbuf loader directory was not found."
    }
    $queryLoaders = Join-Path $GtkRuntimeDirectory "bin\gdk-pixbuf-query-loaders.exe"
    $loaderPaths = Get-ChildItem $loaderDirectory.FullName -Filter "*.dll" |
        ForEach-Object { [System.IO.Path]::GetRelativePath($staging, $_.FullName) }
    Push-Location $staging
    try {
        $loaderCache = & $queryLoaders @loaderPaths
    }
    finally {
        Pop-Location
    }
    $utf8WithoutBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllLines(
        (Join-Path $loaderDirectory.Parent.FullName "loaders.cache"),
        $loaderCache,
        $utf8WithoutBom)

    New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
    wix build `
        FirmwareUpdate/packaging/windows/Product.wxs `
        -arch x64 `
        -d ProductVersion=$Version `
        -d SourceDirectory=$staging `
        -o (Join-Path $OutputDirectory "BandHub-Firmware-Update-$Version.msi")
}
finally {
    Remove-Item $staging -Recurse -Force -ErrorAction SilentlyContinue
}
