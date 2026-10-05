<#
.SYNOPSIS
把两个 Live2D 形象装进开发用的数据目录。

.DESCRIPTION
形象资源不随安装包发行，调试构建的数据目录默认是空的 —— 起实例只能看到
「本地 Live2D 模型不可用」，首次运行向导也走不到底。每次要起实例测试之前先跑这个。

目标布局与 ResourceManager 一致：<DataDir>/resources/installed/live2d/<模型 ID>/，
每个目录里必须**正好一个** *.model3.json，否则 ResourceManager.IsInstalled 判为未安装。

.PARAMETER DataDir
数据目录。默认是 Debug 构建的输出目录（应用按当前工作目录定位数据目录）。

.PARAMETER SourceRoot
形象来源目录，下面应有 Nori/ 与 ARGNori/ 两个子目录。默认取同级的 nori-theme 仓库。

.EXAMPLE
pwsh scripts/seed-dev-models.ps1
.EXAMPLE
pwsh scripts/seed-dev-models.ps1 -DataDir D:\ncn-workspace\nori-run\data
#>
[CmdletBinding()]
param(
	[string] $DataDir,
	[string] $SourceRoot
)

$ErrorActionPreference = 'Stop'

$desktopRoot = Split-Path -Parent $PSScriptRoot
if (-not $DataDir) {
	$DataDir = Join-Path $desktopRoot 'Nori.Desktop\bin\Debug\net10.0\data'
}
if (-not $SourceRoot) {
	# app/desktop → Nori.Desktop → ncn-workspace
	$workspace = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $desktopRoot))
	$SourceRoot = Join-Path $workspace 'nori-theme\live\sdk\Samples\Resources'
}

# 模型 ID 与来源目录名不同：ID 是 SupportedModelIds 里的固定值，来源目录用的是
# Cubism 导出时的名字。目标目录必须用 ID 命名，IsInstalled 是按 ID 拼路径的。
$models = @(
	@{ Id = 'nori';     Source = 'Nori' }
	@{ Id = 'arg-nori'; Source = 'ARGNori' }
)

$target = Join-Path $DataDir 'resources\installed\live2d'
New-Item -ItemType Directory -Force -Path $target | Out-Null

foreach ($model in $models) {
	$from = Join-Path $SourceRoot $model.Source
	if (-not (Test-Path $from)) {
		throw "形象来源不存在: $from（用 -SourceRoot 指定其它位置）"
	}

	$into = Join-Path $target $model.Id
	if (Test-Path $into) { Remove-Item -Recurse -Force $into }
	Copy-Item -Recurse -Force $from $into

	# 校验与 ResourceManager.IsInstalledAt 同口径：正好一个 model3.json。
	$manifests = @(Get-ChildItem -Path $into -Recurse -Filter '*.model3.json')
	if ($manifests.Count -ne 1) {
		throw "$($model.Id) 装入后有 $($manifests.Count) 个 model3.json，应为 1 个: $into"
	}

	$size = [math]::Round(((Get-ChildItem -Path $into -Recurse -File | Measure-Object -Property Length -Sum).Sum / 1MB), 1)
	Write-Output "$($model.Id) <- $from  ($size MB)"
}

Write-Output "已装入: $target"
