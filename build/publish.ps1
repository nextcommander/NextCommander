<#
.SYNOPSIS
  Builds a self-contained single-file NextCommander and packs it for one platform.

.EXAMPLE
  ./build/publish.ps1 -Rid win-x64           # dist/NextCommander-<ver>-win-x64.zip, a portable zip, .msix and a setup program
                                             # (-setup.exe, Inno Setup); signed when $env:MSIX_CERT (.pfx) [+ $env:MSIX_CERT_PASSWORD]
                                             # is set or the user's store has a CN=NextCommander code signing certificate
  ./build/publish.ps1 -Rid linux-x64         # dist/NextCommander-<ver>-linux-x64.tar.gz (+ AppImage if appimagetool exists)
  ./build/publish.ps1 -Rid osx-arm64         # dist/NextCommander-<ver>-osx-arm64.zip with NextCommander.app
                                             # signed when $env:MACOS_SIGN_IDENTITY is set; on macOS also a .dmg,
                                             # notarized when $env:MACOS_NOTARY_PROFILE (notarytool keychain profile) is set
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')][string]$Rid,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$version = ([xml](Get-Content "$root/Directory.Build.props")).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
$out = Join-Path $root "artifacts/publish/$Rid"
$dist = Join-Path $root 'dist'
$name = "NextCommander-$version-$Rid"
Remove-Item -Recurse -Force $out -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null

dotnet publish "$root/src/NextCommander.App" -c $Configuration -r $Rid --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o $out
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

# Zip of a folder's contents. Compress-Archive (Windows PowerShell 5.1) opens files without sharing and fails when a
# virus scanner or file watcher is reading a freshly written file; ZipFile shares reading and is retried a few times.
function New-Zip([string]$Folder, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    for ($attempt = 1; ; $attempt++) {
        try {
            Remove-Item $Destination -ErrorAction SilentlyContinue
            [IO.Compression.ZipFile]::CreateFromDirectory($Folder, $Destination, [IO.Compression.CompressionLevel]::Optimal, $false)
            return
        }
        catch {
            if ($attempt -ge 5) { throw }
            Start-Sleep -Seconds 2
        }
    }
}

# Download of a build tool package. Windows PowerShell 5.1 may not offer TLS 1.2 by itself, and some networks only let
# curl.exe through (it takes the system's proxy settings the same way as a browser): both are tried.
function Get-Download([string]$Uri, [string]$Destination) {
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest $Uri -OutFile $Destination -UseBasicParsing
    }
    catch {
        if (-not (Get-Command curl.exe -ErrorAction SilentlyContinue)) { throw }
        & curl.exe -sSfL -o $Destination $Uri
        if ($LASTEXITCODE -ne 0) { throw "download failed: $Uri" }
    }
}

switch -Wildcard ($Rid) {
    'win-*' {
        New-Zip $out "$dist/$name.zip"
        # Portable variant: settings next to the program (portable.json).
        '{}' | Set-Content -Encoding utf8 "$out/portable.json"
        New-Zip $out "$dist/$name-portable.zip"
        Remove-Item "$out/portable.json"

        # MSIX: makeappx.exe / signtool.exe from an installed Windows SDK, otherwise from the NuGet package
        # Microsoft.Windows.SDK.BuildTools (downloaded once into artifacts/tools; no installation, no administrator rights)
        $sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}/Windows Kits/10/bin/*/x64" -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending | Select-Object -First 1
        if (-not (Get-Command makeappx.exe -ErrorAction SilentlyContinue) -and -not ($sdkBin -and (Test-Path "$sdkBin/makeappx.exe"))) {
            $toolsVersion = '10.0.28000.2705'
            $tools = Join-Path $root "artifacts/tools/sdk-buildtools-$toolsVersion"
            if (-not (Test-Path $tools)) {
                Write-Host "Windows SDK not installed: downloading Microsoft.Windows.SDK.BuildTools $toolsVersion (makeappx, signtool)..."
                $package = Join-Path $root "artifacts/tools/sdk-buildtools.nupkg"
                New-Item -ItemType Directory -Force (Split-Path $package) | Out-Null
                try {
                    Get-Download "https://www.nuget.org/api/v2/package/Microsoft.Windows.SDK.BuildTools/$toolsVersion" $package
                    Add-Type -AssemblyName System.IO.Compression.FileSystem
                    [IO.Compression.ZipFile]::ExtractToDirectory($package, $tools)
                }
                catch {
                    Write-Host "Could not download the SDK build tools: $($_.Exception.Message)"
                }
                Remove-Item $package -ErrorAction SilentlyContinue
            }
            $sdkBin = Get-ChildItem "$tools/bin/*/x64" -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path "$_/makeappx.exe" } | Sort-Object Name -Descending | Select-Object -First 1
        }
        $makeappx = if (Get-Command makeappx.exe -ErrorAction SilentlyContinue) { 'makeappx.exe' } elseif ($sdkBin -and (Test-Path "$sdkBin/makeappx.exe")) { "$sdkBin/makeappx.exe" }
        if ($makeappx) {
            $layout = Join-Path $root "artifacts/msix-$Rid"
            Remove-Item -Recurse -Force $layout -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force "$layout/Assets" | Out-Null
            Copy-Item "$out/*" $layout -Recurse
            Copy-Item "$PSScriptRoot/windows/Assets/*" "$layout/Assets"
            $publisher = if ($env:MSIX_PUBLISHER) { $env:MSIX_PUBLISHER } else { 'CN=NextCommander' }
            $arch = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
            $msixVersion = (($version -split '[-+]')[0] + '.0.0.0').Split('.')[0..3] -join '.'
            (Get-Content "$PSScriptRoot/windows/AppxManifest.xml" -Raw).Replace('$VERSION', $msixVersion).Replace('$ARCH', $arch).Replace('$PUBLISHER', $publisher) |
                Set-Content -Encoding utf8 "$layout/AppxManifest.xml"
            & $makeappx pack /o /d $layout /p "$dist/$name.msix"
            if ($LASTEXITCODE -ne 0) { throw "makeappx failed" }
            # signer: a .pfx (MSIX_CERT [+ MSIX_CERT_PASSWORD]), otherwise a code signing certificate of the user's store whose
            # subject is the publisher (made by build/windows/new-msix-certificate.ps1)
            $storeCert = if (-not $env:MSIX_CERT) {
                Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq $publisher -and $_.NotAfter -gt (Get-Date) } |
                    Sort-Object NotAfter -Descending | Select-Object -First 1
            }
            if ($env:MSIX_CERT -or $storeCert) {
                $signtool = if (Get-Command signtool.exe -ErrorAction SilentlyContinue) { 'signtool.exe' } else { "$sdkBin/signtool.exe" }
                $signArgs = if ($storeCert) { @('sign', '/fd', 'SHA256', '/sha1', $storeCert.Thumbprint, '/s', 'My') } else { @('sign', '/fd', 'SHA256', '/f', $env:MSIX_CERT) }
                if ($env:MSIX_CERT_PASSWORD -and -not $storeCert) { $signArgs += @('/p', $env:MSIX_CERT_PASSWORD) }
                & $signtool @signArgs "$dist/$name.msix"
                if ($LASTEXITCODE -ne 0) { throw "signtool failed" }
                Write-Host "MSIX signed by $(if ($storeCert) { "$($storeCert.Subject) ($($storeCert.Thumbprint))" } else { $env:MSIX_CERT })"
            }
            else {
                Write-Host "MSIX is unsigned: install needs a trusted certificate (build/windows/new-msix-certificate.ps1, or MSIX_CERT / MSIX_PUBLISHER)."
            }
        }
        else {
            Write-Host "makeappx.exe not found (Windows SDK): skipping MSIX."
        }

        # Setup program (Inno Setup): ISCC.exe of an installed Inno Setup 6, otherwise from the NuGet package
        # Tools.InnoSetup (downloaded once into artifacts/tools; no installation)
        $innoVersion = '6.7.1'
        $iscc = @("${env:ProgramFiles(x86)}/Inno Setup 6/ISCC.exe", "$env:ProgramFiles/Inno Setup 6/ISCC.exe", "$env:LOCALAPPDATA/Programs/Inno Setup 6/ISCC.exe") |
            Where-Object { Test-Path $_ } | Select-Object -First 1
        if (-not $iscc) {
            $innoTools = Join-Path $root "artifacts/tools/innosetup-$innoVersion"
            if (-not (Test-Path "$innoTools/tools/ISCC.exe")) {
                Write-Host "Inno Setup not installed: downloading Tools.InnoSetup $innoVersion..."
                $package = Join-Path $root "artifacts/tools/innosetup.nupkg"
                New-Item -ItemType Directory -Force (Split-Path $package) | Out-Null
                try {
                    Get-Download "https://www.nuget.org/api/v2/package/Tools.InnoSetup/$innoVersion" $package
                    Add-Type -AssemblyName System.IO.Compression.FileSystem
                    Remove-Item -Recurse -Force $innoTools -ErrorAction SilentlyContinue
                    [IO.Compression.ZipFile]::ExtractToDirectory($package, $innoTools)
                }
                catch {
                    Write-Host "Could not download Inno Setup: $($_.Exception.Message)"
                }
                Remove-Item $package -ErrorAction SilentlyContinue
            }
            if (Test-Path "$innoTools/tools/ISCC.exe") { $iscc = "$innoTools/tools/ISCC.exe" }
        }
        if ($iscc) {
            $setupArch = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
            & $iscc /Q "/DAppVersion=$version" "/DSourceDir=$out" "/DOutputDir=$dist" "/DOutputName=$name-setup" "/DArch=$setupArch" `
                "/DIconFile=$root/src/NextCommander.App/Assets/NextCommander.ico" "$PSScriptRoot/windows/NextCommander.iss"
            if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }
            # signed with the same certificate as the MSIX, when there is one
            $publisher = if ($env:MSIX_PUBLISHER) { $env:MSIX_PUBLISHER } else { 'CN=NextCommander' }
            $setupCert = if (-not $env:MSIX_CERT) {
                Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq $publisher -and $_.NotAfter -gt (Get-Date) } |
                    Sort-Object NotAfter -Descending | Select-Object -First 1
            }
            $signtool = if (Get-Command signtool.exe -ErrorAction SilentlyContinue) { 'signtool.exe' } elseif ($sdkBin -and (Test-Path "$sdkBin/signtool.exe")) { "$sdkBin/signtool.exe" }
            if ($signtool -and ($env:MSIX_CERT -or $setupCert)) {
                $signArgs = if ($setupCert) { @('sign', '/fd', 'SHA256', '/sha1', $setupCert.Thumbprint, '/s', 'My') } else { @('sign', '/fd', 'SHA256', '/f', $env:MSIX_CERT) }
                if ($env:MSIX_CERT_PASSWORD -and -not $setupCert) { $signArgs += @('/p', $env:MSIX_CERT_PASSWORD) }
                & $signtool @signArgs "$dist/$name-setup.exe"
                if ($LASTEXITCODE -ne 0) { throw "signtool failed (setup)" }
            }
            Write-Host "Setup: $dist/$name-setup.exe"
        }
        else {
            Write-Host "ISCC.exe not found (Inno Setup): skipping the setup program."
        }
    }
    'linux-*' {
        Copy-Item "$root/src/NextCommander.App/Assets/NextCommander.png" "$out/nextcommander.png"
        Copy-Item "$PSScriptRoot/linux/nextcommander.desktop" "$out/nextcommander.desktop"
        # icon theme sizes (copy icons/hicolor to ~/.local/share/icons or /usr/share/icons when installing by hand)
        foreach ($size in 16, 24, 32, 48, 64, 96, 128, 256, 512) {
            $iconDir = "$out/icons/hicolor/${size}x${size}/apps"
            New-Item -ItemType Directory -Force $iconDir | Out-Null
            Copy-Item "$PSScriptRoot/icons/png/$size.png" "$iconDir/nextcommander.png"
        }
        if (Test-Path "$PSScriptRoot/icons/nextcommander.svg") {
            New-Item -ItemType Directory -Force "$out/icons/hicolor/scalable/apps" | Out-Null
            Copy-Item "$PSScriptRoot/icons/nextcommander.svg" "$out/icons/hicolor/scalable/apps/nextcommander.svg"
        }
        tar -czf "$dist/$name.tar.gz" -C $out .
        if (Get-Command appimagetool -ErrorAction SilentlyContinue) {
            $appDir = Join-Path $root "artifacts/AppDir-$Rid"
            Remove-Item -Recurse -Force $appDir -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force "$appDir/usr/bin" | Out-Null
            Copy-Item "$out/*" "$appDir/usr/bin" -Recurse
            Copy-Item "$out/nextcommander.desktop", "$out/nextcommander.png" $appDir
            Copy-Item "$out/nextcommander.png" "$appDir/.DirIcon"
            New-Item -ItemType Directory -Force "$appDir/usr/share/icons" | Out-Null
            Copy-Item "$out/icons/hicolor" "$appDir/usr/share/icons" -Recurse
            "#!/bin/sh`nexec `"`$(dirname `"`$0`")/usr/bin/NextCommander`" `"`$@`"" | Set-Content -NoNewline "$appDir/AppRun"
            chmod +x "$appDir/AppRun" "$appDir/usr/bin/NextCommander"
            appimagetool $appDir "$dist/$name.AppImage"
        }
    }
    'osx-*' {
        $app = Join-Path $root "artifacts/$Rid/NextCommander.app"
        Remove-Item -Recurse -Force $app -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force "$app/Contents/MacOS", "$app/Contents/Resources" | Out-Null
        Copy-Item "$out/*" "$app/Contents/MacOS" -Recurse
        Copy-Item "$root/src/NextCommander.App/Assets/NextCommander.icns" "$app/Contents/Resources/NextCommander.icns"
        (Get-Content "$PSScriptRoot/macos/Info.plist" -Raw).Replace('$VERSION', $version) | Set-Content -NoNewline "$app/Contents/Info.plist"
        if ($env:MACOS_SIGN_IDENTITY) {
            codesign --force --deep --options runtime --timestamp --entitlements "$PSScriptRoot/macos/entitlements.plist" -s $env:MACOS_SIGN_IDENTITY $app
        }
        elseif (Get-Command codesign -ErrorAction SilentlyContinue) {
            # Ad-hoc signature of the whole bundle (Info.plist and resources sealed): without it a downloaded copy is
            # reported as "damaged" instead of "unidentified developer". Packed elsewhere only the program is signed.
            codesign --force --deep -s - $app
        }
        Push-Location (Split-Path $app)
        if (Get-Command hdiutil -ErrorAction SilentlyContinue) {
            # Disk image with the app and an Applications link to drag it into.
            $dmgDir = Join-Path $root "artifacts/dmg-$Rid"
            Remove-Item -Recurse -Force $dmgDir -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force $dmgDir | Out-Null
            ditto NextCommander.app "$dmgDir/NextCommander.app"
            ln -s /Applications "$dmgDir/Applications"
            hdiutil create -volname NextCommander -srcfolder $dmgDir -ov -format UDZO "$dist/$name.dmg"
            if ($LASTEXITCODE -ne 0) { throw "hdiutil failed" }
            if ($env:MACOS_SIGN_IDENTITY) { codesign --force --timestamp -s $env:MACOS_SIGN_IDENTITY "$dist/$name.dmg" }
            if ($env:MACOS_NOTARY_PROFILE) {
                xcrun notarytool submit "$dist/$name.dmg" --keychain-profile $env:MACOS_NOTARY_PROFILE --wait
                if ($LASTEXITCODE -ne 0) { throw "notarization failed" }
                xcrun stapler staple "$dist/$name.dmg"
            }
        }
        if (Get-Command ditto -ErrorAction SilentlyContinue) { ditto -c -k --keepParent NextCommander.app "$dist/$name.zip" }
        elseif (Get-Command python -ErrorAction SilentlyContinue) {
            # No ditto (packing on Windows / Linux): a zip with Unix modes, so the program stays executable.
            python "$PSScriptRoot/macos/zip_app.py" $app "$dist/$name.zip"
            if ($LASTEXITCODE -ne 0) { throw "zip_app.py failed" }
        }
        else {
            # Neither ditto nor Python: plain zip; Unix permissions are not kept (chmod +x needed after unpacking).
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            Remove-Item "$dist/$name.zip" -ErrorAction SilentlyContinue
            [IO.Compression.ZipFile]::CreateFromDirectory($app, "$dist/$name.zip", [IO.Compression.CompressionLevel]::Optimal, $true)
        }
        Pop-Location
    }
}
Write-Host "Done: $dist"
