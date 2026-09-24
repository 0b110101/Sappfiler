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
    $testsPath = Join-Path $repoRoot 'tests/GameTimeTracker.Tests'
    if (Test-Path $testsPath) {
        Write-Host ""
        Write-Host "运行测试..." -ForegroundColor Cyan
        dotnet test $testsPath -c $Configuration --nologo
        if ($LASTEXITCODE -ne 0) { Fail '测试未通过，不打包。（确实要跳过请用 -SkipTests）' }
        Write-Host "测试通过" -ForegroundColor Green
    }
}

# ---- 4. 发布 ----
$outName = "Sappfiler-v$version-$RuntimeIdentifier"
$outDir = Join-Path $repoRoot "dist/$outName"

Write-Host ""
Write-Host "发布到 $outDir ..." -ForegroundColor Cyan

# 检查是否有正在运行的 Sappfiler / GameTimeTracker 进程占用输出目录中的文件
$targetExe = Join-Path $outDir 'Sappfiler.exe'
$targetExeLegacy = Join-Path $outDir 'GameTimeTracker.exe'
$runningProcs = Get-Process -Name "Sappfiler*", "GameTimeTracker*", "GameTimeTracker.App*" -ErrorAction SilentlyContinue |
    Where-Object { 
        try { $_.Path -eq $targetExe -or $_.Path -eq $targetExeLegacy } catch { $true }
    }

if ($runningProcs) {
    Write-Host "检测到目标目录中的应用正在运行，正在关闭进程以释放文件锁..." -ForegroundColor Yellow
    foreach ($p in $runningProcs) {
        try {
            $p.CloseMainWindow() | Out-Null
            Start-Sleep -Milliseconds 500
            if (-not $p.HasExited) {
                Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
            }
        } catch { }
    }
    Start-Sleep -Seconds 1
}

if (Test-Path $outDir) { Remove-Item $outDir -Recurse -Force }

# 这些参数**显式传**，不依赖 csproj 的默认值 —— 它们任何一项搞错都会产出
# "能生成、但一启动就崩"的包，而且崩在 XAML 里、报错和原因完全对不上。
#   PublishTrimmed=false         WinUI 3 不支持裁剪（见 csproj 里的详细说明）
#   WindowsAppSDKSelfContained   把 WinUI 的 native 实现打进包里，
#                                否则依赖机器上装没装 WindowsAppRuntime
#   SelfContained                自带 .NET 运行时，QA 机器无需预装
dotnet publish (Join-Path $repoRoot 'src/GameTimeTracker.App') `
    -c $Configuration `
    -r $RuntimeIdentifier `
    -p:PublishTrimmed=false `
    -p:SelfContained=true `
    -p:WindowsAppSDKSelfContained=true `
    -p:PublishReadyToRun=false `
    --nologo `
    -o $outDir

if ($LASTEXITCODE -ne 0) { Fail 'dotnet publish 失败' }

# ---- 3b. 把使用文档放进包 ----
# QA / 用户拿到 zip 后应能直接看说明，不必回头找仓库。
# README 是给使用者的（配置 Notion、更新方法、常见问题），
# CHANGELOG 是给 QA 的（本版改了什么 / 重点验什么 / 已知问题）。
$docCopies = @(
    @{ Src = (Join-Path $repoRoot 'README.md');        Dst = 'README.md' },
    @{ Src = (Join-Path $repoRoot 'CHANGELOG-QA.md');  Dst = '更新说明.md' }
)
foreach ($d in $docCopies) {
    if (Test-Path $d.Src) {
        Copy-Item $d.Src (Join-Path $outDir $d.Dst) -Force
    } else {
        Write-Host "  提示: 未找到 $($d.Src)，跳过" -ForegroundColor Yellow
    }
}
Write-Host "已随包附带 README.md 与 更新说明.md" -ForegroundColor DarkGray

# 清理非必需的 pdb 调试符号文件
Get-ChildItem $outDir -Filter "*.pdb" | Remove-Item -Force

# ---- 4a. 清理多余的语言资源目录 ----
# WinUI 的 native 库自带 86 个语言的 .mui 卫星资源，默认全被复制进来，
# 于是包根目录多出 86 个语言文件夹（约 3.7MB）。它们只影响 WinUI **内部**
# 字符串（主要是 XAML 报错）的本地化，与本程序界面无关（界面走 App.pri）。
#
# csproj 里已设 SatelliteResourceLanguages 做同样的事，但那只对 NuGet 卫星资源生效；
# WinUI 的 .mui 是 WindowsAppSDK 目标复制的内容文件，不保证被它过滤，所以这里兜底。
#
# 只删「目录名像语言代码」**且**「里面只有 .mui」的目录 ——
# 顶层的 .pri（App.pri / Microsoft.UI.Xaml.Controls.pri 等）是必需应用资源，绝不能被误删。
$keepLocales = @('zh-CN', 'en-us', 'en-US', 'zh-Hans', 'zh-Hant')
$removedLocales = @()
foreach ($dir in Get-ChildItem $outDir -Directory) {
    if ($keepLocales -contains $dir.Name) { continue }

    # 目录名必须是语言代码形态：xx / xx-YY / xx-Script-YY
    #   ja-JP、zh-CN、sr-Cyrl-RS、ca-Es-VALENCIA
    # 不匹配 Assets / data / Microsoft.UI.Xaml.Resources 这类正常目录。
    if ($dir.Name -notmatch '^[a-zA-Z]{2,3}(-[a-zA-Z]{2,4})?(-[a-zA-Z]{2,8})?$') { continue }

    $files = Get-ChildItem $dir.FullName -File -Recurse
    if ($files.Count -eq 0) { continue }
    # 里面必须**只有** .mui —— 有任何其它文件就跳过，宁可留着也不误删
    if ($files | Where-Object { $_.Extension -ne '.mui' }) { continue }

    Remove-Item $dir.FullName -Recurse -Force
    $removedLocales += $dir.Name
}

if ($removedLocales.Count -gt 0) {
    Write-Host "已清理 $($removedLocales.Count) 个多余语言目录（保留 zh-CN / en-us）" -ForegroundColor DarkGray
}

# ---- 4b. 产物校验：拦下"能生成但跑不起来"的包 ----
# 2026-09-18 就是栽在这里：Release 裁剪把 WinUI native 和 WinRT 投影删掉了，
# 包正常生成、测试也全过，但程序一启动就崩。这种问题必须在打包阶段拦住，
# 否则 QA 拿到的是个必崩的包。
$requiredFiles = @(
    'Microsoft.UI.Xaml.dll',              # WinUI XAML 的 native 实现
    'Microsoft.UI.Xaml.Controls.dll',
    'Microsoft.WinUI.dll',                # WinRT 投影
    'Microsoft.WindowsAppRuntime.Bootstrap.dll',
    'Microsoft.WindowsAppRuntime.dll',    # 运行时本体（改依赖集后补入，防止漏引）
    'CoreMessagingXP.dll',
    'DWriteCore.dll',
    'Sappfiler.dll',
    'hostpolicy.dll',
    'coreclr.dll',
    'Sappfiler.pri',            # 应用资源（界面文案/资源），删了界面会出问题
    'Microsoft.UI.Xaml.Controls.pri',
    'Microsoft.WindowsAppRuntime.pri'
)
$missing = @()
foreach ($f in $requiredFiles) {
    if (-not (Test-Path (Join-Path $outDir $f))) { $missing += $f }
}

if ($missing.Count -gt 0) {
    Write-Host ""
    Write-Host "产物校验失败：缺少以下关键文件" -ForegroundColor Red
    foreach ($f in $missing) { Write-Host "  - $f" -ForegroundColor Red }
    Fail @"
缺文件通常意味着 PublishTrimmed 被打开了（WinUI 3 不支持裁剪），
或 WindowsAppSDKSelfContained / SelfContained 没生效。
这样的包能生成但一启动就崩，不要交给 QA。
请检查 GameTimeTracker.App.csproj 的 Publish Properties 段。
"@
}

$fileCount = (Get-ChildItem $outDir -File -Recurse | Measure-Object).Count

# ---- 4b-2. 资源 PRI 校验：拦下"应用 PRI 没合并框架资源"的包 ----
# 2026-09-19 事故：把 WindowsAppSDK 元包拆成 6 个子包后，应用 PRI 不再合并框架的 PRI。
# 包能生成、110 个测试全过，但**一启动就崩**：
#   XamlParseException: Cannot locate resource from
#   'ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml'
# 更阴险的是它被增量构建掩盖了 —— 旧的合并版 PRI 一直留在 bin/ 里被沿用，
# 直到某次全新编译才暴露，于是"上一版还好好的，这一版突然起不来"。
#
# 判据：合并后 Sappfiler.pri ≈ 2.2MB；未合并只有 ~43KB。
# 阈值取 1MB，两侧都留足余量。
$priPath = Join-Path $outDir 'Sappfiler.pri'
if (Test-Path $priPath) {
    $priKB = [math]::Round((Get-Item $priPath).Length / 1KB)
    if ($priKB -lt 1024) {
        Write-Host ""
        Write-Host "产物校验失败：Sappfiler.pri 只有 ${priKB}KB，框架资源没被合并进去" -ForegroundColor Red
        Fail @"
正常应在 2MB 量级（里面合并了 WinUI 的 Themes/themeresources.xaml）。
只有几十 KB 时程序会**一启动就崩**，报：
  XamlParseException: Cannot locate resource from
  'ms-appx:///Microsoft.UI.Xaml/Themes/themeresources.xaml'

常见原因：GameTimeTracker.App.csproj 里没有引用 Microsoft.WindowsAppSDK
元包（改成子包引用会让 PRI 不合并）。
请把该依赖改回元包后重新打包。
"@
    }
    Write-Host "  资源 PRI 已合并（${priKB}KB）" -ForegroundColor DarkGray
}

Write-Host "产物校验通过（$fileCount 个文件，关键 WinUI 组件齐全）" -ForegroundColor Green

# ---- 4c. ReadyToRun 校验（防御性，非已证实的根因）----
# 2026-09-19 曾有 1 轮把「关窗口再打开就崩」归因于 R2R，**那个结论后来被推翻了**
# （用户实测非 R2R 的 alpha22 照样崩；真正来源是隐藏窗口时拆掉了页面，见
#  App.csproj / MainWindow 里的说明）。这里保留校验，只为两点：
#   1. R2R 会显著增大体积，本程序启动路径没有需要它的热点；
#   2. 当初观察到 R2R 版确实更容易崩，在没查清前不放开。
# 如果你确认要开 R2R，把下面 $r2rChecks 清空即可 —— 它不是硬性正确性要求。
#
# 判据：预编译后程序集体积约为不开启时的 2 倍。
#   App.dll：R2R ~736KB ／ 非 R2R ~400KB   → 阈值取 600KB
#   Core.dll：R2R ~168KB ／ 非 R2R ~71KB
# 用体积而不是翻 PE 头，是因为它足够稳、且失败时能直接看出原因。
$r2rChecks = @(
    @{ Name = 'Sappfiler.dll';     MaxKB = 600 },
    @{ Name = 'Sappfiler.Core.dll'; MaxKB = 200 }
)
$r2rHit = @()
foreach ($c in $r2rChecks) {
    $p = Join-Path $outDir $c.Name
    if (Test-Path $p) {
        $kb = [math]::Round((Get-Item $p).Length / 1KB)
        if ($kb -gt $c.MaxKB) { $r2rHit += "$($c.Name) = ${kb}KB（上限 $($c.MaxKB)KB）" }
    }
}

if ($r2rHit.Count -gt 0) {
    Write-Host ""
    Write-Host "产物校验失败：像是开着 ReadyToRun 发布的" -ForegroundColor Red
    foreach ($h in $r2rHit) { Write-Host "  - $h" -ForegroundColor Red }
    Fail @"
ReadyToRun 会让「关掉界面再从托盘打开」稳定崩溃（原生层，日志空白），
不要交给 QA。请确认：
  · GameTimeTracker.App.csproj 里 PublishReadyToRun 是恒定 False
  · publish.ps1 的 dotnet publish 带了 -p:PublishReadyToRun=false
"@
}

# ---- 5. 写 VERSION.txt ----
$versionFile = Join-Path $outDir 'VERSION.txt'
# 注意：不要在双引号 here-string 里写 $(if (...) {...} else {...})，
# Windows PowerShell 5.1 的词法分析器会报"缺少右括号"。先算好再插值。
$dirtyNote = if ($gitDirty) { '  (工作树有未提交改动)' } else { '' }
@"
Sappfiler $version
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
