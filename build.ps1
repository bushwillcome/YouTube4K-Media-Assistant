$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if ([Environment]::Is64BitOperatingSystem) {
    $compiler64 = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (Test-Path -LiteralPath $compiler64) { $compiler = $compiler64 }
}
if (-not (Test-Path -LiteralPath $compiler)) { throw '找不到 .NET Framework C# 编译器，请安装 .NET Framework 4.x 开发工具。' }
$dist = Join-Path $root 'dist'
$engine = Join-Path $dist 'engine'
New-Item -ItemType Directory -Force -Path $engine | Out-Null
$source = @('DownloadQueue.cs','MediaIntegration.cs','ToolEnvironment.cs','LoginVerification.cs','ChannelReader.cs','ChannelForm.cs','YouTube4KForm.cs') |
    ForEach-Object { Join-Path (Join-Path $root 'src') $_ }
& $compiler /nologo /utf8output /target:winexe /main:YouTube4K.Program "/out:$(Join-Path $dist 'YouTube4K素材助手.exe')" /r:System.Windows.Forms.dll /r:System.Drawing.dll /r:System.Web.Extensions.dll @source
if ($LASTEXITCODE -ne 0) { throw 'C# 编译失败。' }
Copy-Item -LiteralPath (Join-Path $root 'engine\media_worker.py') -Destination $engine
Copy-Item -LiteralPath (Join-Path $root 'engine\gpu_runtime.py') -Destination $engine
Write-Output "源码编译完成：$dist"
Write-Output '运行前请按 README 配置 yt-dlp、Deno、FFmpeg；如需音频或文字稿，还需配置 Python 和模型。'
