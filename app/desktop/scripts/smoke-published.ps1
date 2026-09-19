[CmdletBinding()]
param(
	[Parameter(Mandatory = $true)]
	[string] $BinaryPath,

	[ValidateSet("first-run", "initialized")]
	[string] $Mode = "first-run",

	[Alias("Profile")]
	[string] $ProfilePath = "",
	[string] $ExpectedVersion = "",
	[switch] $SafeMode,
	[int] $TimeoutSeconds = 30
)

$ErrorActionPreference = "Stop"

$binary = [IO.Path]::GetFullPath($BinaryPath)
if (-not (Test-Path -LiteralPath $binary -PathType Leaf)) {
	throw "找不到发布可执行文件: $binary"
}
if ($TimeoutSeconds -lt 5 -or $TimeoutSeconds -gt 300) {
	throw "TimeoutSeconds 必须在 5 到 300 之间"
}

$ownsProfile = [string]::IsNullOrWhiteSpace($ProfilePath)
if ($ownsProfile) {
	$ProfilePath = Join-Path ([IO.Path]::GetTempPath()) ("nori-smoke-{0}" -f ([Guid]::NewGuid().ToString("N")))
}
$resolvedProfile = [IO.Path]::GetFullPath($ProfilePath)
$ancestor = [IO.Directory]::GetParent($resolvedProfile)
while ($null -ne $ancestor) {
	if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "profile 祖先目录包含 reparse point: $($ancestor.FullName)" }
	$ancestor = $ancestor.Parent
}
if (Test-Path -LiteralPath $resolvedProfile) {
	$item = Get-Item -LiteralPath $resolvedProfile -Force
	if (-not $item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw "profile 不能是文件或 reparse point: $resolvedProfile" }
	if (@(Get-ChildItem -LiteralPath $resolvedProfile -Force).Count -ne 0) { throw "profile 必须是此前不存在或完全为空的目录, 不会删除已有内容: $resolvedProfile" }
} else {
	New-Item -ItemType Directory -Path $resolvedProfile -Force | Out-Null
}
$databasePath = Join-Path $resolvedProfile "data\core\database\nori.db"
if (Test-Path -LiteralPath $databasePath) {
	throw "profile 不是隔离的临时目录, 已存在 nori.db: $databasePath"
}
$readinessPath = Join-Path $resolvedProfile "readiness.json"
$logToken = [Guid]::NewGuid().ToString("N")
$stdoutPath = Join-Path ([IO.Path]::GetTempPath()) "nori-smoke-$logToken.stdout.log"
$stderrPath = Join-Path ([IO.Path]::GetTempPath()) "nori-smoke-$logToken.stderr.log"
Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue

$process = $null
try {
	$workingDirectory = Split-Path -Parent $binary
	$arguments = "--smoke-test $Mode --profile `"$resolvedProfile`""
	if ($SafeMode) { $arguments += " --safe-mode" }
	$process = Start-Process -FilePath $binary -ArgumentList $arguments -WorkingDirectory $workingDirectory `
		-RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru

	$deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
	while (-not (Test-Path -LiteralPath $readinessPath -PathType Leaf)) {
		if ($process.HasExited) {
			$stderr = if (Test-Path -LiteralPath $stderrPath) { Get-Content -LiteralPath $stderrPath -Raw } else { "" }
			throw "发布程序在 readiness checkpoint 前退出 (code=$($process.ExitCode)): $stderr"
		}
		if ([DateTime]::UtcNow -gt $deadline) {
			throw "等待 readiness checkpoint 超时 ($TimeoutSeconds 秒): $readinessPath"
		}
		Start-Sleep -Milliseconds 200
	}

	$ready = Get-Content -LiteralPath $readinessPath -Raw | ConvertFrom-Json
	if ($ready.status -ne "ready") { throw "readiness status 不正确: $($ready.status)" }
	if ($ready.mode -ne $Mode) { throw "readiness mode 不正确: $($ready.mode)" }
	if ($null -eq $ready.schema_version -or [int]$ready.schema_version -ne 2) { throw "readiness schema_version 必须为 2" }
	if ([string]::IsNullOrWhiteSpace([string]$ready.product_version)) { throw "readiness 缺少 product_version" }
	if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and $ready.product_version -ne $ExpectedVersion) {
		throw "readiness product_version 不匹配: $($ready.product_version) != $ExpectedVersion"
	}
	if ($null -eq $ready.database_schema_version -or [int]$ready.database_schema_version -lt 1) { throw "readiness database_schema_version 无效" }
	if ($null -eq $ready.config_schema_version -or [int]$ready.config_schema_version -lt 1) { throw "readiness config_schema_version 无效" }
	if ($null -eq $ready.safe_mode -or [bool]$ready.safe_mode -ne [bool]$SafeMode) {
		throw "readiness safe_mode 不匹配: $($ready.safe_mode) != $SafeMode"
	}
	$expectedDataDir = [IO.Path]::GetFullPath((Join-Path $resolvedProfile "data"))
	$actualDataDir = [IO.Path]::GetFullPath([string]$ready.data_dir)
	if (-not [StringComparer]::OrdinalIgnoreCase.Equals($actualDataDir, $expectedDataDir)) {
		throw "冒烟程序使用了 profile 之外的数据目录: $actualDataDir"
	}

	$exitDeadline = [DateTime]::UtcNow.AddSeconds(10)
	while (-not $process.HasExited -and [DateTime]::UtcNow -lt $exitDeadline) {
		Start-Sleep -Milliseconds 200
	}
	if (-not $process.HasExited) {
		Stop-Process -Id $process.Id -Force
		throw "冒烟程序写入 readiness 后未在 10 秒内退出"
	}
	if ($process.ExitCode -ne 0) {
		throw "冒烟程序退出码不是 0: $($process.ExitCode)"
	}
	Write-Host "发布冒烟通过: $Mode ($binary)"
}
finally {
	if ($null -ne $process -and -not $process.HasExited) {
		Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
	}
	Remove-Item -LiteralPath $stdoutPath, $stderrPath -Force -ErrorAction SilentlyContinue
	if ($ownsProfile -and (Test-Path -LiteralPath $resolvedProfile)) {
		Remove-Item -LiteralPath $resolvedProfile -Recurse -Force -ErrorAction SilentlyContinue
	}
}
