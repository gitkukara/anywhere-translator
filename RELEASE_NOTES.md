# v0.3.8

Anywhere Translator 的首个公开版本：在 Windows 桌面应用中选中文字，通过自己配置的 AI API 翻译。

## 功能

- 拖选或双击取词，选区旁显示翻译按钮；支持全局快捷键 `Ctrl+Alt+T` 和指定应用的复制降级。
- 支持 DeepSeek、OpenAI、Anthropic、Google Gemini 及自定义兼容接口。供应商实例、密钥、模型和当前服务独立管理，可主动刷新模型或测试连接。
- 流式翻译浮窗支持停止、重试、复制、置顶和快捷键，显示本次请求的供应商与模型。
- 可设置按钮文字、图案、自定义图片、颜色、大小、透明度和选区位置；设置实时预览并自动保存。
- API Key 使用 Windows 当前用户加密，默认不保存原文、译文或历史。
- 托盘后台运行，支持当前用户启动文件夹快捷方式登记，并尊重 Windows 启动应用的禁用状态。

## 下载与运行

从 [GitHub Releases](https://github.com/gitkukara/anywhere-translator/releases/tag/v0.3.8) 下载 `TranslatorAnywhere-v0.3.8-win-x64.zip`。需要 Windows x64 和 [.NET 10 Desktop Runtime 的 Windows x64 版本](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)。

完整解压后运行 `TranslatorAnywhere.exe`，保持随附文件在同一目录。首次运行需自行配置服务和 API Key。更新时保留 `data/` 目录；GitHub 的 `Source code` 压缩包需要先构建。

## 验证与限制

通过 293 项本地服务检查和 102 项界面模拟检查，测试使用独立目录与模拟服务，未使用真实 API Key 请求外部服务。

已验证启动快捷方式能被 Windows 启动项枚举识别，并可执行为不弹出设置的后台进程；尚未通过重启或重新登录验证登录触发。

取词依赖目标应用暴露可访问的文字选区。图片和扫描 PDF 暂无 OCR；部分自绘界面、高权限窗口、Word、微信、各类 PDF 阅读器、混合 DPI 多屏与复制降级仍需逐个实测。当前提供浅色界面。

本项目采用 [MIT 许可证](https://github.com/gitkukara/anywhere-translator/blob/v0.3.8/LICENSE)。第三方图标授权见 [THIRD-PARTY-NOTICES.md](https://github.com/gitkukara/anywhere-translator/blob/v0.3.8/THIRD-PARTY-NOTICES.md)，详细用法见 [README](https://github.com/gitkukara/anywhere-translator/blob/v0.3.8/README.md)。
