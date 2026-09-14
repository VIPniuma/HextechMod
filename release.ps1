#Requires -Version 5.1
<#
.SYNOPSIS
    一键发布到两个更新平台：自有更新服务器 + 雷霆商店。

.DESCRIPTION
    自动完成「每次更新同步双平台」：
      1. 读取 src/HextechMod/Plugin.cs 的 Version，patch +1（或 -Version 指定）
      2. 写回 Plugin.cs 与 HextechMod.csproj 的 <Version>，保证两平台版本号一致
      3. 调用 _tools/publish.ps1 -NoBump 发布到更新服务器
         （构建 dll + 上传 version.json/dll + 线上校验 sha256）
      4. git 提交版本变更、打 tag v<版本>、推送 main 与 tag
         → 触发 GitHub Actions 自动 dotnet pack + tcli publish 到雷霆商店

    凭据：
      - 更新服务器：Config.Build.user.props（SSH 私钥等，已被 .gitignore 忽略）
      - 雷霆商店：GitHub Actions Secret TCLI_AUTH_TOKEN（仅 CI 用，本机无需）

.EXAMPLE
    .\release.ps1 -Notes "· 新增 XX 强化；修复行李箱抽奖边界"

.EXAMPLE
    .\release.ps1 -Notes "..." -Version 0.4.0   # 指定版本号

.EXAMPLE
    .\release.ps1 -Notes "..." -DryRun          # 只走服务器发布预览，不提交/不打 tag
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Notes,

    [string]$Version,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$root       = Split-Path -Parent $PSScriptRoot
$pluginCs   = Join-Path $root 'src\HextechMod\Plugin.cs'
$modCsproj  = Join-Path $root 'src\HextechMod\HextechMod.csproj'
$publishPs1 = Join-Path $root '_tools\publish.ps1'

function Write-Step([string]$text) { Write-Host "`n=== $text ===" -ForegroundColor Cyan }
function Write-Ok([string]$text)   { Write-Host "  [ok] $text" -ForegroundColor Green }

function Read-ModVersion {
    $text = [System.IO.File]::ReadAllText($pluginCs)
    $m = [regex]::Match($text, 'public const string Version = "([0-9]+\.[0-9]+\.[0-9]+)"')
    if (-not $m.Success) { throw "在 $pluginCs 找不到 Version 常量。" }
    return $m.Groups[1].Value
}

function Add-PatchVersion([string]$value) {
    $parts = $value.Split('.')
    return '{0}.{1}.{2}' -f $parts[0], $parts[1], ([int]$parts[2] + 1)
}

# 源码文件带不带 BOM 要保持原样，避免 git 里多出「编码变了」的噪音。
function Write-TextPreservingBom([string]$path, [string]$text) {
    $bytes = [System.IO.File]::ReadAllBytes($path)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($hasBom)))
}

# --------------------------------------------------------------------------
# 版本号
# --------------------------------------------------------------------------
Write-Step '版本号'

$current = Read-ModVersion
$target  = if ($Version) { $Version } elseif ($DryRun) { $current } else { Add-PatchVersion $current }

Write-Host "  模组：$current -> $target"

if ($target -ne $current -and -not $DryRun) {
    $t1 = [System.IO.File]::ReadAllText($pluginCs) -replace 'public const string Version = "[0-9.]+"', "public const string Version = `"$target`""
    Write-TextPreservingBom $pluginCs $t1

    $t2 = [System.IO.File]::ReadAllText($modCsproj) -replace '<Version>[0-9.]+</Version>', "<Version>$target</Version>"
    Write-TextPreservingBom $modCsproj $t2

    Write-Ok 'Plugin.cs / HextechMod.csproj 已更新'
}

# --------------------------------------------------------------------------
# 发布到更新服务器
# --------------------------------------------------------------------------
Write-Step '发布到更新服务器'

$pubArgs = @('-Notes', $Notes, '-NoBump')
if ($DryRun) { $pubArgs += '-DryRun' }

& $publishPs1 @pubArgs
if ($LASTEXITCODE -ne 0) { throw '更新服务器发布失败，已中止（未打 tag）。' }

Write-Ok '更新服务器已发布'

if ($DryRun) {
    Write-Host "`n[DryRun] 未提交 git、未打 tag。" -ForegroundColor Yellow
    return
}

# --------------------------------------------------------------------------
# 提交 + 打 tag → 触发雷霆商店 CI
# --------------------------------------------------------------------------
Write-Step '提交并打 tag → 触发雷霆商店 CI'

git -C $root add $pluginCs $modCsproj
git -C $root commit -m "release: v$target`n`n$Notes" | Out-Null
git -C $root tag "v$target"
git -C $root push origin main --tags

Write-Ok "已推送 main + tag v$target，GitHub Actions 会自动发布到雷霆商店"

Write-Host "`n发布完成：v$target 已提交到两平台（更新服务器即时生效，雷霆商店待 CI 跑完）。" -ForegroundColor Green
