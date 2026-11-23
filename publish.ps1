$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$project = Join-Path $root 'src/HongguoTV.Desktop/HongguoTV.Desktop.csproj'
$manifest = Join-Path $root 'src/HongguoTV.Desktop/app.manifest'

$projectText = Get-Content -Raw -Encoding UTF8 $project
$manifestText = Get-Content -Raw -Encoding UTF8 $manifest

if ($projectText -notmatch '<Version>(\d+)\.(\d+)\.(\d+)</Version>') {
    throw '未在 csproj 中找到 <Version>x.y.z</Version>'
}
$major = [int]$Matches[1]
$minor = [int]$Matches[2]
$patch = [int]$Matches[3]
$next = "$major.$minor.$($patch + 1)"
$nextFull = "$next.0"

$newProjectText = $projectText -replace '<Version>\d+\.\d+\.\d+</Version>', "<Version>$next</Version>"
$newManifestText = $manifestText -replace '(<assemblyIdentity\s+version=")\d+\.\d+\.\d+\.\d+(")', ('${1}' + $nextFull + '${2}')

Set-Content -Path $project -Value $newProjectText -Encoding UTF8 -NoNewline
Set-Content -Path $manifest -Value $newManifestText -Encoding UTF8 -NoNewline

Write-Host "版本号已更新：$major.$minor.$patch -> $next"

try {
    dotnet publish $project -c Release -r win-x64 -p:PublishProfile=FolderProfile --nologo
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish 失败，退出码 $LASTEXITCODE" }
}
catch {
    Set-Content -Path $project -Value $projectText -Encoding UTF8 -NoNewline
    Set-Content -Path $manifest -Value $manifestText -Encoding UTF8 -NoNewline
    Write-Host '发布失败，已回滚版本号。' -ForegroundColor Yellow
    throw
}

Write-Host "发布完成：$next -> $(Join-Path $root 'src/HongguoTV.Desktop/bin/Release/net10.0/publish')" -ForegroundColor Green
