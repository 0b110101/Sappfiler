<#
.SYNOPSIS
    按当前版本号构建并打包发布产物，供交给 QA 调试。

.DESCRIPTION
    版本号唯一来源是仓库根 Directory.Build.props（VersionPrefix / VersionSuffix /
    FileVersion）。本脚本**从那里读**，不自己维护第二份 —— 避免"程序里显示 alpha18、
    包名叫 alpha17"这类不一致。

    产物：
      dist\GameTimeTracker-v<version>-win-x64\       解压后的可运行目录
      dist\GameTimeTracker-v<version>-win-x64.zip   交给 QA 的压缩包
      dist\GameTimeTracker-v<version>-win-x64\VERSION.txt  版本 + commit + 工作树状态

.EXAMPLE
    .\publish.ps1
    .\publish.ps1 -SkipTests
    .\publish.ps1 -Configuration Debug

.NOTES
    必须在**正常的 Windows 终端**里跑（不是 WorkBuddy 的自动化 shell）。
    后者的环境块缺少 PROGRAMDATA / ALLUSERSPROFILE / APPDATA 等变量，
    NuGet 会以 "Value cannot be null. (Parameter 'path1')" 失败 —— 详见 HANDOVER.md 第 5 节。
#>
[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
Set-Location $repoRoot

function Fail($msg) {
    Write-Host ""
    Write-Host "打包中止: $msg" -ForegroundColor Red
    exit 1
}

# ---- 1. 从 Directory.Build.props 读版本号（唯一来源） ----
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
if (-not (Test-Path $propsPath)) { Fail "找不到 $propsPath" }

[xml]$props = Get-Content $propsPath -Raw
$prefix = $props.Project.PropertyGroup.VersionPrefix
$suffix = $props.Project.PropertyGroup.VersionSuffix
$fileVersion = $props.Project.PropertyGroup.FileVersion

if (-not $prefix) { Fail 'Directory.Build.props 里没读到 VersionPrefix' }

$version = if ($suffix) { "$prefix-$suffix" } else { $prefix }

Write-Host "版本号: $version  (FileVersion=$fileVersion)" -ForegroundColor Cyan

# ---- 2. 一致性自检：三处版本号必须对齐 ----
# 这几处历史上漂移过，导致 exe 属性显示旧版本、QA 分不清手里的包。
$manifestPath = Join-Path $repoRoot 'src/GameTimeTracker.App/Package.appxmanifest'
if (Test-Path $manifestPath) {
    [xml]$manifest = Get-Content $manifestPath -Raw
    $manifestVersion = $manifest.Package.Identity.Version
    if ($manifestVersion -ne $fileVersion) {
        Fail "版本号不一致：Directory.Build.props 的 FileVersion=$fileVersion，但 Package.appxmanifest 的 Version=$manifestVersion。请把两者改成一致再打包。"
    }
    Write-Host "一致性检查通过: appxmanifest Version=$manifestVersion" -ForegroundColor Green
}

# 工作树有未提交改动时明确警告 —— QA 拿到包要能对上某个 commit。
# 注意用数组接住 git 输出并判空：git 在非仓库/无 git 时返回空而不是抛异常，
# 单靠 try/catch 接不住，$LASTEXITCODE 也不一定能拿到。
$gitDirty = $false
$gitCommit = 'unknown'
try {
    $commitOut = @(git rev-parse --short HEAD 2>$null)
    if ($commitOut.Count -gt 0 -and $commitOut[0]) { $gitCommit = "$($commitOut[0])".Trim() }

    $statusOut = @(git status --porcelain 2>$null)
    if ($statusOut.Count -gt 0) { $gitDirty = $true }

    if ($gitCommit -eq 'unknown') {
        Write-Host "警告: 读不到 git commit（不是 git 仓库，或 git 不在 PATH）。" -ForegroundColor Yellow
        Write-Host "      包里的 VERSION.txt 将标注 unknown，QA 无法据此定位代码。" -ForegroundColor Yellow
    }
} catch {
    Write-Host "警告: 读取 git 信息失败：$($_.Exception.Message)" -ForegroundColor Yellow
}

if ($gitDirty) {
    Write-Host "警告: 工作树有未提交改动，包里会含未入库代码，QA 无法据此定位问题。" -ForegroundColor Yellow
    Write-Host "      建议先 commit 再打包。" -ForegroundColor Yellow
}

# ---- 3. 测试 ----
if (-not $SkipTests) {
    Write-Host ""
    Write-Host "运行测试..." -ForegroundColor Cyan
    dotnet test (Join-Path $repoRoot 'tests/GameTimeTracker.Tests') -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) { Fail '测试未通过，不打包。（确实要跳过请用 -SkipTests）' }
    Write-Host "测试通过" -ForegroundColor Green
}

# ---- 4. 发布 ----
$outName = "GameTimeTracker-v$version-$RuntimeIdentifier"
$outDir = Join-Path $repoRoot "dist/$outName"

Write-Host ""
Write-Host "发布到 $outDir ..." -ForegroundColor Cyan
if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

dotnet publish (Join-Path $repoRoot 'src/GameTimeTracker.App') `
    -c $Configuration `
    -r $RuntimeIdentifier `
    --nologo `
    -o $outDir

if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish 失败' }

# ---- 5. 写 VERSION.txt ----
$versionFile = Join-Path $outDir 'VERSION.txt'
# 注意：不要在双引号 here-string 里写 $(if (...) {...} else {...})，
# Windows PowerShell 5.1 的词法分析器会报"缺少右括号"。先算好再插值。
$dirtyNote = if ($gitDirty) { '  (工作树有未提交改动)' } else { '' }
@"
GameTimeTracker $version
FileVersion    : $fileVersion
Commit         : $gitCommit$dirtyNote
Configuration  : $Configuration
Runtime        : $RuntimeIdentifier
打包时间       : $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')
"@ | Set-Content $versionFile -Encoding utf8

# ---- 6. 压缩 ----
$zipPath = Join-Path $repoRoot "dist/$outName.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }

Write-Host ""
Write-Host "压缩到 $zipPath ..." -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$zipSize = [math]::Round((Get-Item $zipPath).Length / 1MB, 1)

Write-Host ""
Write-Host "完成" -ForegroundColor Green
Write-Host "  $zipPath  ($zipSize MB)"
Write-Host ""
Write-Host "交给 QA 时请一并说明：本版改了什么、要重点验证哪些场景。" -ForegroundColor Cyan
