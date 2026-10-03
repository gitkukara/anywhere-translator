<h1>Anywhere Translator</h1>

Windows 全局划词翻译：选中文字 → 点击按钮 → 查看译文。不局限于浏览器。

[下载 v1.1.2](https://github.com/gitkukara/anywhere-translator/releases/tag/v1.1.2) · [更新说明](RELEASE_NOTES.md) · [使用说明](https://github.com/gitkukara/anywhere-translator/blob/main/docs/USAGE.md)

<img src="docs/screenshots/appearance.png" alt="浅色外观设置" width="360" />

## 特色

- 支持多种 API 接入： DeepSeek、智谱 GLM、小米 MiMo、千问 Qwen、OpenAI、Gemini ，还可自定义接口，API Key 在本机加密保存；
- 支持界面浅色、深色及跟随系统；
- 支持自定义翻译按钮的样式与出现位置；
- 支持开机自启动；
- 解压即用，已内置 .NET 运行时，无需另外安装。

## 使用

1. 下载 Windows x64 便携版，完整解压后运行 `TranslatorAnywhere.exe`。
2. 在“翻译服务”添加供应商，填写 API Key，刷新并选择模型，测试连接后设为当前服务。
3. 在其他应用中拖选文字，点击选区附近出现的按钮，弹出浮窗显示翻译；也可选中文字后按 `Ctrl+Alt+T`。
4. 点击翻译浮窗外部即可收起，钉住后保持显示。

在“外观”中切换界面主题、调整按钮样式和位置；右下角“点这里预览测试”可打开测试文字窗口。

**Zotero 取词**：在“其他设置”开启“兼容应用自动复制取词”，并在“兼容名单”填写 `zotero`。该方式会模拟复制来读取选中文字。

**使用演示（视频由AI制作，实际体验更丝滑）：**

[![15 秒操作示意，使用模拟译文](docs/demo/usage.gif)](https://github.com/gitkukara/anywhere-translator/releases/download/v1.0.0/Anywhere-Translator-demo.mp4)

## 更新

目前需要手动更新。已有版本的用户按以下步骤操作，首次安装直接看上方“使用”即可。

1. **退出旧程序**：在 Windows 任务栏右下角找到 Anywhere Translator 图标（可能藏在“∧”里），右键选择“退出”。只关闭设置窗口不会退出程序。
2. **下载新版**：打开 [最新版本页面](https://github.com/gitkukara/anywhere-translator/releases/latest)，下载名称为 `TranslatorAnywhere-v版本号-win-x64.zip` 的文件，不要下载 `Source code`。
3. **备份原目录**：找到原来的 `TranslatorAnywhere.exe` 所在文件夹，复制一份作为备份。里面的 `data` 文件夹保存了你的设置和加密后的 API Key，不能删除。
4. **覆盖程序文件**：先把新版 ZIP 解压到临时文件夹，再将其中全部文件复制到原程序目录。Windows 询问是否替换同名文件时，选择“替换”。不要只替换 EXE，也不要删除原来的 `data` 文件夹；官方发布包不包含这个文件夹。
5. **重新启动**：双击原目录中的 `TranslatorAnywhere.exe`。在同一台电脑、同一个 Windows 用户账户下，原有设置和 API Key 会继续使用，通常不需要重新填写。

例如：原程序位于 `D:\Apps\AnywhereTranslator\`，就把新版文件复制到这个目录，保留其中的 `D:\Apps\AnywhereTranslator\data\`。

如果提示文件正在使用，先确认已从托盘退出程序，再重试。若改用了新的程序目录，需要把旧 `data` 文件夹一起复制过去；启用过开机启动的用户，还应在新程序中关闭再开启“开机静默启动”，更新启动路径。

**图标说明**：如果 `data` 中有自定义的 `app.ico`，升级后仍优先使用它。想换回新版内置图标，可以退出程序后将它改名为 `app.ico.bak`，再启动程序。

**使用限制**：图片、扫描 PDF 暂不支持 OCR；部分应用可能需要兼容取词设置。Zotero 已在本机验证可用，福昕 PDF 编辑器兼容问题仍待完善。

## 开发

安装 .NET 10 SDK 后运行 `dotnet build TranslatorAnywhere.slnx -c Release`；打包使用 `./scripts/build.ps1`。

[MIT](LICENSE) · [第三方图标授权](THIRD-PARTY-NOTICES.md)
