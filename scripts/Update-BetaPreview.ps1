param(
    [string]$BuildDirectory = "$PSScriptRoot/../bin/Debug/net8.0-windows",
    [string]$Destination = "$PSScriptRoot/../artifacts/beta-preview"
)
$ErrorActionPreference = 'Stop'

# 配置更新は実行物だけ。ビルド先に残った個人DB・INI・Session設定を混入させない。
$source = (Resolve-Path -LiteralPath $BuildDirectory).Path
if (!(Test-Path -LiteralPath (Join-Path $source 'IIDXProgressDashboard.exe'))) {
    throw '先にアプリをビルドしてください。'
}
New-Item -ItemType Directory -Path $Destination -Force | Out-Null
Get-ChildItem -LiteralPath $source -File | Where-Object {
    $_.Extension -in '.exe', '.dll', '.pdb' -or
    $_.Name -in 'IIDXProgressDashboard.deps.json', 'IIDXProgressDashboard.runtimeconfig.json'
} | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $Destination -Force }
foreach ($name in 'runtimes', 'ThirdParty') {
    $folder = Join-Path $source $name
    if (Test-Path -LiteralPath $folder) { Copy-Item -LiteralPath $folder -Destination $Destination -Recurse -Force }
}
