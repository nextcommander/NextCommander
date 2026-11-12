<#
.SYNOPSIS
  Starts local FTP, SFTP, WebDAV, S3, SMB and Azure Blob test servers and runs the remote integration tests against them.

.DESCRIPTION
  Needs Python 3. Creates tests/servers/.venv (pyftpdlib, asyncssh, wsgidav, moto; see requirements.txt) on the first
  run, serves temporary folders on 127.0.0.1:2121 (FTP), 127.0.0.1:2222 (SFTP), 127.0.0.1:8088 (WebDAV) and
  127.0.0.1:4445 (SMB, impacket), an
  in-memory S3 on 127.0.0.1:5000 and, when Node.js is installed, Azurite (Azure Blob, installed into
  tests/servers/node_modules on the first run) on 127.0.0.1:10000, sets FM_TEST_FTP / FM_TEST_SFTP / FM_TEST_WEBDAV /
  FM_TEST_S3 / FM_TEST_SMB / FM_TEST_AZURE and runs the Remote
  tests of NextCommander.Core.Tests and the Phase6 tests of NextCommander.App.Tests. The servers are stopped afterwards.
  Without these variables the integration tests are skipped, so a normal "dotnet test" does not need any of this.

.EXAMPLE
  ./tests/servers/run-integration-tests.ps1
#>
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot
$root = Split-Path -Parent (Split-Path -Parent $here)
$venv = Join-Path $here '.venv'
$python = if ($IsWindows -or $env:OS -eq 'Windows_NT') { Join-Path $venv 'Scripts/python.exe' } else { Join-Path $venv 'bin/python' }

if (-not (Test-Path $python)) {
    $system = if (Get-Command python3 -ErrorAction SilentlyContinue) { 'python3' } else { 'python' }
    # Native tools write notices to stderr; Windows PowerShell would treat them as errors under 'Stop'.
    $ErrorActionPreference = 'Continue'
    & $system -m venv $venv
    & $python -m pip install --quiet --disable-pip-version-check -r (Join-Path $here 'requirements.txt') 2>&1 | Out-Host
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) { throw 'pip install failed.' }
}
else {
    # an older .venv: add the packages that came later (WebDAV, S3, SMB)
    $ErrorActionPreference = 'Continue'
    & $python -c "import wsgidav, cheroot, moto, impacket" 2>$null
    if ($LASTEXITCODE -ne 0) {
        & $python -m pip install --quiet --disable-pip-version-check -r (Join-Path $here 'requirements.txt') 2>&1 | Out-Host
    }
    $ErrorActionPreference = 'Stop'
}

# Azure Blob: Azurite (Node.js); without Node the Azure tests are skipped
$azurite = Join-Path $here 'node_modules/azurite/dist/src/blob/main.js'
$node = Get-Command node -ErrorAction SilentlyContinue
if ($node -and -not (Test-Path $azurite)) {
    $ErrorActionPreference = 'Continue'
    npm install --prefix $here --no-save --no-package-lock --no-audit --no-fund azurite@3.35.0 2>&1 | Out-Host
    $ErrorActionPreference = 'Stop'
}
$useAzure = $node -and (Test-Path $azurite)

$data = Join-Path ([IO.Path]::GetTempPath()) ("fm-servers-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
$ftpRoot = Join-Path $data 'ftp'
$sftpRoot = Join-Path $data 'sftp'
$davRoot = Join-Path $data 'webdav'
$azureRoot = Join-Path $data 'azure'
$smbRoot = Join-Path $data 'smb'
New-Item -ItemType Directory -Force $ftpRoot, $sftpRoot, $davRoot, $azureRoot, $smbRoot | Out-Null

$servers = @(
    Start-Process -FilePath $python -ArgumentList @((Join-Path $here 'ftp_server.py'), $ftpRoot) -PassThru -WindowStyle Hidden
    Start-Process -FilePath $python -ArgumentList @((Join-Path $here 'sftp_server.py'), $sftpRoot) -PassThru -WindowStyle Hidden
    Start-Process -FilePath $python -ArgumentList @((Join-Path $here 'webdav_server.py'), $davRoot) -PassThru -WindowStyle Hidden
    Start-Process -FilePath $python -ArgumentList @((Join-Path $here 's3_server.py')) -PassThru -WindowStyle Hidden
    Start-Process -FilePath $python -ArgumentList @((Join-Path $here 'smb_server.py'), $smbRoot) -PassThru -WindowStyle Hidden
)
if ($useAzure) {
    $servers += Start-Process -FilePath $node.Source -PassThru -WindowStyle Hidden -ArgumentList @(
        "`"$azurite`"", '--blobHost', '127.0.0.1', '--blobPort', '10000', '--location', "`"$azureRoot`"", '--silent', '--skipApiVersionCheck', '--loose')
}

function Wait-Port([int]$port) {
    for ($i = 0; $i -lt 50; $i++) {
        try {
            $client = [Net.Sockets.TcpClient]::new('127.0.0.1', $port)
            $client.Dispose()
            return
        }
        catch {
            Start-Sleep -Milliseconds 200
        }
    }
    throw "Test server on port $port did not start."
}

try {
    Wait-Port 2121
    Wait-Port 2222
    Wait-Port 8088
    Wait-Port 5000
    Wait-Port 4445
    if ($useAzure) {
        Wait-Port 10000
        # Azurite's well-known development account
        $env:FM_TEST_AZURE = 'azblob://devstoreaccount1:' + [Uri]::EscapeDataString('Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==') + '@127.0.0.1:10000/fm-test-container'
    }
    # Password "şifre123" percent-encoded (UTF-8)
    $env:FM_TEST_FTP = 'ftp://tester:%C5%9Fifre123@127.0.0.1:2121/'
    $env:FM_TEST_SFTP = 'sftp://tester:%C5%9Fifre123@127.0.0.1:2222/'
    $env:FM_TEST_WEBDAV = 'dav://tester:%C5%9Fifre123@127.0.0.1:8088/'
    $env:FM_TEST_S3 = 's3://testkey:testsecret@127.0.0.1:5000/fm-test-bucket'
    $env:FM_TEST_SMB = 'smb://tester:%C5%9Fifre123@127.0.0.1:4445/TEST'
    dotnet test (Join-Path $root 'tests/NextCommander.Core.Tests') --filter 'FullyQualifiedName~Remote|FullyQualifiedName~S3Options'
    if ($LASTEXITCODE -ne 0) { throw 'Core remote tests failed.' }
    dotnet test (Join-Path $root 'tests/NextCommander.App.Tests') --filter 'FullyQualifiedName~Phase6'
    if ($LASTEXITCODE -ne 0) { throw 'App remote tests failed.' }
}
finally {
    Remove-Item Env:FM_TEST_FTP, Env:FM_TEST_SFTP, Env:FM_TEST_WEBDAV, Env:FM_TEST_S3, Env:FM_TEST_SMB, Env:FM_TEST_AZURE -ErrorAction SilentlyContinue
    foreach ($server in $servers) {
        if (-not $server.HasExited) { Stop-Process -Id $server.Id }
    }
    Remove-Item -Recurse -Force $data -ErrorAction SilentlyContinue
}
