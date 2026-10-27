# 第三方组件

本仓库只包含自有应用源码，不包含以下程序、运行库或模型。用户自行获取时请查看对应版本的许可与发布说明。

| 组件 | 用途 | 官方项目 |
| --- | --- | --- |
| yt-dlp | 视频信息及下载 | https://github.com/yt-dlp/yt-dlp |
| Deno | yt-dlp 所需的 JavaScript 运行环境 | https://github.com/denoland/deno |
| FFmpeg / FFprobe | 音视频合并、转换、检查 | https://ffmpeg.org/legal.html |
| faster-whisper / CTranslate2 | 本地语音识别 | https://github.com/SYSTRAN/faster-whisper 和 https://github.com/OpenNMT/CTranslate2 |
| Systran faster-whisper-large-v3 | 本地识别模型 | https://huggingface.co/Systran/faster-whisper-large-v3 |

现有本地绿色版使用的 FFmpeg 构建显示 `--enable-gpl --enable-version3`。如果将来公开发布含 FFmpeg 的完整压缩包或 EXE，需要根据所分发的**具体二进制构建**确认许可证、源代码提供和归属信息等义务。不要将完整绿色版文件夹直接提交为这个源码仓库。
