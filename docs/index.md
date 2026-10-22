# 小沐 OCR (ShawMuOCR) 文档索引

欢迎查阅 **小沐 OCR (ShawMuOCR)** 的开发者与架构文档库。本项目是一款基于 **.NET 11 + Avalonia 12 + Sdcb.SimdPaddleOCR + SkiaSharp + Rust (MuClip)** 构建的现代化、极速、纯离线 Windows 桌面 OCR、截图标注翻译与局域网跨端同步利器。

---

## 📚 核心文档导航

| 文档 | 适用对象 | 内容说明 |
| :--- | :--- | :--- |
| **[AI 开发与维护规范 (Agents Guide)](agents.md)** | **AI Coding Assistants & 开发者** | **必读**：全景技术栈、模块职责、NativeAOT 裁剪规则、推理引擎避坑指南、调试测试流 |
| **[项目主说明 (README)](../README.md)** | **普通用户 & 贡献者** | 产品特性、快捷键列表、编译运行、模型下载与致谢信息 |
| **[设计规划文档](../小沐%20OCR%20工具设计规划.md)** | **产品与架构设计者** | 原始产品功能设计规划、UI 页面布局、扩展能力设想 |

---

## 🧩 解决方案架构概览

```
ShawMuOCR.slnx
├── 🖥️ ShawMuOCR                    # 主程序应用层 (WinExe, Avalonia 12, MVVM)
│   ├── ViewModels/                 # 页面 ViewModel (Main, Settings, Translate, BatchOcr, HttpApi 等)
│   ├── Views/                      # 窗口与视图控件 (MainWindow, ScreenshotEditor, TranslateFloatingWindow 等)
│   │   └── Clipboard/              # 统一收归的剪贴板视图 (ClipboardView, QuickPanelWindow, Components)
│   ├── Assets/                     # 图标、Logo、矢量资源与样式字典
│   └── Libs/x64/muclip_core.dll    # 统一托管的 64 位原生动态库
│
├── 🧠 src/ShawMuOCR.Core            # 核心业务抽象与跨平台逻辑
│   ├── OCR/                        # IOcrEngine 契约、PaddleOcrV6Engine、OcrModelProvider 模型扫描与解析
│   ├── Translation/                # 多源翻译 Provider (百度大模型、Google NMT、OpenAI 兼容)、额度管控
│   ├── Capture/                    # 截图与标注领域模型 (ScreenRect, AnnotationLayer)
│   ├── Hotkey/                     # 快捷键解析与抽象数据结构
│   └── Settings/                   # AppSettings、配置防抖持久化 (AutoSave)
│
├── 🪟 src/ShawMuOCR.Windows         # Windows 平台专用原生集成
│   ├── Hotkey/                     # Win32 RegisterHotKey 内核消息循环泵
│   ├── ScreenCapture/              # GDI+ / Win32 原生多显示器 DPI 自适应捕获
│   └── Clipboard/                  # Win32 原生剪贴板直通通道
│
├── 📋 src/MuClip                   # 剪贴板与 P2P 安全流同步子系统 (纯 Core 中间层类库)
│   ├── Interop/                    # Rust 原生 FFI 绑定 (MuClipNative, MuClipSync, BinarySpanReader)
│   ├── Services/                   # 剪贴板细粒度契约 (IClipboard*Service) 与分部服务 (MuClipService.*.cs)
│   ├── ViewModels/                 # 剪贴板与局域网传输状态调度 ViewModel
│   └── Models/                     # 剪贴板条目、配对设备模型、分片文件传输模型
│
└── 🧪 tests/ShawMuOCR.Tests         # 自动化测试套件
    └── 247 项涵盖 OCR 推理、GPU/CPU 引擎、翻译额度拦截、截图标注、MuClip 握手配对等全绿测试
```

---

## ⚡ 核心设计原则

1. **增量模型分发与开箱即用 (Incremental Models on Publish)**：
   发布编译时通过 `<CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>` 增量同步 `Models/`，若目标目录模型文件未变动则自动跳过拷贝，既保证开箱即用，又防止重复发布时清空已有模型目录。
2. **纯托管与 Native AOT 优先**：
   核心 OCR 推理引擎采用纯托管 SIMD 优化版或 Vulkan 计算着色器，杜绝外部庞大 C++ 运行时依赖，全面兼容 Native AOT 提前编译。
3. **安全隐私第一**：
   本地文字识别与剪贴板历史 100% 离线；跨设备通信与文件传输基于 XChaCha20-Poly1305 与 Diffie-Hellman 单流单次随机密钥协商，零隐私泄露隐患。
4. **单文件规模红线与高内聚组件化 (Modular & Componentized)**：
   严禁单文件无节制堆砌（建议不超过 800 行）。大型服务必须按领域拆分分部类并下沉细粒度契约接口；庞大 UI 视图抽离为独立二级组件（`Views/Components/`），保证架构可读性、高扩展性与并发维护安全。
