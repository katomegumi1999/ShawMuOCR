# 小沐 OCR (ShawMuOCR)

<p align="center">
  <img src="Assets/logo.png" alt="小沐 OCR Logo" width="96" height="96" />
</p>

<p align="center">
  <strong>轻量 · 极速 · 纯离线 · 现代化的 Windows 桌面 OCR 文字识别与截图标注 / 翻译工具</strong>
</p>

<p align="center">
  <a href="https://dotnet.microsoft.com/download/dotnet/11.0"><img src="https://img.shields.io/badge/.NET-11.0-512BD4?logo=dotnet&logoColor=white" alt=".NET 11" /></a>
  <a href="https://avaloniaui.net/"><img src="https://img.shields.io/badge/UI-Avalonia%2012-7A43B6?logo=avalonia&logoColor=white" alt="Avalonia UI" /></a>
  <a href="https://github.com/sdcb/SimdPaddleOCR"><img src="https://img.shields.io/badge/Inference-Pure%20C%23%20SIMD%20%C2%B7%20Zero%20Copy-0078D4" alt="Pure C# SIMD Zero Copy" /></a>
  <a href="https://learn.microsoft.com/dotnet/core/deploying/native-aot/"><img src="https://img.shields.io/badge/Publish-Native%20AOT%20Ready-success" alt="Native AOT" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License: MIT" /></a>
  <a href="https://www.microsoft.com/windows"><img src="https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20(x64)-0078D6?logo=windows&logoColor=white" alt="Windows" /></a>
</p>

---

## 📖 项目简介

**小沐 OCR (ShawMuOCR)** 是一款专为 Windows 用户量身打造的开源桌面 OCR 文字识别利器。

不同于传统 OCR 软件对 Python 环境、CUDA 显卡驱动、庞大的 C++ 原生动态链接库或笨重外部运行时的依赖，小沐 OCR 基于 **.NET 11 + Avalonia UI + SkiaSharp + Sdcb.SimdPaddleOCR** 构建。它采用**纯 C# 编写的 AVX2/SIMD 内存级神经网络推理引擎**，将高精度 **PP-OCRv6** 模型作为嵌入资源直接编译入程序集内，真正做到**开箱即用、零文件依赖、毫秒级响应、完全离线隐私安全**。

此外，小沐 OCR 还深度整合了**系统级快速截图复制**、**功能完备的截图标注编辑器**、**随动选区即时翻译浮窗**以及**多源大模型翻译服务**，并由 **Windows 内核级全局热键**全程驱动，让截屏、提取、标注与翻译一气呵成。

---

## ✨ 核心特性

### 1. ⚡ 纯 C# SIMD 极速推理与本地离线安全
- **零环境依赖**：无需安装 Python、无需配置 OpenCV 或 ONNX Runtime C++ 原生 DLL，绿色纯净；
- **全链路零拷贝**：基于 Sdcb.SimdPaddleOCR 1.4+ 原生像素格式通道直通（`Bgra32` / `Rgba32`），SIMD Kernel 在图像 Resize / Warp 时就地完成色彩通道解包并直写 NHWC 神经网络张量，截图像素无需任何中间格式转换与临时缓冲，单次识别中间内存分配归零；
- **指令集加速**：x64 AVX2 / SIMD 硬件指令集 + 图级 NHWC 卷积推理。仓库内置速度基准单元测试（640×120 中英双语图文，热运行 10 轮均值）：

  | 模型 | 冷启动 | 热运行均值 | 吞吐量 |
  | :--- | ---: | ---: | ---: |
  | PP-OCRv6_tiny | ~37 ms | **~25 ms** | 39.6 FPS |
  | PP-OCRv6_small | ~338 ms | **~94 ms** | 10.7 FPS |

- **100% 离线隐私**：所有图像识别推理均在本地 CPU 与物理内存中完成，文字数据绝不上云；
- **智能内存整理**：内置闲置自动休眠机制（默认 3 分钟无操作自动释放工作集与非活跃内存），日常常驻内存极低。

### 2. ✂️ 全场景截图与标注生态
- **截图 OCR 识别 (`Alt+Z`)**：全屏高分屏 1:1 物理像素无损裁剪 ➔ 自动识别文字 ➔ 自动复制到剪贴板 ➔ 屏幕底部轻量气泡卡片提醒；
- **快速截图复制 (`Alt+X`)**：媲美系统级剪贴体验，框选后直接将图像写入系统剪贴板；
- **高级截图标注编辑器 (`Alt+C`)**：
  - 支持矩形、椭圆、箭头、自由画笔、荧光笔、文本输入、序号徽章（递增气泡）、马赛克与高斯模糊；
  - 支持无损撤销/重做、贴图置顶（钉在屏幕桌面）、一键保存（PNG/JPG/WEBP/BMP，自定义画质与缩放比例）、一键送检翻译；
- **贴图置顶细节**：去除圆角与描边的 1:1 纯净无缝贴图，精准还原至最初截图的物理位置；贴图瞬间触发霓虹电光蓝呼吸灯定位提示（约 2.3 秒后优雅熄灭），平时零边框、零遮挡、零干扰；
- **连续截屏零残留**：每次框选启动前 0ms 预清除上一轮气泡提醒，杜绝气泡残影被截入底图或遮挡视线；
- **跨 DPI 严密对齐**：完整适配 Windows 多显示器、混杂 DPI 缩放环境，彻底杜绝模糊与错位。

### 3. 🌐 选区随动即时翻译与多源大模型接入
- **随动翻译浮窗 (`Alt+T`)**：框选屏幕任意段落，翻译浮窗就地停靠展开，OCR 提取与翻译单向流水线推送，零闪烁、不遮挡视线；
- **浮窗位置自锁**：浮窗仅在首次打开时贴靠选区；用户调整位置后，后续再次框选自动保持现有坐标不跳动；
- **轻量交互与模型热切**：支持直接在浮窗顶部切换翻译服务；原文采用悬浮 Hover 展开并支持一键复制，把主视区留给译文；
- **多翻译提供商深度适配**：
  - **百度翻译**：全面支持最新百度大模型翻译 (`aiTextTranslate`) 与通用文本翻译；
  - **谷歌翻译**：接入 Google Cloud NMT v2，自动处理 HTML 实体转义编码；
  - **OpenAI 兼容接口**：兼容 GPT-4o-mini、DeepSeek、Qwen、通义千问、Kimi 等各种兼容端点；
- **月度额度保护硬拦截**：默认额度 百度 100 万字符 / 谷歌 50 万字符 / OpenAI 20 万 Token（每月，均可自定义）。本地精确统计消耗，超限即时本地拦截请求，绝不多发一次云端调用；支持跨月自动清零与一键重置计数；
- **独立【文本翻译】功能页**：提供双栏对照、语言快速互换、字符计数与额度健康度展示。

### 4. 📑 多页 PDF 与常见图像深度支持
- 支持常见格式（PNG, JPG, BMP, WEBP, TIFF）直接拖拽至工作区快速识别；
- 接入高性能纯托管 PDF 引擎，支持数十页乃至上百页 PDF 多页批量文档自动解析与 OCR 文本提取；
- **空间拓扑阅读顺序排版**：基于几何坐标网格二维排序算法，智能重组多栏排版与复杂版面文字。

### 5. 🎨 现代设计与极简体验
- **Fluent 现代磨砂界面**：基于 Avalonia 12 Fluent 主题，支持深色/浅色主题自适应；
- **全彩 Emoji 字体栈支持**：完美支持彩色 Emoji 徽标与状态展示；
- **实时自动保存 (Auto-Save)**：修改任何开关、下拉框、API 参数或快捷键，后台即刻静默保存（API Key 等文本输入采用 400ms 防抖，离开设置页即时落盘），绝不强制跳转页面；
- **识别完成不抢焦点**：识别结果照常推送剪贴板与气泡提醒，但严格保留用户当前所在页面，绝不打断"正在改设置"这类工作流。

### 6. 🛡 内核级全局热键，永不掉线
- **RegisterHotKey 内核机制**：热键注册表由 Windows 内核（win32k.sys）统一维护，彻底告别 `WH_KEYBOARD_LL` 低级钩子受 `LowLevelHooksTimeout` 限制、被系统在高负载或休眠唤醒时静默拔除的顽疾——闲置休眠数日、锁屏、睡眠唤醒或全屏游戏中，热键依然精准响应；
- **vkE8 虚拟按键击发**：Alt 系热键触发后自动注入一次无害虚拟按键，根治前台窗口（Word、资源管理器、VS Code）菜单栏被 Alt 高亮弹出的副作用；
- **零监听零误报**：不再逐键监听全系统输入，零 CPU 开销，杜绝杀毒软件将程序误报为"键盘记录器"；
- **冲突精准感知**：热键被其他软件抢占时立即在状态栏呈现友好告警；界面内直接按下键盘录制，防重复、防系统占用。

---

## ⌨️ 默认全局快捷键

| 功能 | 默认快捷键 | 说明 |
| :--- | :---: | :--- |
| **截图 OCR 识别** | `Alt + Z` | 唤起全屏框选，自动提取文字、复制并弹出通知 |
| **截图复制** | `Alt + X` | 唤起全屏框选，直接将选中截图复制入剪贴板 |
| **截图标注** | `Alt + C` | 唤起截图标注编辑器，涂鸦批注、保存或钉在桌面 |
| **选区翻译** | `Alt + T` | 框选屏幕段落，唤起随动浮窗就地进行双语翻译 |

> 💡 **提示**：所有快捷键均可在【系统设置】中自由修改或清空禁用（输入 `Backspace` 清空）。

---

## 🏗 项目架构设计

本项目遵循清晰的分层解耦设计原则，UI 层仅负责视图表现，业务流与底层系统 API 完全抽象：

```
ShawMuOCR
├── ShawMuOCR (主应用层 / Avalonia 12 MVVM)
│   ├── ViewModels/          # MainViewModel, SettingsViewModel, TranslateViewModel 等
│   ├── Views/               # MainWindow, TranslateFloatingWindow, ScreenshotEditorWindow 等
│   └── Assets/              # 图标、Logo 与样式资源
│
├── src/ShawMuOCR.Core (核心领域模型与通用服务抽象)
│   ├── OCR/                 # IOcrEngine 契约、PaddleOcrV6Engine 零拷贝推理、模型排版排序算法
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
└── tests/ShawMuOCR.Tests (自动化单元测试套件)
    └── 涵盖引擎推理与速度基准、翻译额度拦截、排序拓扑、截图标注图层、自动保存与热键手势等 97 项全绿测试
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
# 执行全量 97 项单元测试（含 OCR 推理速度基准）
dotnet test
```

### 4. Native AOT 独立发布 (单文件便携绿色版)
项目已原生支持并深度优化 Native AOT，发布时不包含任何冗余符号文件与无用模型：

```bash
# 发布高度优化的 Windows x64 Native AOT 绿色单目录/单文件版本
dotnet publish ShawMuOCR.csproj -c Release -r win-x64 --self-contained
```
发布产物位于 `bin/Release/net11.0-windows/win-x64/publish/`，直接双击 `ShawMuOCR.exe` 即可毫秒级闪电启动！

---

## 🧩 自定义模型与扩展指南

除了内置的官方 `PP-OCRv6_small` 与 `PP-OCRv6_tiny` 高精度推理模型外，小沐 OCR 还支持灵活接入自定义外部模型：

1. **外部本地目录**：
   - 在程序同级目录下创建 `models/` 文件夹；
   - 放入自定义训练的 PaddleOCR ONNX 格式模型文件（需包含 `*det*.onnx` 检测模型与 `*rec*.onnx` 识别模型）；
   - 字典文件（`dict.txt`）及方向分类（`cls.onnx`）可自由选填，缺省时将自动复用内置的高品质官方资源；
   - 在【系统设置】➔【OCR 模型设置】中开启“识别外部模型”，即可在下拉列表中直接选用。
2. **外部 Web API 接口**：
   - 支持直接对接内网或公网部署的 OCR HTTP 接口（兼容标准 POST 请求与 JSON 返回）。

> 💡 **速度说明**：内置模型与外部模型共用同一套纯 C# SIMD / AVX2 推理底座，运行期耗时 100% 取决于模型网络参数量与图片尺寸。放入相同的官方 PP-OCRv6 ONNX，识别耗时分毫不差；换成 Tiny 更快，换成 Server 级大模型则更准更慢。唯一差别是冷启动读取来源（程序集嵌入流 vs 磁盘文件流），差值仅数毫秒，完全无感。

---

## 🤝 贡献与反馈

欢迎提交 Issue 与 Pull Request！

- **提交 Bug 报告**：请注明您的操作系统版本、显示器 DPI 缩放配置及复现步骤；
- **新特性建议**：欢迎在 Discussions 畅所欲言，分享您对小沐 OCR 的建议与构想；
- **贡献代码**：请确保修改后的代码符合现有分层架构，并通过所有单元测试（`dotnet test`）。

---

## 💖 致谢与开源生态

小沐 OCR 的诞生离不开开源社区的强大基石，在此对以下优秀项目表示由衷感谢：

- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) - 卓越的跨平台 XAML .NET UI 框架
- [Sdcb.SimdPaddleOCR](https://github.com/sdcb/SimdPaddleOCR) - 极致优雅的纯 C# SIMD 优化 PP-OCR 推理库
- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) - 百度优秀的开源飞桨深度学习 OCR 工具库
- [SkiaSharp](https://github.com/mono/SkiaSharp) - 强大的 2D 矢量图形渲染跨平台引擎
- [PdfPig](https://github.com/UglyToad/PdfPig) - 纯托管的高性能 PDF 文档解析利器
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) - 简洁高效的现代化 MVVM 工具箱

---

## 👤 作者与社区

- **开发作者**：沐洵羽
- **哔哩哔哩 (B站)**：[@沐洵羽](https://space.bilibili.com/192158726)
- **GitHub 仓库**：[katomegumi1999/ShawMuOCR](https://github.com/katomegumi1999/ShawMuOCR)
- 欢迎在 GitHub 点上一颗 ⭐️ **Star**，您的支持是持续维护与迭代的最大动力！

---

## 📜 开源协议

本项目基于 [MIT License](LICENSE) 协议开源。
你可以自由地使用、修改、分发与商用本软件，但请保留原作者版权信息与开源声明。
