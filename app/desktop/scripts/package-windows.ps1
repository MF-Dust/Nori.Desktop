[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string] $PublishDir,

	[Parameter(Mandatory = $true)]
	[string] $Version,

	[string] $OutputDir = "bin\release"
)

$ErrorActionPreference = "Stop"
$publish = [IO.Path]::GetFullPath($PublishDir)
$output = [IO.Path]::GetFullPath($OutputDir)
node (Join-Path $PSScriptRoot "validate-publish-structure.mjs") $publish win-x64
if ($LASTEXITCODE -ne 0) { throw "Windows 发布结构门禁失败" }
New-Item -ItemType Directory -Path $output -Force | Out-Null

$artifactName = "nori-$Version-win-x64-framework-dependent.zip"
$artifactPath = Join-Path $output $artifactName
if (Test-Path -LiteralPath $artifactPath) { Remove-Item -LiteralPath $artifactPath -Force }
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($publish, $artifactPath, [IO.Compression.CompressionLevel]::Optimal, $false)

$hash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = $artifactPath + ".sha256"
[IO.File]::WriteAllText($checksumPath, ($hash + "  " + $artifactName + "`n"), (New-Object Text.UTF8Encoding($false)))

if ($Version -ne "Dev") {
	node (Join-Path $PSScriptRoot "generate-update-manifest.mjs") `
		--version $Version --rid win-x64 --archive-path $artifactPath --publish-dir $publish --output-dir $output
	if ($LASTEXITCODE -ne 0) { throw "生成 Windows 更新清单失败" }
}

Write-Host "Windows framework-dependent ZIP 已生成: $artifactPath"
Write-Host "SHA-256: $checksumPath"
