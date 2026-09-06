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
if (-not (Test-Path -LiteralPath $publish -PathType Container)) { throw "找不到发布目录: $publish" }
New-Item -ItemType Directory -Path $output -Force | Out-Null

if (-not (Test-Path -LiteralPath (Join-Path $publish ".current") -PathType Leaf)) { throw "发布根目录缺少 .current" }
$currentSlot = (Get-Content -LiteralPath (Join-Path $publish ".current") -Raw).Trim()
if ($currentSlot -notmatch '^app-[0-9]+\.[0-9]+\.[0-9]+-[0-9]+$') { throw "发布根目录 .current 无效: $currentSlot" }
$requiredFiles = @(
	"Nori.exe",
	"LICENSE",
	"$currentSlot\deployment.json",
	"$currentSlot\Nori.Desktop.exe",
	"$currentSlot\Nori.Desktop.dll",
	"$currentSlot\Nori.Desktop.deps.json",
	"$currentSlot\Nori.Desktop.runtimeconfig.json",
	"$currentSlot\Live2DCubismCore.dll",
	"$currentSlot\wwwroot\index.html"
)
foreach ($relativePath in $requiredFiles) {
	$requiredPath = Join-Path $publish $relativePath
	if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
		throw "发布目录缺少必要文件: $relativePath"
	}
}

if (Get-ChildItem -LiteralPath $publish -Recurse -File -Filter "dotnet.exe" -ErrorAction SilentlyContinue) {
	throw "Windows ZIP 不得携带 .NET Runtime (必须是 framework-dependent)"
}
if (Get-ChildItem -LiteralPath $publish -Recurse -File -Filter "*.map" -ErrorAction SilentlyContinue) {
	throw "发布目录仍含 source map, 不能打包"
}

# Windows FDD 的未压缩 publish 基线约 159 MiB（WebView/Avalonia/原生依赖占比较高）。
# 预算留约 13% 余量，仍可拦截再次引入几十 MiB 级运行时（如 Playwright driver）。
node (Join-Path $PSScriptRoot "check-package-size.mjs") `
	--path $publish --label "windows publish" --max-mib 180
if ($LASTEXITCODE -ne 0) { throw "Windows 发布体积门禁失败" }

$metadataOutput = Join-Path $output "metadata"
Remove-Item -LiteralPath $metadataOutput -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $metadataOutput -Force | Out-Null
node (Join-Path $PSScriptRoot "generate-release-metadata.mjs") `
	--publish-dir $publish --version $Version --rid win-x64 --output-dir $metadataOutput

if (Test-Path -LiteralPath (Join-Path $publish "data")) { throw "发布包不得携带运行时 data 目录" }
$artifactName = "nori-$Version-win-x64-framework-dependent.zip"
$artifactPath = Join-Path $output $artifactName
$staging = Join-Path $output ".staging"
$expanded = Join-Path $output ".expanded-smoke"
Remove-Item -LiteralPath $staging, $expanded, $artifactPath -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path $staging -Force | Out-Null
Get-ChildItem -LiteralPath $publish -Force | Copy-Item -Destination $staging -Recurse -Force
node (Join-Path $PSScriptRoot "validate-publish-structure.mjs") $publish win-x64
if ($LASTEXITCODE -ne 0) { throw "Windows 发布结构门禁失败" }
$metadataFiles = @("THIRD-PARTY-NOTICES.json", "THIRD-PARTY-NOTICES.md", "SBOM.cdx.json", "RELEASE-MANIFEST.json")
foreach ($metadataFile in $metadataFiles) {
	$metadataPath = Join-Path $metadataOutput $metadataFile
	Copy-Item -LiteralPath $metadataPath -Destination $staging -Force
	Copy-Item -LiteralPath $metadataPath -Destination $output -Force
	$suffixed = switch ($metadataFile) {
		"SBOM.cdx.json" { "SBOM-win-x64.cdx.json" }
		"THIRD-PARTY-NOTICES.json" { "THIRD-PARTY-NOTICES-win-x64.json" }
		"THIRD-PARTY-NOTICES.md" { "THIRD-PARTY-NOTICES-win-x64.md" }
		"RELEASE-MANIFEST.json" { "RELEASE-MANIFEST-win-x64.json" }
	}
	Copy-Item -LiteralPath $metadataPath -Destination (Join-Path $output $suffixed) -Force
}
Add-Type -AssemblyName System.IO.Compression.FileSystem
[IO.Compression.ZipFile]::CreateFromDirectory($staging, $artifactPath, [IO.Compression.CompressionLevel]::Optimal, $false)

# 解压后再检查一次, 避免 ZIP 根目录或必要文件路径打错。
Expand-Archive -LiteralPath $artifactPath -DestinationPath $expanded -Force
foreach ($relativePath in $requiredFiles + @("THIRD-PARTY-NOTICES.json", "SBOM.cdx.json", "RELEASE-MANIFEST.json")) {
	if (-not (Test-Path -LiteralPath (Join-Path $expanded $relativePath) -PathType Leaf)) {
		throw "ZIP 内容缺少必要文件: $relativePath"
	}
}
if (Test-Path -LiteralPath (Join-Path $expanded "shared")) {
	throw "ZIP 包含 .NET shared runtime, 不是 framework-dependent 包"
}

$hash = (Get-FileHash -LiteralPath $artifactPath -Algorithm SHA256).Hash.ToLowerInvariant()
$checksumPath = $artifactPath + ".sha256"
[IO.File]::WriteAllText($checksumPath, ($hash + "  " + $artifactName + "`n"), (New-Object Text.UTF8Encoding($false)))

if ($Version -ne "Dev") {
	node (Join-Path $PSScriptRoot "generate-update-manifest.mjs") `
		--version $Version --rid win-x64 --archive-path $artifactPath --publish-dir $publish --output-dir $output
	if ($LASTEXITCODE -ne 0) { throw "生成 Windows 更新清单失败" }
}

Remove-Item -LiteralPath $staging, $expanded -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Windows framework-dependent ZIP 已生成: $artifactPath"
Write-Host "SHA-256: $checksumPath"
