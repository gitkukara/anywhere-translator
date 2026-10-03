# Anywhere Translator

Windows 桌面划词翻译工具，当前版本为 1.1.1。

鼠标拖选或双击文字后，在选区旁显示按钮。点击按钮打开翻译窗口，并通过用户配置的 AI API 返回译文。程序独立运行，无需浏览器扩展。

[下载最新版本](https://github.com/gitkukara/anywhere-translator/releases/latest) · [v1.1.1 发布说明](../RELEASE_NOTES.md) · [GitHub 仓库](https://github.com/gitkukara/anywhere-translator)

<img src="screenshots/appearance.png" alt="浅色外观设置" width="360" />

界面示意截图使用模拟译文。

## 下载与运行

运行环境为 Windows x64。正式发布的完整便携版已内置 .NET 10 Desktop Runtime，解压即可运行，无需另外安装 .NET。

在 [Releases](https://github.com/gitkukara/anywhere-translator/releases) 下载 `TranslatorAnywhere-v1.1.1-win-x64.zip`，完整解压到可写入的目录，然后运行 `TranslatorAnywhere.exe`。EXE、DLL、JSON 和随附文档应保存在一起。程序使用便携目录保存配置，首次运行需自行配置 API 服务和密钥。

GitHub 的 `Source code` 压缩包是源码，需要先构建才能运行；开发步骤见下方“开发与构建”。

## 开始使用

1. 运行解压目录中的 `TranslatorAnywhere.exe`，在设置页配置服务和按钮外观。关闭设置窗口后，程序继续在系统托盘运行；双击托盘图标可重新打开设置。
2. 在“翻译服务”添加供应商，填写 API Key，刷新模型列表或手动填写模型名称，将该供应商设为当前翻译服务。设置自动保存，已有旧版 DeepSeek 配置会自动迁移。
3. 在其他应用中拖选文字，点击选区旁的按钮。也可双击选词，或选中后按 `Ctrl+Alt+T`。
4. “外观”右下角的“点这里预览测试”会打开独立文本窗口，验证实际按钮样式、位置和取词交互。

## 设置界面与后台运行

设置分为“外观／翻译服务／其他设置”，采用紧凑布局。窗口默认 520 × 560，最小 480 × 440；标签靠左，控件靠右，短输入文字居中，下拉文字靠左，宽度按最长选项收紧。外观选项可滚动，右下角测试按钮固定显示。滚动条位于右侧外边距，出现时不压缩卡片。在“外观 → 界面主题”通过横排档位选择“跟随系统／浅色／深色”，默认跟随系统。选择后自动保存并实时切换；跟随系统读取 Windows 的应用颜色模式，系统变化时自动更新。设置、翻译窗口、输入框、下拉菜单和托盘菜单使用同一套配色；自定义浮动按钮颜色保持独立。

有效设置在停止输入约 0.6 秒后自动保存，关闭窗口前也会立即保存待处理的修改。输入无效或写入失败时保留上一次有效配置，显示简短提示；修正后继续自动保存。开关仅点击右侧开关本身切换，也支持 Tab 和空格操作。目标语言固定为简体中文、繁体中文、English、日本語和한국어五项。

界面操作图标统一采用 Hugeicons Stroke Rounded。翻译浮窗提供中文悬停提示和无障碍名称；复制成功后短暂显示蓝色对勾。添加服务与服务详情的返回箭头和文字组成完整点击区域。控件不显示操作后的高亮边框，保留键盘导航、文本光标和选区反馈。

开机启动使用当前用户“启动”文件夹中的 `TranslatorAnywhere.lnk`，保存后核对程序路径、后台参数和工作目录。已开启的旧版登记会迁移并清理本应用的旧 Run 项；重新打开设置会刷新实际启动状态，Windows 已禁用的项目会显示提示。

### 翻译服务与供应商

参考 magpie 的供应商管理方式，已配置的供应商以列表呈现：品牌图标、名称、接口域名、模型数量、密钥配置状态、启用开关及详情入口。添加页面直接展示七个预设，不设搜索框，同一供应商可以配置多份独立实例。

- 添加目录只保留 DeepSeek、智谱 GLM、小米 MiMo、千问 Qwen、OpenAI、Google Gemini 和自定义接口。其他兼容服务可通过自定义名称、API 地址和模型接入。
- 支持 OpenAI Chat Completions 兼容接口及 Anthropic Messages 接口；Gemini 使用其官方 OpenAI 兼容接口。本地服务允许不填密钥。
- 每个实例保存自己的地址、密钥、模型列表和选定模型。刷新会自动读取模型名称并保存，优先按接口创建时间倒序排列；未提供时间时按名称中的数字自然倒序排列（例如 model-10 在 model-9 前），不代表已验证发布日期。刷新保留当前选定模型。刷新模型列表和测试连接仅在主动点击时执行；测试连接发送固定测试文字，可能产生少量 API 用量。
- 模型发现失败时可以手动输入模型 ID，原有模型不会因刷新失败而被清空。有密钥只表示已配置，连接是否可用以测试结果为准。
- “启用”控制供应商是否可用于翻译，“用于翻译”明确选择当前供应商。停用当前供应商会提示重新选择，不会自动改用其他服务。
- 翻译浮窗底部显示本次请求的供应商和模型，悬停可查看服务地址；这是本次请求的配置，不是服务端模型身份的独立认证。

本版接入的是 API 服务，不包含 magpie 的订阅账户登录、代理网关或 Agent 配置管理。

设置窗口集中管理按钮外观、选区位置、翻译服务和取词兼容。翻译浮窗仅显示译文，内容区域宽约 380 个逻辑像素，高度随内容变化；长译文支持滚动。顶部可拖动、钉住和打开设置；未钉住时点击浮窗外部会自动收起，钉住后保持显示并置顶，底部可复制和重试；停止按钮仅在请求中出现。

在“其他设置”中启用“开机静默启动”后，程序会自动保存并在当前用户的“启动”文件夹创建 `TranslatorAnywhere.lnk`。可通过文件资源管理器的 `shell:startup` 查看该快捷方式。下次登录 Windows 时使用 `--background` 驻留托盘，不会弹出设置窗口；已在运行时也不会弹出重复启动提示。默认不会登记启动项，也不需要管理员权限。若在 Windows 启动应用中禁用了本程序，请先在那里重新启用；应用不会绕过该禁用状态。

移动便携程序目录后，在新目录运行 EXE，重新启用“开机启动”，以更新快捷方式路径。从托盘选择“退出”会结束当前运行，已启用的开机启动仍会在下次登录时生效；要取消它，应关闭设置中的开关，程序会移除本应用的启动快捷方式和旧 Run 登记。

## 按钮与位置

- 支持文字（默认“翻”）、内置翻译图案，以及导入 PNG、JPG、JPEG、ICO、BMP、GIF。
- 按钮大小使用“较小／标准／较大／最大”四档，对应 24／32／40／48 个逻辑像素，颜色使用 `#RRGGBB`，例如 `#3390EC`。旧版颜色前面的 `FF` 表示完全不透明，新版显示和保存六位颜色值。
- 颜色后通过横排档位选择 25%／50%／75%／100%，100% 完整显示，25% 最淡。文字、图案和自定义图片统一应用，预览实时更新并自动保存。旧颜色中的透明度会迁移到此设置。旧版非档位数值保持原显示效果，选择新档位后才更改；配置文件仍保存原来的透明度定义。
- 显示方式使用“文字／符号／自设”横排选择；相对选区使用“左上／左下／右上／右下”四个档位，横向、纵向偏移可单独设置；超出屏幕时自动限制在当前显示器工作区。旧版鼠标附近定位在选择新方位前保留。
- 外观页可选中文字，并实时预览按钮位置；修改自动应用到新的取词操作。
- 译文支持流式显示、停止、重试、复制、置顶和 Esc 收起。Ctrl+R 重新翻译；Ctrl+C 在选中译文时复制选区，未选中时复制整段。

## 更换图标

三类图标分别设置：

| 需要更换的图标 | 设置方式 | 生效时间 |
| --- | --- | --- |
| 选中文字旁的浮动按钮 | 在“外观”选择“自定义图片”，点击“导入” | 下一次取词 |
| 系统托盘、设置窗口、翻译窗口和窗口任务栏图标 | 将 ICO 文件放入数据目录，命名为 `app.ico` | 从托盘退出并重新运行后 |
| 文件资源管理器中 EXE 文件自身的图标 | 替换源码 `src/TranslatorAnywhere/Assets/app.ico`，重新构建 | 使用新构建的 EXE 后 |

浮动按钮支持 PNG、JPG、JPEG、ICO、BMP 和 GIF，文件不超过 5 MB、单帧尺寸不超过 2048 × 2048。导入后会复制到 `data/icons/`，修改应用图标不会修改浮动按钮设置。

应用图标的默认位置为程序目录下的 `data/app.ico`；如果设置了 `TRANSLATOR_ANYWHERE_DATA_DIR`，则使用该目录下的 `app.ico`。文件必须是真正的 ICO 格式，不超过 5 MB，最多 32 帧，每帧尺寸不超过 256 × 256。建议包含 16、20、24、32、48、64、128、256 像素，以适应不同 DPI。文件损坏时会回退到内置图标，删除覆盖文件后重启也会恢复默认图标。

运行时覆盖不会修改 EXE 内嵌资源，因此文件资源管理器仍显示构建时的图标。内置图标使用自定义图稿，原稿保存为 `Assets/app-source.png`，预览为 `Assets/app-256.png`；窗口和托盘从同一份 ICO 加载，图标文件读取后即可替换，不会持续被程序锁住。

在 Photoshop 中完成正方形图稿后，可以导入 PNG 或 JPG 并生成多尺寸 ICO。PNG 的透明度会保留，JPG 按原图保留背景：

```powershell
.\scripts\import-icon.ps1 -ImagePath 'C:\Icons\app.png'
.\scripts\build.ps1
```

也可以用以下命令重新生成文字图标，然后构建：

```powershell
.\scripts\generate-icon.ps1 -Label '翻' -Color '#3390EC'
.\scripts\build.ps1
```

也可以直接用自己的多尺寸 ICO 替换 `Assets/app.ico` 后运行构建脚本。构建会保留现有 `dist/win-x64/data/`，生成的版本压缩包不包含配置、API Key、用户图标或日志。更新程序时保留原来的 `data/` 目录；如果目标程序正在运行，应由用户先从托盘退出，再替换发布文件。Windows 可能暂时缓存 EXE 的旧图标，这不影响托盘和窗口读取新的图标。

## 取词与兼容

默认通过 Windows UI Automation 直接读取选区，不修改剪贴板。是否能获取文字取决于目标应用是否暴露可访问的文字选区，因此“全局”指在多个应用中工作，不能保证任意窗口都可读取。

UIA 失败时，全局快捷键可使用模拟复制降级。在“其他设置”中，也可以开启指定应用的自动复制降级，并填写进程名，例如 `SumatraPDF`。自动降级仅对名单中的应用生效；进程名可带或不带 `.exe`。

复制降级会检查前台窗口、焦点、按键状态与剪贴板序号。原始剪贴板存在无法备份的私有格式、被占用、控件密码属性未知或查询超时时，会放弃模拟复制。终端程序不会被注入 Ctrl+C。

图片文字、扫描 PDF、受保护内容、部分自绘界面和高权限窗口仍有兼容限制。本版聚焦可选中文字的主流程，尚未集成截图 OCR。UIA 调用可能被目标应用阻塞，应用使用单个后台线程和有界队列控制影响；持续无法取词时可从托盘退出后重新启动。

## 数据保存

配置保存在程序所在目录的 `data/settings.json`。导入图标会复制到 `data/icons/`，删除原始图片不影响使用。

每个供应商实例的 API Key 单独保存在 `data/api-key.<实例 ID>.dpapi`，由 Windows 当前用户加密，普通设置文件不包含密钥。复制到其他 Windows 账户或电脑后，应重新填写 Key。旧版首次升级会备份原设置文件，并将原 `api-key.dpapi` 密文迁移到对应实例；原有地址、模型及外观设置保持不变。

默认不保存原文、译文和翻译历史。诊断日志不记录完整正文、密钥或服务返回的错误原文。程序直接访问用户配置的接口，在主动翻译时发送选中文字；主动刷新模型会查询模型接口，测试连接会发送固定的测试文字。打开设置或切换菜单不会发起 API 请求。

可通过环境变量 `TRANSLATOR_ANYWHERE_DATA_DIR` 指定数据目录。`--background` 启动后仅显示托盘；`--fixture` 启动独立测试文本窗口。

## 开发与构建

在 Windows x64 上安装 [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)。项目采用 WPF + Win32 + UI Automation，无第三方 NuGet 依赖。SDK 包含运行开发版本所需的 Desktop Runtime。

获取源码并构建后运行：

```powershell
git clone https://github.com/gitkukara/anywhere-translator.git
cd anywhere-translator
dotnet build TranslatorAnywhere.slnx --configuration Release
dotnet run --project src/TranslatorAnywhere/TranslatorAnywhere.csproj --configuration Release
```

本地回归检查与打包：

```powershell
dotnet run --project tests/TranslatorAnywhere.Tests/TranslatorAnywhere.Tests.csproj --configuration Release
dotnet run --project tests/TranslatorAnywhere.UiChecks/TranslatorAnywhere.UiChecks.csproj --configuration Release
.\scripts\build.ps1
.\scripts\run.ps1
```

默认打包输出为 `dist/win-x64/` 和版本 ZIP，包含 .NET 运行时、WPF 及语言资源。首次构建可能需要联网下载微软运行时组件。`scripts/build.ps1 -FrameworkDependent` 可选生成依赖本机 .NET 的轻量版，其输出目录和 ZIP 名称带 `-framework-dependent`，不会与完整版混用。`-SkipDeploy` 只生成发布包，不覆盖本地运行目录。

## 已完成的验证

- Release 构建与 Windows 发布版本生成。
- 307 项本地服务检查：DPAPI 加密、主题偏好保存与旧配置兼容、旧版配置与颜色透明度迁移、固定目标语言、独立密钥与保存失败回滚、模型发现、OpenAI／Anthropic 请求和流式响应、取消、错误信息、图标验证及不同 DPI 位置计算。其中 64 项启动检查覆盖快捷方式参数、工作目录、旧登记迁移、Windows 禁用与未知状态，以及隔离目录中的真实 COM 快捷方式读写；自动测试不操作真实启动文件夹或注册表。
- 134 项界面模拟检查覆盖外部点击收起、钉住保留、收起时取消翻译、主题实时切换、模拟系统主题通知、手动模式优先级、深色文本对比度、主题保存恢复、窄窗口布局、预览纵向滚动、最小尺寸下各页控件可达与长地址／模型编辑、统一矢量图标与无障碍名称、复制成功反馈及计时恢复、无原文展示、返回导航、六位颜色与旧颜色兼容、透明度实时预览与保存恢复、实际按钮悬停透明度、固定语言选项、供应商切换、自动保存防抖、关闭前保存、未修改或恢复原值不写入、非法输入与写入失败、密钥清除、模型选择与手输、取消过期请求及图标加载。截图生成到 `artifacts/ui-v1.1.1/`，包含语言下拉框展开状态及深色设置、供应商、翻译窗口和原生标题栏；测试使用模拟服务和独立目录，不读取用户配置或密钥。
- 新版实际窗口已检查三项导航、7 项供应商目录、详情入口、模型刷新与连接测试反馈；网络操作使用本地模拟服务。
- 1.1.1 已实际创建当前用户启动快捷方式，Windows 启动项枚举能够识别，旧 Run 登记已移除。已执行该快捷方式并确认单个 `--background` 进程完成启动，未弹出设置窗口。尚未通过重启或重新登录 Windows 验证登录触发。
- 第一版实际桌面测试：在独立 WPF 进程拖选文字后，按钮出现在选区旁，原选区保留。

未使用真实 API Key 请求外部服务。Word、微信、各类 PDF 阅读器、混合 DPI 多屏和剪贴板降级的兼容情况仍需逐个实测，不能视为已验证。

## 参考项目

架构与交互研究参考 [NextAI Translator](https://github.com/nextai-translator/nextai-translator)、[Pot](https://github.com/pot-app/pot-desktop)、[CopyTranslator](https://github.com/CopyTranslator/CopyTranslator)。供应商管理参考 [magpie](https://github.com/yetone/magpie) 的预设目录、独立实例、启用和模型管理设计。本项目采用独立的 Windows 原生实现。

供应商品牌图标来自 magpie 收录的 [LobeHub Icons](https://github.com/lobehub/lobe-icons)，授权与来源见 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

普通界面图标来自 [Hugeicons](https://hugeicons.com) 官方免费包 `@hugeicons/core-free-icons` 4.3.5，使用 MIT 授权。所选 SVG、来源清单保存在 `src/TranslatorAnywhere/Assets/Hugeicons/`，通过共享 `HugeIcon` 组件渲染为 WPF 矢量图，无需联网加载。后续新增界面图标优先选择同一套 Stroke Rounded 图形。

Windows 取词能力依据 [UI Automation 选区接口](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nf-uiautomationclient-iuiautomationtextpattern-getselection)，AI 接入依据 [DeepSeek 官方文档](https://api-docs.deepseek.com/)。

## 许可证

本项目使用 [MIT 许可证](../LICENSE)，Copyright (c) 2026 gitkukara。第三方图标的来源与授权单独列于 [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md)。

### 文档应用兼容

Zotero：在“其他设置”开启“兼容应用自动复制取词”，并在兼容名单填写 `zotero`，每行一个进程名。本机已验证可用；采用辅助功能检查与模拟复制，现有配置升级后保持原样，不会自动开启。福昕 PDF 编辑器兼容仍未验证成功，暂列为已知限制。
