<#
.SYNOPSIS
  Builds a self-contained single-file NextCommander and packs it for one platform.

.EXAMPLE
  ./build/publish.ps1 -Rid win-x64           # dist/NextCommander-<ver>-win-x64.zip, a portable zip and a setup program
                                             # (-setup.exe, Inno Setup); signed when $env:SIGN_CERT (.pfx) [+ $env:SIGN_CERT_PASSWORD]
                                             # is set or the user's store has a CN=NextCommander code signing certificate
  ./build/publish.ps1 -Rid linux-x64         # dist/NextCommander-<ver>-linux-x64.tar.gz, .deb and .rpm (nfpm)
                                             # (+ AppImage if appimagetool exists)
  ./build/publish.ps1 -Rid osx-arm64         # dist/NextCommander-<ver>-osx-arm64/NextCommander.app and a .zip of it
                                             # signed when $env:MACOS_SIGN_IDENTITY is set; on macOS also a .dmg,
                                             # notarized when $env:MACOS_NOTARY_PROFILE (notarytool keychain profile) is set
#>
param(
    [Parameter(Mandatory = $true)][ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')][string]$Rid,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# YY.MM.NNNN: year and month of the commit, NNNN its number within the month from 0000 (NcVersion target in
# Directory.Build.props). $version keeps the month with two digits (file names, setup); $numericVersion has no
# leading zero (deb / rpm and macOS bundle versions).
$versionJson = (dotnet msbuild "$root/src/NextCommander.App" -nologo -t:NcVersion -getProperty:Version -getProperty:NcNumericVersion | Out-String)
if ($LASTEXITCODE -ne 0) { throw "Could not read the version: $versionJson" }
$versionProps = ($versionJson | ConvertFrom-Json).Properties
$version = $versionProps.Version
$numericVersion = $versionProps.NcNumericVersion
if ($version -notmatch '^\d\d\.\d\d\.\d+$' -or $numericVersion -notmatch '^\d+\.\d+\.\d+$') { throw "Could not read the version: $versionJson" }
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
        $curl = if (Get-Command curl.exe -ErrorAction SilentlyContinue) { 'curl.exe' } elseif (Get-Command curl -CommandType Application -ErrorAction SilentlyContinue) { 'curl' }
        if (-not $curl) { throw }
        & $curl -sSfL -o $Destination $Uri
        if ($LASTEXITCODE -ne 0) { throw "download failed: $Uri" }
    }
}

# The machine the script runs on: Windows PowerShell 5.1 has no $IsWindows / $IsMacOS.
$hostWindows = $env:OS -eq 'Windows_NT'
$hostMac = -not $hostWindows -and (uname) -eq 'Darwin'
$hostArm = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq [System.Runtime.InteropServices.Architecture]::Arm64

# nfpm (deb and rpm without dpkg-deb / rpmbuild, on every system): an installed nfpm, otherwise the release of
# goreleaser/nfpm for this machine (downloaded once into artifacts/tools, checked against the release's checksums.txt).
function Get-Nfpm {
    if (Get-Command nfpm -ErrorAction SilentlyContinue) { return 'nfpm' }
    $nfpmVersion = '2.48.0'
    $os = if ($hostWindows) { 'Windows' } elseif ($hostMac) { 'Darwin' } else { 'Linux' }
    $arch = if ($hostArm) { 'arm64' } else { 'x86_64' }
    $archive = if ($hostWindows) { "nfpm_${nfpmVersion}_${os}_${arch}.zip" } else { "nfpm_${nfpmVersion}_${os}_${arch}.tar.gz" }
    $tools = Join-Path $root "artifacts/tools/nfpm-$nfpmVersion-$os-$arch"
    $exe = Join-Path $tools $(if ($hostWindows) { 'nfpm.exe' } else { 'nfpm' })
    if (Test-Path $exe) { return $exe }
    Write-Host "nfpm not installed: downloading nfpm $nfpmVersion ($archive)..."
    $release = "https://github.com/goreleaser/nfpm/releases/download/v$nfpmVersion"
    $download = Join-Path $root "artifacts/tools/$archive"
    $sums = Join-Path $root "artifacts/tools/nfpm-$nfpmVersion-checksums.txt"
    New-Item -ItemType Directory -Force $tools | Out-Null
    try {
        Get-Download "$release/$archive" $download
        Get-Download "$release/checksums.txt" $sums
        $expected = (Get-Content $sums | Where-Object { $_ -match "\s$([regex]::Escape($archive))$" } | Select-Object -First 1) -split '\s+' | Select-Object -First 1
        $actual = (Get-FileHash $download -Algorithm SHA256).Hash
        if (-not $expected -or $actual -ne $expected.ToUpperInvariant()) { throw "checksum of $archive does not match" }
        if ($hostWindows) {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
            [IO.Compression.ZipFile]::ExtractToDirectory($download, $tools)
        }
        else {
            tar -xzf $download -C $tools
            chmod +x $exe
        }
    }
    catch {
        Write-Host "Could not get nfpm: $($_.Exception.Message)"
        Remove-Item -Recurse -Force $tools -ErrorAction SilentlyContinue
    }
    Remove-Item $download, $sums -ErrorAction SilentlyContinue
    if (Test-Path $exe) { return $exe }
}

switch -Wildcard ($Rid) {
    'win-*' {
        New-Zip $out "$dist/$name.zip"
        # Portable variant: settings next to the program (portable.json).
        '{}' | Set-Content -Encoding utf8 "$out/portable.json"
        New-Zip $out "$dist/$name-portable.zip"
        Remove-Item "$out/portable.json"

        # Setup program (Inno Setup 7): ISCC.exe of an installed Inno Setup 7 (64-bit edition first), otherwise from the
        # NuGet package Tools.InnoSetup (downloaded once into artifacts/tools; no installation). Inno Setup 6 cannot
        # compile the script (SetupArchitecture, WizardStyle "dynamic").
        $innoVersion = '7.1.0'
        $iscc = @("$env:ProgramFiles/Inno Setup 7/ISCC.exe", "${env:ProgramFiles(x86)}/Inno Setup 7/ISCC.exe", "$env:LOCALAPPDATA/Programs/Inno Setup 7/ISCC.exe") |
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
        if (-not $iscc) {
            Write-Host "ISCC.exe not found (Inno Setup): skipping the setup program."
            break
        }
        $setupArch = if ($Rid -eq 'win-arm64') { 'arm64' } else { 'x64' }
        # Inno Setup 7 writes its warnings to stderr: Windows PowerShell 5.1 must not take them as a failure
        $previous = $ErrorActionPreference
        $ErrorActionPreference = 'Continue'
        & $iscc -q "-DAppVersion=$version" "-DSourceDir=$out" "-DOutputDir=$dist" "-DOutputName=$name-setup" "-DArch=$setupArch" `
            "-DIconFile=$root/src/NextCommander.App/Assets/NextCommander.ico" "$PSScriptRoot/windows/NextCommander.iss"
        $ErrorActionPreference = $previous
        if ($LASTEXITCODE -ne 0) { throw "ISCC failed" }

        # Signature of the setup: a .pfx (SIGN_CERT [+ SIGN_CERT_PASSWORD]), otherwise a code signing certificate of the
        # user's store whose subject is SIGN_PUBLISHER (default CN=NextCommander, made by
        # build/windows/new-signing-certificate.ps1). The former names MSIX_CERT / MSIX_CERT_PASSWORD / MSIX_PUBLISHER work too.
        $pfx = if ($env:SIGN_CERT) { $env:SIGN_CERT } else { $env:MSIX_CERT }
        $pfxPassword = if ($env:SIGN_CERT_PASSWORD) { $env:SIGN_CERT_PASSWORD } else { $env:MSIX_CERT_PASSWORD }
        $publisher = if ($env:SIGN_PUBLISHER) { $env:SIGN_PUBLISHER } elseif ($env:MSIX_PUBLISHER) { $env:MSIX_PUBLISHER } else { 'CN=NextCommander' }
        $storeCert = if (-not $pfx) {
            Get-ChildItem Cert:\CurrentUser\My -CodeSigningCert -ErrorAction SilentlyContinue | Where-Object { $_.Subject -eq $publisher -and $_.NotAfter -gt (Get-Date) } |
                Sort-Object NotAfter -Descending | Select-Object -First 1
        }
        if ($pfx -or $storeCert) {
            # signtool.exe: on the PATH, from an installed Windows SDK, otherwise from the NuGet package
            # Microsoft.Windows.SDK.BuildTools (downloaded once into artifacts/tools; no installation)
            $signtool = if (Get-Command signtool.exe -ErrorAction SilentlyContinue) { 'signtool.exe' }
            if (-not $signtool) {
                $sdkBin = Get-ChildItem "${env:ProgramFiles(x86)}/Windows Kits/10/bin/*/x64" -Directory -ErrorAction SilentlyContinue |
                    Where-Object { Test-Path "$_/signtool.exe" } | Sort-Object Name -Descending | Select-Object -First 1
                if (-not $sdkBin) {
                    $toolsVersion = '10.0.28000.2705'
                    $tools = Join-Path $root "artifacts/tools/sdk-buildtools-$toolsVersion"
                    if (-not (Test-Path $tools)) {
                        Write-Host "Windows SDK not installed: downloading Microsoft.Windows.SDK.BuildTools $toolsVersion (signtool)..."
                        $package = Join-Path $root "artifacts/tools/sdk-buildtools.nupkg"
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
                    $sdkBin = Get-ChildItem "$tools/bin/*/x64" -Directory -ErrorAction SilentlyContinue | Where-Object { Test-Path "$_/signtool.exe" } |
                        Sort-Object Name -Descending | Select-Object -First 1
                }
                if ($sdkBin) { $signtool = "$sdkBin/signtool.exe" }
            }
            if ($signtool) {
                $signArgs = if ($storeCert) { @('sign', '/fd', 'SHA256', '/sha1', $storeCert.Thumbprint, '/s', 'My') } else { @('sign', '/fd', 'SHA256', '/f', $pfx) }
                if ($pfxPassword -and -not $storeCert) { $signArgs += @('/p', $pfxPassword) }
                & $signtool @signArgs "$dist/$name-setup.exe"
                if ($LASTEXITCODE -ne 0) { throw "signtool failed (setup)" }
                Write-Host "Setup signed by $(if ($storeCert) { "$($storeCert.Subject) ($($storeCert.Thumbprint))" } else { $pfx })"
            }
            else {
                Write-Host "signtool.exe not found: the setup is not signed."
            }
        }
        Write-Host "Setup: $dist/$name-setup.exe"
    }
    'linux-*' {
        # tar.gz: the program, a .desktop file and the hicolor icon theme next to it (for installing by hand: copy
        # icons/hicolor to ~/.local/share/icons or /usr/share/icons)
        Copy-Item "$root/src/NextCommander.App/Assets/NextCommander.png" "$out/nextcommander.png"
        Copy-Item "$PSScriptRoot/linux/nextcommander.desktop" "$out/nextcommander.desktop"
        $sizes = 16, 24, 32, 48, 64, 96, 128, 256, 512
        foreach ($size in $sizes) {
            $iconDir = "$out/icons/hicolor/${size}x${size}/apps"
            New-Item -ItemType Directory -Force $iconDir | Out-Null
            Copy-Item "$PSScriptRoot/icons/png/$size.png" "$iconDir/nextcommander.png"
        }
        $svg = "$PSScriptRoot/icons/nextcommander.svg"
        if (Test-Path $svg) {
            New-Item -ItemType Directory -Force "$out/icons/hicolor/scalable/apps" | Out-Null
            Copy-Item $svg "$out/icons/hicolor/scalable/apps/nextcommander.svg"
        }
        tar -czf "$dist/$name.tar.gz" -C $out .

        # deb and rpm (nfpm): the program in /opt/nextcommander, the command /usr/bin/nextcommander, the .desktop file
        # in /usr/share/applications and the icons in the hicolor theme of /usr/share/icons
        $nfpm = Get-Nfpm
        if ($nfpm) {
            $packageDir = Join-Path $root "artifacts/nfpm-$Rid"
            Remove-Item -Recurse -Force $packageDir -ErrorAction SilentlyContinue
            New-Item -ItemType Directory -Force $packageDir | Out-Null
            # the menu entry of the package starts the command on the PATH; the package scripts with LF line ends (a
            # Windows checkout may have turned them into CRLF, which /bin/sh cannot run)
            $utf8 = New-Object System.Text.UTF8Encoding $false
            [IO.File]::WriteAllText("$packageDir/nextcommander.desktop",
                [IO.File]::ReadAllText("$PSScriptRoot/linux/nextcommander.desktop", $utf8).Replace('Exec=NextCommander ', 'Exec=nextcommander ').Replace("`r`n", "`n"), $utf8)
            foreach ($script in 'postinstall.sh', 'postremove.sh') {
                [IO.File]::WriteAllText("$packageDir/$script", [IO.File]::ReadAllText("$PSScriptRoot/linux/$script", $utf8).Replace("`r`n", "`n"), $utf8)
            }
            function Yaml([string]$Path) { "'" + $Path.Replace('\', '/').Replace("'", "''") + "'" }
            $extras = @('nextcommander.png', 'nextcommander.desktop')
            $contents = New-Object System.Collections.Generic.List[string]
            foreach ($file in Get-ChildItem $out -Recurse -File) {
                $relative = $file.FullName.Substring((Resolve-Path $out).Path.Length).TrimStart('\', '/').Replace('\', '/')
                if ($extras -contains $relative -or $relative.StartsWith('icons/')) { continue }
                $mode = if ($relative -eq 'NextCommander') { '0755' } else { '0644' }
                $contents.Add("  - src: $(Yaml $file.FullName)`n    dst: $(Yaml "/opt/nextcommander/$relative")`n    file_info:`n      mode: $mode")
            }
            $contents.Add("  - src: /opt/nextcommander/NextCommander`n    dst: /usr/bin/nextcommander`n    type: symlink")
            $contents.Add("  - src: $(Yaml "$packageDir/nextcommander.desktop")`n    dst: /usr/share/applications/nextcommander.desktop`n    file_info:`n      mode: 0644")
            foreach ($size in $sizes) {
                $contents.Add("  - src: $(Yaml "$PSScriptRoot/icons/png/$size.png")`n    dst: /usr/share/icons/hicolor/${size}x${size}/apps/nextcommander.png`n    file_info:`n      mode: 0644")
            }
            if (Test-Path $svg) {
                $contents.Add("  - src: $(Yaml $svg)`n    dst: /usr/share/icons/hicolor/scalable/apps/nextcommander.svg`n    file_info:`n      mode: 0644")
            }
            $packageArch = if ($Rid -eq 'linux-arm64') { 'arm64' } else { 'amd64' }
            $maintainer = if ($env:PACKAGE_MAINTAINER) { $env:PACKAGE_MAINTAINER } else { 'NextCommander' }
            $config = [IO.File]::ReadAllText("$PSScriptRoot/linux/nfpm.yaml", $utf8).Replace('$ARCH', $packageArch).Replace('$VERSION', $numericVersion).
                Replace('$MAINTAINER', $maintainer).Replace('$SCRIPTS', $packageDir.Replace('\', '/')) + "contents:`n" + ($contents -join "`n") + "`n"
            $configFile = "$packageDir/nfpm.yaml"
            [IO.File]::WriteAllText($configFile, $config.Replace("`r`n", "`n"), $utf8)
            foreach ($packager in 'deb', 'rpm') {
                & $nfpm package --config $configFile --packager $packager --target "$dist/$name.$packager"
                if ($LASTEXITCODE -ne 0) { throw "nfpm failed ($packager)" }
            }
        }
        else {
            Write-Host "nfpm not found: skipping the .deb and .rpm packages."
        }

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
        # UTF-8 without BOM (Windows PowerShell 5.1 reads and writes the ANSI code page by default)
        [IO.File]::WriteAllText("$app/Contents/Info.plist", [IO.File]::ReadAllText("$PSScriptRoot/macos/Info.plist", [Text.Encoding]::UTF8).Replace('$VERSION', $numericVersion).Replace("`r`n", "`n"), (New-Object System.Text.UTF8Encoding $false))
        if (-not $hostWindows) { chmod +x "$app/Contents/MacOS/NextCommander" }
        if ($env:MACOS_SIGN_IDENTITY) {
            codesign --force --deep --options runtime --timestamp --entitlements "$PSScriptRoot/macos/entitlements.plist" -s $env:MACOS_SIGN_IDENTITY $app
        }
        elseif (Get-Command codesign -ErrorAction SilentlyContinue) {
            # Ad-hoc signature of the whole bundle (Info.plist and resources sealed): without it a downloaded copy is
            # reported as "damaged" instead of "unidentified developer". Packed elsewhere only the program is signed.
            codesign --force --deep -s - $app
        }

        # The app itself: dist/<name>/NextCommander.app (copied with ditto on a Mac, which keeps its signature and modes;
        # elsewhere the executable bit is not kept: use the zip to bring it to a Mac)
        $appOut = Join-Path $dist $name
        Remove-Item -Recurse -Force $appOut -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force $appOut | Out-Null
        if (Get-Command ditto -ErrorAction SilentlyContinue) { ditto $app "$appOut/NextCommander.app" }
        else { Copy-Item $app $appOut -Recurse }
        Write-Host "App: $appOut/NextCommander.app"

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
