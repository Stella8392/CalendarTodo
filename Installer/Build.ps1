param(
    [string]$Dotnet = 'dotnet',
    [string]$Wix = 'wix',
    [string]$OutputDirectory
)

$ErrorActionPreference = 'Stop'
$project = Split-Path -Parent $PSScriptRoot
$publish = Join-Path $project 'App/obj/widget-publish'
$output = if ($OutputDirectory) { $OutputDirectory } else { Join-Path $project 'dist' }
$package = Join-Path $output 'CalendarTodo-Setup-1.1.6-x64.msi'
$intermediate = Join-Path $project 'App/obj/wix-intermediate'

& $Dotnet publish (Join-Path $project 'App/CalendarTodo.csproj') -c Release -o $publish -p:DebugType=None -p:DebugSymbols=false -p:UseSharedCompilation=false
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed ($LASTEXITCODE)" }

Copy-Item -LiteralPath (Join-Path $project 'ThirdParty') -Destination $publish -Recurse -Force
Copy-Item -LiteralPath (Join-Path $project 'THIRD-PARTY-NOTICES.md') -Destination $publish -Force
$null = New-Item -ItemType Directory -Path $output -Force
$definition = Join-Path $PSScriptRoot 'CalendarTodo.wxs'
& $Wix build $definition -arch x64 -d "PublishDir=$publish" -intermediatefolder $intermediate -pdbtype none -out $package
if ($LASTEXITCODE -ne 0) { throw "wix build failed ($LASTEXITCODE)" }

Get-Item -LiteralPath $package,(Join-Path $output 'CalendarTodo-1.1.6.cab')
