# 多模态OCR 验证记录

## 已验证的运行能力

- 插件实现 STranslate `IOcrPlugin`，保持稳定 `PluginID` `b6b8a6c7-6202-4c76-8cb1-46c9cbaeb8a5`。
- 内置验证图片优先从插件 DLL 目录读取，文件不可用时回退到宿主基目录和程序集嵌入资源。验证图片 SHA-256 为 `4CF5D0A415032E9649FFE72EBDC2C76C4107A1C5958EF28E902F18BBB4B189AD`。
- 真实 MiniMax 与 GPT 兼容服务曾返回 HTTP `200` 的非空 SSE 结果，识别内容包括 `STranslate Vision OCR`、`Validation 2026` 与 `ABC 123`。
- 正式 OCR 的 BMP 输入通过 WPF 解码器无损标准化为 PNG，发送请求使用标准化后的图片 MIME 与字节哈希。
- 流式请求使用相邻响应空闲超时，非流式请求使用总超时。模型返回空内容、HTTP 失败与解析失败都会进入重试。
- 成功结果只包含纯文本。失败结果保留脱敏错误文本，防止 STranslate 宿主结果区域空白。

## 品牌与发布验证

- `plugin.json`、语言资源、设置页、默认提示词、日志前缀、README 和本记录统一使用“多模态OCR”。
- 提示词区域标题为“提示词配置”，并继续使用 STranslate 官方提示词编辑窗口。
- 插件图标直接使用 STranslate `src/STranslate/Resources/ocr.png`。源码图标与插件图标 SHA-256 均为 `578242DB43F31A2714CA2534F4FC4D97C94806B185C980DEB35EC4F9ACB248F0`。
- 设置页底部使用 STranslate 原生 `HyperlinkButton`，跳转地址为 `https://github.com/NianBroken/STranslate.Plugin.Ocr.VisionModel`。
- `2026-09-04 22:20` 使用 Release 配置执行 `dotnet build`，结果为 `0` 个警告、`0` 个错误，生成 `STranslate.Plugin.Ocr.VisionModel.spkg`。
- 最终安装包 SHA-256 为 `81452847105BA465AECFA81951888D7030282FE82AED50AC7BE8781FF705147C`。包根目录直接包含 `plugin.json`、插件 DLL、`.deps.json`、`icon.png`、`Assets/validation-image.png`、`Languages`、`LICENSE` 和 `NOTICE`，没有项目目录嵌套。
- 将最终 `.spkg` 解压并部署到真实 STranslate 用户插件目录后，于 `2026-09-04 22:21:32` 在宿主日志确认 `Found plugin: 多模态OCR v1.0.1`，随后确认 `✓ 多模态OCR v1.0.1`，作者为 `NianBroken`，程序集为 `STranslate.Plugin.Ocr.VisionModel`。加载过程没有该插件的加载失败记录。
- 日志保存了此前针对内置 PNG 校验图的真实 MiniMax 流式请求。请求得到 HTTP 成功流式响应，最终文本为 `STranslate Vision OCR`、`Validation 2026` 与 `ABC 123`。此前正式 OCR 的 BMP 输入也已在同一真实宿主中记录为无损 PNG 标准化并成功获取流式响应。
- 当前运行环境没有向界面控制通道公开 STranslate 的桌面 WPF 控件，因此没有伪造设置页点击、全屏拖拽或 GitHub 文字按钮的人工界面记录。发布包已部署并由真实宿主完成加载，未覆盖的人工视觉检查必须在可操作的桌面界面中执行。

## 安全核验

- 源代码、文档、插件元数据、日志记录和安装包不包含真实 API 密钥或 GitHub 密钥。
- `.gitignore` 忽略构建产物、安装包、缓存、日志、临时文件、IDE 配置和本机密钥配置。
