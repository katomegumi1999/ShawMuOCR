# 小沐 OCR (ShawMuOCR)

<p align="center">
  <a href="README.md">中文</a> | <a href="README.en.md">English</a>
</p>

<p align="center">
  <img src="Assets/logo.png" alt="小沐 OCR Logo" width="96" height="96" />
</p>

<p align="center">
  <strong>轻量 · 极速 · 纯离线 · 现代化的 Windows 桌面 OCR 文字识别、截图标注翻译与跨设备协同利器</strong>
</p>

<p align="center">
  <a href="https://www.23771139.xyz/ShawMuOCR/"><img src="https://img.shields.io/badge/官网-ShawMuOCR-0078D4?logo=google-chrome&logoColor=white" alt="Official Website" /></a>
  <a href="https://dotnet.microsoft.com/download/dotnet/11.0"><img src="https://img.shields.io/badge/.NET-11.0-512BD4?logo=dotnet&logoColor=white" alt=".NET 11" /></a>
  <a href="https://avaloniaui.net/"><img src="https://img.shields.io/badge/UI-Avalonia%2012-7A43B6?logo=avalonia&logoColor=white" alt="Avalonia UI" /></a>
  <a href="https://github.com/sdcb/SimdPaddleOCR"><img src="https://img.shields.io/badge/Inference-CPU%20SIMD%20%2F%20Vulkan%20GPU-0078D4" alt="Sdcb.SimdPaddleOCR" /></a>
  <a href="https://learn.microsoft.com/dotnet/core/deploying/native-aot/"><img src="https://img.shields.io/badge/Publish-Native%20AOT%20Ready-success" alt="Native AOT" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License: MIT" /></a>
  <a href="https://www.microsoft.com/windows"><img src="https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20(x64)-0078D6?logo=windows&logoColor=white" alt="Windows" /></a>
  <a href="tests/ShawMuOCR.Tests"><img src="https://img.shields.io/badge/Tests-247%20Passing-brightgreen" alt="247 Tests Passing" /></a>
</p>

---

## 📖 项目简介

**小沐 OCR (ShawMuOCR)** 是一款专为 Windows 用户量身打造的开源桌面 OCR 文字识别与办公协作利器。

不同于传统 OCR 软件对 Python 环境、CUDA 显卡驱动或庞大外部运行时的笨重依赖，小沐 OCR 基于 **.NET 11 + Avalonia UI 12 + SkiaSharp + Sdcb.SimdPaddleOCR 2.0 + Rust (MuClip)** 构建：
- **CPU SIMD / GPU Vulkan 双轨引擎**：既支持 AVX2 指令集内存级推理，也支持基于 Vulkan 计算着色器的 GPU 高速推理，并在多显卡环境下支持智能识别与手动切换；
- **全链路零拷贝直通**：截图像素通过 `Bgra32` 直通神经网络张量，无中间格式转换与临时位图缓冲；
- **纯离线隐私安全**：文字识别、排版整理及剪贴板管理完全在本地物理机执行；
- **高集成生态**：深度整合系统级快速截图、专业截图标注编辑器、随动选区即时翻译、批量 OCR 转换、局域网 HTTP API 服务，以及基于纯 Rust 底层驱动的跨设备剪贴板同步系统（[MuClip](https://github.com/katomegumi1999/MuClip)）。

---

## ✨ 核心特性

### 1. ⚡ CPU SIMD / GPU 双轨极速推理与位图零拷贝
- **双轨加速**：
  - **CPU 模式**：x64 AVX2 / SIMD 硬件指令集就地色彩解包，冷启动与常驻内存开销极低；
  - **GPU 模式**：基于 Vulkan 着色器优化的高性能并行推理，长图与大批量识别更高效；
  - **独显 / 核显智能探测与手动切换**：原生枚举所有物理显卡并标注 `[独显]` / `[核显]`，默认优先独立显卡，支持在设置页随时手动指定；
- **全链路零拷贝直通 (Zero-Copy Pixel Direct Pass)**：截图像素直接供给模型输入张量，中间分配归零；
- **模型开箱即用与动态扫描**：直接以同级 `models/` 目录为模型库，默认 `PP-OCRv6_small`，支持 `PP-OCRv6_tiny`、`PP-OCRv6_medium` 及任意自定义 Paddle 模型，提供一键 🔄 刷新与 📂 打开目录。
- **开箱即用与增量发布**：发布时采用 `<CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>` 策略，目标目录已有相同模型文件时自动跳过重复拷贝，既避免模型被发布清理误删，又保持极速增量发布。

### 2. ✂️ 全场景截图与标注生态
- **截图 OCR 识别 (`Alt+Z`)**：全屏高分屏 1:1 物理像素无损裁剪 ➔ 自动提取文字 ➔ 自动复制到剪贴板 ➔ 屏幕底部轻量气泡卡片提醒；
- **表格智能分列**：识别到表格时，剪贴板同时写入 HTML 表格与纯文本两种格式——Excel / WPS / Word 粘贴即还原为真正的单元格（空单元格留空、合并表头保留跨列、列不错位），聊天窗口、搜索框、记事本则只拿到干净的纯文本，无制表符副作用。分列优先按图像中的**表格竖线**切分，没有竖线时才回退到检测框投影推断，因此紧凑排版、长数字并排的坐标表也能稳定分列，不会出现"同一张图有时分有时不分"；非表格内容行为完全不变，可随时在设置页关闭。写入结果以剪贴板实际状态为准，HTML 格式未落盘时状态栏不会显示"表格已分列"，日志会记录 `[表格分列]` 诊断行，便于区分「写入失败」与「目标程序不认 CF_HTML」；
- **快速截图复制 (`Alt+X`)**：媲美系统级剪贴体验，框选后直接将图像写入系统剪贴板；
- **长截图 (`Alt+L`)**：框选一块区域后照着平时习惯自己滚就行——程序连续抓帧、逐帧算出内容上移多少像素，把新露出的部分接到长图末尾，停止滚动约 0.7 秒自动收尾并写入剪贴板。不注入滚动事件、不抢焦点，滚多快由你决定；往回滚不会被误当成结束；
- **高级截图标注编辑器 (`Alt+C`)**：
  - 支持矩形、椭圆、箭头、自由画笔、荧光笔、文本输入、序号徽章（递增气泡）、马赛克与高斯模糊；
  - 支持无损撤销/重做、贴图置顶（钉在屏幕桌面）、一键保存（PNG/JPG/WEBP/BMP，自定义画质与缩放比例）、一键送检翻译；
- **贴图置顶细节**：1:1 纯净无缝贴图，精准还原至截图物理坐标；贴图瞬间触发电光蓝定位提示，平时零边框、零遮挡；
- **跨 DPI 严密对齐**：完整适配 Windows 多显示器、混杂 DPI 缩放环境，彻底杜绝模糊与错位。

### 3. 🌐 选区随动即时翻译与多源大模型接入
- **随动翻译浮窗 (`Alt+T`)**：框选屏幕任意段落，翻译浮窗就地停靠展开，OCR 提取与翻译单向流水线推送，零闪烁、不遮挡视线；
- **浮窗位置自锁**：浮窗调整后自动保持坐标不跳动；支持悬浮 Hover 展开原文与一键复制；
- **多翻译提供商深度适配**：
  - **百度翻译**：全面支持最新百度大模型翻译 (`aiTextTranslate`) 与通用文本翻译；
  - **谷歌翻译**：接入 Google Cloud NMT v2，自动处理 HTML 实体转义编码；
  - **OpenAI 兼容接口**：兼容 GPT-4o-mini、DeepSeek、Qwen、通义千问、Kimi 等各种兼容端点；
- **月度额度保护硬拦截**：本地精确统计字符与 Token 消耗，超限即时拦截，绝不多发一次云端调用。

### 4. 🚀 批量 OCR 转换与 HTTP API 服务
- **批量 OCR 转换 (Batch OCR)**：支持多文件、多文件夹批量导入图片与多页 PDF，支持排队并行处理，导出 TXT/JSON/Markdown 或一键复制；PDF 兼容三种形态——带文字层的直接取文本、扫描件提取内嵌位图、矢量型/文字转路径型（GIS、CAD 导出）整页栅格化后识别，不再出现"整页空白"；
- **局域网 HTTP API 服务 (Http Api Server)**：内置轻量 HTTP 服务器，支持标准 RESTful POST 请求，便于外部脚本、自动化流程或第三方软件联动调用；局域网模式强制 Bearer 令牌鉴权，单请求体 20MB 上限、并发超限快速 429，且不接受服务端本地文件路径。

### 5. 📋 跨设备剪贴板同步与协同 ([MuClip](https://github.com/katomegumi1999/MuClip))
- **纯 Rust 高性能底层驱动**：由 [MuClip](https://github.com/katomegumi1999/MuClip) 提供跨设备发现、配对与传输，编译产物为 `Libs/x64/muclip_core.dll`；
- **端到端加密安全传输**：SPAKE2 口令认证握手（PIN 不上线）+ XChaCha20-Poly1305，每条连接使用独立的前向保密会话密钥；
- **常用语快捷面板 (`Alt+W`)**：随时随地呼出悬浮快捷面板，双击快速上屏常用语；
- **独立口令的加密备份与还原 (`.mubc`)**：XChaCha20-Poly1305 + PBKDF2-HMAC-SHA256（10 万轮）把历史记录、常用语、条件规则与配置归档为单个文件，口令与本地库密钥分离；导入支持「增量合并去重」与「完全覆盖」两种模式，换机迁移不必手动搬数据库；
- **大文件安全分片传输**：128 KiB 分块流转 + 全文件 SHA-256 完整性校验。

### 6. 🛡 内核级全局热键与现代设计
- **RegisterHotKey 内核机制**：热键由 Windows 内核直接维护，抗休眠、防系统拔除，零 CPU 占用，不误报；
- **Fluent 现代磨砂界面**：基于 Avalonia 12 Fluent 主题，支持深色/浅色自适应与彩色 Emoji 字体栈；
- **静默自动保存 (Auto-Save)**：任何配置修改后台即刻防抖落盘，不抢焦点，绝不打断当前工作流。

---

## ⌨️ 默认全局快捷键

| 功能 | 默认快捷键 | 说明 |
| :--- | :---: | :--- |
| **截图 OCR 识别** | `Alt + Z` | 唤起全屏框选，自动提取文字、复制并弹出通知 |
| **截图复制** | `Alt + C` | 唤起全屏框选，直接将选中截图复制入剪贴板 |
| **截图标注** | `Alt + X` | 唤起截图标注编辑器，涂鸦批注、保存或钉在桌面 |
| **长截图** | `Alt + L` | 框选区域后跟随手动滚动拼接长图，停止滚动自动完成并写入剪贴板 |
| **屏幕录制** | `Alt + R` | 唤起录屏桌面悬浮栏或开始录制（Rust 核心硬件加速） |
| **选区翻译** | `Alt + T` | 框选屏幕段落，唤起随动浮窗就地进行双语翻译 |
| **OCR 窗格** | `Alt + V` | 框选屏幕区域，识别结果在悬浮窗格中展示，不自动复制 |
| **随手便笺** | `Alt + N` | 呼出随手便笺浮窗，随时记录临时笔记与灵感 |
| **小沐剪贴板** | `Alt + Q` | 呼出极速悬浮剪贴板面板，实时历史检索与一键粘贴 |
| **常用语快速面板** | `Alt + W` | 唤起 MuClip 常用语面板，单击或按键即刻粘贴 |

> 💡 **提示**：所有快捷键均可在【系统设置】中自由修改或清空禁用（输入 `Backspace` 清空）。

---

## 🏗 项目架构设计

本项目遵循分层解耦与 NativeAOT 安全规范：

```
ShawMuOCR
├── ShawMuOCR (主应用层 / Avalonia 12 MVVM)
│   ├── ViewModels/          # MainViewModel, SettingsViewModel, BatchOcrViewModel, HttpApiViewModel 等
│   ├── Views/               # MainWindow, SettingsWindow, ScreenshotEditorWindow, Pages 等
│   ├── OCR/                 # OcrEngineFactory, OcrModelProvider 模型解析与管理
│   ├── Assets/              # 图标、Logo、矢量资源与样式字典
│   └── Libs/x64/            # muclip_core.dll 唯一权威原生依赖
│
├── src/ShawMuOCR.Core (核心领域模型与通用服务抽象)
│   ├── OCR/                 # IOcrEngine 契约、PaddleOcrV6Engine 推理、几何网格拓扑排序
│   ├── Translation/         # ITranslationService、百度/谷歌/OpenAI 翻译 Provider、额度管理
│   ├── Capture/             # 截屏契约、ScreenRect 物理边界、图片文档模型
│   ├── Hotkey/              # 跨平台快捷键手势与解析定义
│   └── Settings/            # AppSettings 配置模型与 JsonSettingsService
│
├── src/ShawMuOCR.Windows (Windows 原生底层集成)
│   ├── ScreenCapture/       # Win32 GDI+ 原生截图、多屏 DPI 自适应捕获
│   ├── Hotkey/              # Win32 RegisterHotKey 内核级全局热键与消息循环线程
│   └── Clipboard/           # 托管与 Win32 原生剪贴板通信服务
│
├── src/MuClip (局域网跨设备剪贴板与文件同步系统)
│   ├── Interop/             # Rust 原生 FFI 绑定 (MuClipNative, MuClipSync, BinarySpanReader)
│   ├── Services/            # 剪贴板监听、历史缓存、单流加密配对与分片文件传输
│   └── ViewModels/ & Views/ # 剪贴板视图与快捷呼出面板
│
├── docs/ (架构与开发指南)
│   ├── index.md             # 文档索引概览
│   └── agents.md            # AI 协同开发与代码维护指南 (必读)
│
└── tests/ShawMuOCR.Tests (自动化单元测试套件)
    └── 345 项涵盖 OCR 推理、GPU/CPU 引擎、翻译拦截、截图标注图层、自动保存与 MuClip 握手等全绿测试
```

---

## 🛠 编译与运行指南

### 1. 环境准备
- 操作系统：**Windows 10 / Windows 11 (x64)**
- 开发环境：[.NET 11.0 SDK](https://dotnet.microsoft.com/download/dotnet/11.0) 或更高版本
- IDE 推荐：Visual Studio 2022 / 2026、JetBrains Rider 或 VS Code (配合 C# Dev Kit)

### 2. 获取源码与编译运行
```bash
# 克隆仓库
git clone https://github.com/katomegumi1999/ShawMuOCR.git
cd ShawMuOCR

# 还原依赖并编译
dotnet build -c Release

# 运行程序
dotnet run -c Release
```

### 3. 执行自动化单元测试
```bash
# 日常改动用快档：跳过真实推理与内存池压力测试，约 3 秒
dotnet test --filter "Category!=Slow"

# 只跑受影响的模块（示例）
dotnet test --filter "FullyQualifiedName~OcrTableLayout"

# 发布 / 提交前跑全量 345 项（含推理速度基准与网络配对测试，约 2 分钟）
dotnet test
```

### 4. Native AOT 独立发布 (便携绿色版)
项目已原生支持 Native AOT，发布时不包含任何冗余符号文件，也不自动捆绑超大模型：

```bash
# 发布高度优化的 Windows x64 Native AOT 绿色单目录/单文件版本
dotnet publish ShawMuOCR.csproj -c Release -r win-x64 --self-contained
```
发布产物位于 `bin/Release/net11.0-windows/win-x64/publish/`，将所需模型放入同级 `models/` 目录，直接双击 `ShawMuOCR.exe` 即可毫秒级闪电启动！

---

## 🧩 模型下载与管理说明

程序运行时自动扫描同目录下的 `models/` 文件夹：

1. **默认模型结构**：
   - `models/PP-OCRv6_small/`（默认内置推荐）：兼顾准确率与极速响应；
   - `models/PP-OCRv6_tiny/`：极速轻量模型；
   - `models/PP-OCRv6_medium/`：高精度大模型；
   - 字典文件（`ppocrv6_dict.txt`）与方向分类模型（`ch_ppocr_mobile_v2.0_cls.onnx`）可置于 `models/` 根目录由各子模型复用；
2. **外部自定义 Paddle 模型**：
   - 直接在 `models/` 下新建任意名称的子文件夹，放入对应的 `*det*.onnx` 和 `*rec*.onnx` 文件；
   - 打开小沐 OCR【设置】➔ 点击模型选择右侧的 🔄 刷新按钮，即可直接在下拉列表中切换！

---

## 🤝 贡献与开发者文档

- 开发者与 AI 协作规范请参阅：**[docs/agents.md](docs/agents.md)**
- 欢迎提交 Issue 与 Pull Request！请确保提交前执行 `dotnet test` 并保证 345 项测试全部通过。

---

## 💖 致谢与开源生态

小沐 OCR 的诞生离不开开源社区的强大基石，在此对以下优秀项目表示由衷感谢：

- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) - 卓越的跨平台 XAML .NET UI 框架
- [Sdcb.SimdPaddleOCR](https://github.com/sdcb/SimdPaddleOCR) - 纯 C# SIMD / Vulkan GPU 优化 PP-OCR 推理库
- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) - 百度优秀的开源飞桨深度学习 OCR 工具库
- [SkiaSharp](https://github.com/mono/SkiaSharp) - 强大的 2D 矢量图形渲染跨平台引擎
- [PdfPig](https://github.com/UglyToad/PdfPig) - 纯托管的高性能 PDF 文档解析利器
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) - 简洁高效的现代化 MVVM 工具箱
- [MuClip](https://github.com/katomegumi1999/MuClip) - 本项目自研的纯 Rust 跨设备剪贴板同步与加密文件互传引擎（已开源）

---

## 👤 作者与社区

- **开发作者**：沐洵羽
- **官方网站**：[https://www.23771139.xyz/ShawMuOCR/](https://www.23771139.xyz/ShawMuOCR/)
- **哔哩哔哩 (B站)**：[@沐洵羽](https://space.bilibili.com/192158726)
- **GitHub 仓库**：[katomegumi1999/ShawMuOCR](https://github.com/katomegumi1999/ShawMuOCR)
- 欢迎在 GitHub 点上一颗 ⭐️ **Star**，您的支持是持续维护与迭代的最大动力！

---

## 📜 开源协议

本项目基于 [MIT License](LICENSE) 协议开源。
你可以自由地使用、修改、分发与商用本软件，但请保留原作者版权信息与开源声明。
