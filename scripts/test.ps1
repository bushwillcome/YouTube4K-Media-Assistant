$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
& (Join-Path $root 'build.ps1')
if ($LASTEXITCODE -ne 0) { throw '构建检查失败。' }
$python = Join-Path $root 'dist\engine\.venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $python)) {
    $python = Join-Path $root 'engine\.venv\Scripts\python.exe'
}
if (-not (Test-Path -LiteralPath $python)) {
    $command = Get-Command py -ErrorAction SilentlyContinue
    if (-not $command) { throw '需要 Python 3.12 或已创建的本地虚拟环境来运行 Python 单元测试。' }
    $python = $command.Source
    $prefix = @('-3.12')
} else { $prefix = @() }
$oldPath = $env:PYTHONPATH
try {
    $env:PYTHONPATH = Join-Path $root 'engine'
    & $python @prefix -m unittest discover -s (Join-Path $root 'tests') -p 'test_*.py' -v
    if ($LASTEXITCODE -ne 0) { throw 'Python 单元测试失败。' }
} finally { $env:PYTHONPATH = $oldPath }
