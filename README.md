# 多模态OCR

多模态OCR 是 STranslate 社区 OCR 插件。它通过视觉模型识别图片文字，只返回模型生成的纯文本，并提供面向不同供应商和不同视觉模型的高度自定义能力。

## 主要功能

- 自定义 API 地址、API 密钥和模型 ID。
- 自定义 JSON 请求体与请求头。用户填写的同名字段会覆盖内置字段，未填写的内置字段继续保留。
- 使用 STranslate 官方提示词配置维护 system、user 和其他角色提示词。
- 默认流式输出。用户可通过请求体将 `stream` 设置为 `false` 使用非流式接口。
- 自动重试，并可配置最大请求次数与超时时间。
- 兼容 OpenAI、Claude、Gemini，以及 Qwen、Kimi、MiniMax 等常见视觉模型请求结构。
- PNG、JPEG、GIF 和 WebP 保持原始字节发送。宿主传入 BMP 时进行无损 PNG 标准化，不缩放、不裁剪、不重采样。
- 过滤思考内容、工具调用、坐标、位置和标注信息，最终只显示 OCR 纯文本。

## 配置

配置页按 API 地址、API 密钥、模型 ID、自定义请求体、自定义请求头、提示词配置、最大请求次数、超时时间和连接校验排列。每个配置卡片均使用 STranslate `SettingsCard` 的纵向布局，描述位于上方，操作控件位于下方。

API 密钥使用可见文本框，但日志和错误信息始终脱敏。提示词配置复用 STranslate 官方 `Prompt` 与 `PromptItem` 模型，并通过宿主提示词编辑窗口维护。首次加载时会创建名为“多模态OCR”的启用提示词，其中包含空的 `system` 与 `user` 项。系统提示词为必填项。

流式超时时间表示相邻两条非空响应之间的最大等待时间。只要服务持续发送数据，请求总时长可以超过配置值。非流式请求使用完整请求生命周期超时。

设置页底部的“在 GitHub 上查看”文字按钮通过 STranslate 原生 `HyperlinkButton` 使用系统默认浏览器打开项目仓库。

## 构建与安装

```powershell
dotnet build .\STranslate.Plugin.Ocr.VisionModel\STranslate.Plugin.Ocr.VisionModel.csproj --configuration Release --nologo
```

Release 安装包路径为：

```text
.artifacts\Release\Plugins\STranslate.Plugin.Ocr.VisionModel\plugins\STranslate.Plugin.Ocr.VisionModel.spkg
```

在 STranslate 插件管理页面导入该 `.spkg`，重启宿主后选择“多模态OCR”。安装包根目录直接包含 `plugin.json`、插件 DLL、`icon.png`、`Assets` 和 `Languages`，符合 STranslate 插件安装结构。

## 日志

插件通过 `Context.Logger` 记录请求和响应时间、脱敏请求头、请求体 RAW、响应体 RAW、流式响应行、图片哈希、重试与异常信息。

## 作者、仓库与许可证

- 作者：[NianBroken](https://www.klaio.top/)
- 仓库：<https://github.com/NianBroken/STranslate.Plugin.Ocr.VisionModel>
- 许可证：[Apache License 2.0](LICENSE)
