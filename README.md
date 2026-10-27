# YouTube4K 素材助手

Windows 图形界面的 YouTube 素材下载工具。可先收集单个视频、播放列表或频道中的任务，再手动开始下载。支持视频清晰度上限、音频、原话文字稿及本地语音识别。

## 功能

- 混合粘贴视频、播放列表和频道地址，读取后勾选；添加任务不会立即开始下载。
- 默认 1 个并发任务，可调整到 1～3；机器人验证错误会暂停新任务。
- 视频可选最高 8K、6K、4K、1440p、1080p、720p，实际清晰度取决于来源和可用格式。
- 可选择视频、MP3 音频、原话文字稿。优先使用原语言字幕；无可用字幕时，使用本地 faster-whisper 识别。
- 文字稿同时生成 `.txt`（纯文字，无时间码）和 `.md`（带时间码）；使用 TXT 不要求处理 Markdown。
- 登录验证默认不使用 Cookie。只有用户主动选择 Chrome、Edge 或本地 Cookie 文件时才读取对应凭据；不会记录 Cookie 内容。

## 登录验证怎么选

先在输入框放入一个视频链接，选择验证方式后点击「测试连接」。测试只读取视频信息，不会下载视频。测试通过后再添加批量任务并点击「开始全部下载」。

1. **不使用 Cookie（默认）**：能正常读取、下载时就保持这一项。若 YouTube 提示需要登录或机器人验证，再尝试下面的方式。
2. **Chrome**：使用 yt-dlp 读取本机 Chrome 的 Cookie。请先在 Chrome 登录 YouTube 并确认视频能打开；如果提示 Cookie 被占用或读取失败，可手动完全关闭 Chrome 后再测试。关闭浏览器也不能保证验证通过。
3. **Edge**：使用 yt-dlp 读取本机 Edge 的 Cookie。操作与 Chrome 类似；即使已登录，仍可能因 Cookie 读取、有效期或平台验证而失败。
4. **Cookie 文件**：先在 Chrome 登录 YouTube，打开目标视频网页；安装 Chrome 网上应用店的 [Get cookies.txt LOCALLY](https://chromewebstore.google.com/detail/get-cookiestxt-locally/cclelndahbckbenkjhflpdbgdldlbecc) 扩展，点击扩展导出 Netscape 格式的 `cookies.txt`。回到下载器选择「Cookie 文件」→「选择文件…」，选中刚导出的文件，再点击「测试连接」。这是一款浏览器扩展，**不是油猴脚本**；请认准名称中的 **LOCALLY**。这个方式在一些机器上更稳定，但文件过期、账号状态或 YouTube 验证变化时仍可能失败。

Cookie 文件包含登录凭据，请只保存在自己设备上，不要发送给别人或提交到 GitHub；不再需要时删除。下载器不会上传 Cookie，也不会在日志中输出其内容。

## 源码构建（Windows）

需要 Windows 10/11、.NET Framework 4.x 的 C# 编译器，以及 PowerShell。运行 `powershell -ExecutionPolicy Bypass -File .\build.ps1`，输出位于 `dist\YouTube4K素材助手.exe`。

下载器从 EXE 所在目录调用这些工具，需要用户自行从官方渠道取得并放在 `dist` 根目录：

- `yt-dlp.exe`
- `deno.exe`
- `ffmpeg.exe`、`ffprobe.exe`

软件会在开始下载前检查这些程序是否存在且能正常启动。工具版本可能随平台变化，请按各项目官方发布页面更新。

音频和文字稿还需要本机 Python 3.12。可在 `dist\engine` 下创建 `.venv`，安装本仓库的 `requirements.txt`；本地语音识别还需要将兼容的 faster-whisper large-v3 模型放在 `dist\engine\models\large-v3`，并配置匹配的 NVIDIA 显卡驱动、CUDA/cuDNN 运行库。现有绿色版的 `engine\runtime` 结构也仍受支持。仅生成视频时无需 Python 环境；音频或文字稿任务会检查 Python 环境。

示例：

```powershell
py -3.12 -m venv .\dist\engine\.venv
.\dist\engine\.venv\Scripts\python.exe -m pip install -r .\requirements.txt
powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1
```

如果模型或 GPU 运行库缺失，有可用原语言字幕的视频仍可直接生成文字稿；无字幕的视频需要完成本地识别环境配置。项目不提供模型、Python 运行时或第三方可执行文件。

## 隐私与安全

Cookie 选择只影响当前运行。请勿将 Cookie 文件、浏览器配置、下载日志、个人媒体或本机设置提交到 Git。仓库的 `.gitignore` 已排除常见输出，但提交前仍应检查 `git status`。只下载自己有权使用的内容，并遵守平台条款和适用法律。

## 测试

`powershell -ExecutionPolicy Bypass -File .\scripts\test.ps1` 会编译图形程序并运行离线 Python 单元测试。它不证明真实 YouTube 下载、字幕可用性或 GPU 转写一定成功；这些需在目标机器上分别验证。

## 授权

本仓库自有代码采用 MIT License。yt-dlp、Deno、FFmpeg、faster-whisper、模型等各自遵循其原始许可，详见 [THIRD_PARTY.md](THIRD_PARTY.md)。仓库没有包含这些第三方程序或模型。
