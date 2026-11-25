<#
.SYNOPSIS
  Makes a GitHub release of NextCommander with one command: tag, packages of every platform, notes, upload.

.DESCRIPTION
  1. Takes the commit to release (default: origin/main after a fetch) into a worktree of its own next to the
     repository, so the working copy and its branch stay as they are.
  2. Reads the version of that commit (YY.MM.NNN, the NcVersion target): the release is v<version>.
  3. Tags the commit v<version> (annotated) and pushes the tag; a tag that is already there must point at the same commit.
  4. Builds the packages with publish.ps1 (-Rid all by default) and writes SHA256SUMS.txt for them.
  5. The notes: docs/release-notes/<version>.md of that commit when it has one, else made from the commit subjects
     since the previous v* tag (feat: new, fix: fixed); links into the repository are made absolute.
  6. Creates the release as a draft (or updates the draft that is there), uploads every package and checks each one
     arrived whole (state and size; an upload that broke off is deleted and sent again).
  7. With -Publish the draft is published as the latest release; without it, it stays a draft to look over.

  Needs git, the .NET SDK and the GitHub CLI (gh, logged in with the repo scope); publish.ps1's own needs (Inno
  Setup, nfpm and Homebrew's bottles are downloaded by it; Python 3 for the macOS packages).

.EXAMPLE
  ./build/release.ps1                      # draft release of origin/main with every package
  ./build/release.ps1 -Publish             # the same, published at the end
  ./build/release.ps1 -Ref v26.11.488      # the draft of a commit / tag that is already there (packages made again)
  ./build/release.ps1 -Rid win-x64         # only some packages (e.g. to try the script)
#>
param(
    [string]$Ref = 'origin/main',
    [ValidateSet('all', 'win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')][string[]]$Rid = @('all'),
    [switch]$Publish,
    # Upload what was built even when a platform failed (its packages are then missing from the release).
    [switch]$AllowFailedPlatforms,
    # Leave the release worktree (with its dist/ folder) in place at the end.
    [switch]$KeepWorktree,
    # Everything on this machine only: no tag, no release, no upload; the worktree is kept to look at.
    [switch]$DryRun
)
if ($DryRun) { $KeepWorktree = $true }
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# A native program whose failure is its exit code. Windows PowerShell 5.1 with 'Stop' would also count any line on
# stderr (git and gh write progress there) as a failure, so the preference is 'Continue' while it runs.
function Invoke-Native([string]$Program, [string[]]$Arguments, [switch]$Quiet) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & $Program @Arguments 2>&1 }
    finally { $ErrorActionPreference = $previous }
    $text = ($output | ForEach-Object { "$_" }) -join "`n"
    if ($LASTEXITCODE -ne 0) { throw "$Program $($Arguments -join ' ') failed ($LASTEXITCODE):`n$text" }
    if (-not $Quiet -and $text) { Write-Host $text }
    return $text
}

# A native program whose failure only means "no answer": its output, or $null.
function Get-NativeAnswer([string]$Program, [string[]]$Arguments) {
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    try { $output = & $Program @Arguments 2>$null }
    catch { $output = $null }
    finally { $ErrorActionPreference = $previous }
    if ($LASTEXITCODE -ne 0 -or -not $output) { return $null }
    return (($output | ForEach-Object { "$_" }) -join "`n").Trim()
}

function Write-Step([string]$Text) { Write-Host ""; Write-Host "== $Text" -ForegroundColor Cyan }

foreach ($tool in 'git', 'dotnet', 'gh') {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) { throw "$tool is needed for a release" }
}
Invoke-Native gh @('auth', 'status') -Quiet | Out-Null
$repo = (Invoke-Native gh @('repo', 'view', '--json', 'nameWithOwner', '--jq', '.nameWithOwner') -Quiet).Trim()

Write-Step "Commit to release: $Ref"
Push-Location $root
try {
    Invoke-Native git @('fetch', '--quiet', '--tags', 'origin') -Quiet | Out-Null
    $commit = (Invoke-Native git @('rev-parse', '--verify', "$Ref^{commit}") -Quiet).Trim()
}
finally { Pop-Location }
Write-Host "$commit ($((Invoke-Native git @('-C', $root, 'log', '-1', '--format=%s', $commit) -Quiet).Trim()))"

# The worktree: next to the repository (not inside it), one per commit.
$worktree = Join-Path (Split-Path -Parent $root) ("NextCommander-release-" + $commit.Substring(0, 10))
if (Test-Path $worktree) { Invoke-Native git @('-C', $root, 'worktree', 'remove', '--force', $worktree) -Quiet | Out-Null }
Invoke-Native git @('-C', $root, 'worktree', 'add', '--quiet', '--detach', $worktree, $commit) -Quiet | Out-Null

try {
    $version = ((Invoke-Native dotnet @('msbuild', "$worktree/src/NextCommander.App", '-nologo', '-t:NcVersion', '-getProperty:Version') -Quiet) -split "`n" | Select-Object -Last 1).Trim()
    if ($version -notmatch '^\d\d\.\d\d\.\d+$') { throw "Could not read the version of $commit ($version)" }
    $tag = "v$version"
    Write-Step "Version $version, tag $tag"

    # The tag: made and pushed, or checked when it is there already.
    $existing = Get-NativeAnswer git @('-C', $root, 'rev-parse', '--verify', '--quiet', "refs/tags/$tag^{commit}")
    if ($existing) {
        if ($existing -ne $commit) { throw "$tag already points at $existing, not at $commit" }
        Write-Host "$tag is there already"
    }
    elseif ($DryRun) { Write-Host "(dry run: $tag not made)" }
    else {
        Invoke-Native git @('-C', $root, 'tag', '-a', $tag, $commit, '-m', "NextCommander $version") -Quiet | Out-Null
        Write-Host "tagged $tag"
    }
    if (-not $DryRun) { Invoke-Native git @('-C', $root, 'push', '--quiet', 'origin', "refs/tags/$tag") -Quiet | Out-Null }

    # A release that is already published is not touched.
    $release = $null
    $json = Get-NativeAnswer gh @('release', 'view', $tag, '--json', 'isDraft,url')
    if ($json) { $release = $json | ConvertFrom-Json }
    if ($release -and -not $release.isDraft) { throw "The release $tag is published already: $($release.url)" }

    Write-Step "Packages ($($Rid -join ', '))"
    $dist = Join-Path $worktree 'dist'
    $failed = @()
    foreach ($one in $Rid) {
        & powershell -NoProfile -ExecutionPolicy Bypass -File "$worktree/build/publish.ps1" -Rid $one
        if ($LASTEXITCODE -ne 0) { $failed += $one }
    }
    if ($failed -and -not $AllowFailedPlatforms) {
        throw "publish.ps1 failed ($($failed -join ', ')); the packages are in $dist. -AllowFailedPlatforms uploads what was built."
    }
    $assets = @(Get-ChildItem $dist -File -Filter "NextCommander-$version-*" | Sort-Object Name)
    if (-not $assets) { throw "No packages in $dist" }
    $sums = Join-Path $dist 'SHA256SUMS.txt'
    $lines = $assets | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" }
    [IO.File]::WriteAllText($sums, (($lines -join "`n") + "`n"), (New-Object Text.UTF8Encoding $false))
    $assets += Get-Item $sums
    $assets | ForEach-Object { Write-Host ("{0,-52} {1,8:N1} MB" -f $_.Name, ($_.Length / 1MB)) }

    Write-Step "Notes"
    $blob = "https://github.com/$repo/blob/$tag"
    $notesFile = Join-Path $worktree "docs/release-notes/$version.md"
    if (Test-Path $notesFile) {
        Write-Host "from docs/release-notes/$version.md"
        $notes = [IO.File]::ReadAllText($notesFile, [Text.Encoding]::UTF8).Replace("`r`n", "`n")
        $notes = [regex]::Replace($notes, '^#\s+[^\n]*\n+', '')                       # the title is the release's name
        $notes = $notes.Replace('](../guide/', "]($blob/docs/guide/").Replace('](../', "]($blob/docs/")
    }
    else {
        $since = Get-NativeAnswer git @('-C', $root, 'describe', '--tags', '--abbrev=0', '--match', 'v[0-9]*', "$commit^")
        $range = if ($since) { "$since..$commit" } else { $commit }
        Write-Host "from the commits $range"
        $subjects = (Invoke-Native git @('-C', $root, 'log', '--no-merges', '--format=%s', $range) -Quiet) -split "`n" | Where-Object { $_ }
        $added = @(); $fixed = @(); $other = @()
        foreach ($subject in $subjects) {
            if ($subject -match '^(\w+)(\([^)]*\))?!?:\s*(.+)$') {
                switch ($Matches[1]) {
                    { $_ -in 'feat', 'feature' } { $added += $Matches[3] }
                    { $_ -in 'fix', 'perf' } { $fixed += $Matches[3] }
                }
            }
            elseif ($subject -notmatch '^(Merge|Revert) ') { $other += $subject }
        }
        $text = New-Object Text.StringBuilder
        foreach ($group in @(@('New', $added), @('Fixed', $fixed), @('Changes', $other))) {
            if ($group[1].Count -eq 0) { continue }
            [void]$text.AppendLine("## $($group[0])").AppendLine()
            foreach ($item in $group[1]) { [void]$text.AppendLine("- $item") }
            [void]$text.AppendLine()
        }
        if ($since) { [void]$text.AppendLine("All changes: https://github.com/$repo/compare/$since...$tag").AppendLine() }
        [void]$text.AppendLine("Downloads below; how to install: https://github.com/$repo/blob/$tag/README.md#download-and-start")
        $notes = $text.ToString()
    }
    $notesOut = Join-Path $dist 'release-notes.md'
    [IO.File]::WriteAllText($notesOut, $notes, (New-Object Text.UTF8Encoding $false))
    if ($DryRun) {
        Write-Host ""
        Write-Host "Dry run: nothing tagged or uploaded. Packages and notes ($notesOut) in $dist" -ForegroundColor Green
        return
    }

    Write-Step "Release $tag"
    $title = "NextCommander $version"
    if ($release) {
        Invoke-Native gh @('release', 'edit', $tag, '--title', $title, '--notes-file', $notesOut) -Quiet | Out-Null
        Write-Host "draft updated"
    }
    else {
        Invoke-Native gh @('release', 'create', $tag, '--draft', '--verify-tag', '--title', $title, '--notes-file', $notesOut) -Quiet | Out-Null
        Write-Host "draft created"
    }

    # Uploads, one file at a time; GitHub sometimes answers 404 or leaves an upload unfinished ("starter"): such an
    # asset is deleted and the file sent again, until it is there with its size.
    function Get-Assets { (Invoke-Native gh @('release', 'view', $tag, '--json', 'assets') -Quiet | ConvertFrom-Json).assets }
    foreach ($file in $assets) {
        for ($attempt = 1; ; $attempt++) {
            $there = Get-Assets | Where-Object name -eq $file.Name
            if ($there -and $there.state -eq 'uploaded' -and [long]$there.size -eq $file.Length) { break }
            if ($attempt -gt 4) { throw "$($file.Name) could not be uploaded" }
            if ($there) { Invoke-Native gh @('api', '-X', 'DELETE', $there.apiUrl) -Quiet | Out-Null }
            Write-Host "uploading $($file.Name)$(if ($attempt -gt 1) { " (attempt $attempt)" })"
            $previous = $ErrorActionPreference
            $ErrorActionPreference = 'Continue'
            & gh release upload $tag $file.FullName --clobber 2>&1 | Out-Null
            $ErrorActionPreference = $previous
        }
    }
    Write-Host "$($assets.Count) files uploaded and checked"

    if ($Publish) {
        Invoke-Native gh @('release', 'edit', $tag, '--draft=false', '--latest') -Quiet | Out-Null
        Write-Host "published"
    }
    $url = (Invoke-Native gh @('release', 'view', $tag, '--json', 'url', '--jq', '.url') -Quiet).Trim()
    Write-Host ""
    Write-Host "$(if ($Publish) { 'Release' } else { 'Draft release' }) $tag`: $url" -ForegroundColor Green
    if ($failed) { Write-Warning "Not in the release (publish.ps1 failed): $($failed -join ', ')" }
}
finally {
    if ($KeepWorktree) { Write-Host "Worktree kept: $worktree" }
    else { Get-NativeAnswer git @('-C', $root, 'worktree', 'remove', '--force', $worktree) | Out-Null }
}
