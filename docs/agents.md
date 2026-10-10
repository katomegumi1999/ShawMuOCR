# AI 协同开发与代码维护指南 (AI Agents Guide)

> 📌 **致未来的 AI Coding Assistant (如 Claude, Gemini, GPT 等)**：
> 欢迎接手 **小沐 OCR (ShawMuOCR)** 的代码开发与重构任务！在开始编写或修改任何代码前，请务必完整阅读本指南，它记录了本项目经过反复验证的架构边界、核心技术机制与“踩坑避雷”法则，能帮助你在 0 破坏原有功能的前提下快速推进需求。

---

## 1. 核心技术栈与开发环境

- **运行环境**：.NET 11.0-windows (x64)
- **UI 框架**：Avalonia UI 12.1.3 (FluentTheme, 纯托管跨平台 XAML)
- **MVVM 库**：CommunityToolkit.Mvvm 8.4.2 (基于 Source Generators 的 `[ObservableProperty]`, `[RelayCommand]`)
- **图形与排版**：SkiaSharp (高性能 2D 绘图与位图操作), PdfPig (纯托管多页 PDF 解析)
- **OCR 推理底座**：`Sdcb.SimdPaddleOCR 2.0.0` (支持 x64 AVX2/SIMD 纯 C# 神经网络计算与 Vulkan GPU 着色器加速推理)
- **剪贴板同步底座 (MuClip)**：C# P/Invoke + 纯 Rust 高性能多线程引擎 (`Libs/x64/muclip_core.dll`)
- **构建目标**：支持 Native AOT 提前编译，零未裁剪运行时依赖，毫秒级冷启动。

---

## 2. 关键目录结构与文件职责

```
ShawMuOCR/
├── ShawMuOCR.csproj                 # 主发布工程：配置 NativeAOT、应用清单、静态资源与发布排除规则
├── App.axaml / App.axaml.cs         # 应用生命周期、系统托盘、全局单例协调 (OcrCoordinator, IdleSleepService)
├── Program.cs                       # 启动入口、Win32 控制台与未捕获异常过滤器、Avalonia 初始化
├── OCR/                             # 主程序专属 OCR 工厂与适配层
│   ├── OcrEngineFactory.cs          # 根据 AppSettings（CPU/GPU、设备ID、模型名称）安全实例化引擎
│   └── OcrModelProvider.cs          # 动态扫描同级 models/ 目录下的可用模型（small, tiny, medium, 外部目录）
├── ViewModels/                      # 主应用各个页面 ViewModel (Main, Settings, Translate, BatchOcr, HttpApi)
├── Views/                           # 主窗口与各个功能页 View (XAML / C#)
├── Models/                          # 默认 OCR 运行模型目录（包含 PP-OCRv6_small/tiny/medium, cls, dict）
├── Libs/x64/muclip_core.dll         # 【唯一权威】原生 64 位 Rust 动态链接库，禁止在根目录冗余复制
├── src/
│   ├── ShawMuOCR.Core/              # 核心业务接口与模型（IOcrEngine, ITranslationService, AppSettings 等）
│   ├── ShawMuOCR.Windows/           # Win32 原生集成（RegisterHotKey 消息循环泵, GDI+ DPI 自适应截图）
│   └── MuClip/                      # 局域网跨设备剪贴板同步子系统（纯 C# MVVM + Rust FFI）
└── tests/ShawMuOCR.Tests/           # 单元测试工程（包含 174 项全绿自动化测试）
```

---

## 3. 必须坚守的设计铁律与避坑指南 (Critical Invariants)

### 3.1 模型发布规则：增量同步 Models 文件夹 (PreserveNewest)
- 在 `ShawMuOCR.csproj` 中，`Models\**` 必须保持：
  ```xml
  <None Update="Models\**">
    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>
    <CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>
  </None>
  ```
- **原因**：`PreserveNewest` 会在发布时自动比对文件时间戳与大小，如果发布目录里已经有相同的模型文件，MSBuild 会直接跳过复制（0 额外开销）；而如果设为 `Never`，发布时的目录同步清理逻辑会把目标发布目录里原本存在的 `models/` 文件夹直接删除。因此统一使用 `PreserveNewest`，既避免重复复制耗时，又防止发布目录中的模型被清空。

### 3.2 模型加载与 ComboBox 绑定的 Null 崩溃陷阱
- **Avalonia 机制陷阱**：在 Avalonia MVVM 中，当 `ComboBox.ItemsSource` 绑定的集合执行 `.Clear()` 时，Avalonia 会强制将 `SelectedItem` 触发置为 `null`！
- **严重后果**：若 View 绑定了双向属性 `SelectedModel`，置 `null` 会立即触发 `SettingsViewModel.OnSelectedModelChanged` 并写入 `AppSettings.Model = null`，导致随后触发的 AutoSave 保存了 `null`，使得底层 `OcrEngineFactory.Create` 在解析模型名称时抛出 `NullReferenceException` 闪退。
- **强制解法**：
  1. `OcrEngineFactory.cs` 内部任何时候访问 `settings.Model`，必须使用保底逻辑：
     ```csharp
     string modelName = string.IsNullOrWhiteSpace(settings?.Model) ? "PP-OCRv6_small" : settings.Model.Trim();
     ```
  2. `SettingsViewModel.cs` 在更新 `AvailableModels` 时，**严禁直接调用 `.Clear()`**，必须使用集合增量差量比对更新（保持已有选项不变，仅添加/删除变动项）。
  3. 在 `SelectedModel` setter 中添加防护：
     ```csharp
     if (string.IsNullOrWhiteSpace(value)) return;
     ```

### 3.3 Avalonia XAML 布局中的 Grid 默认列覆盖陷阱
- 在 Avalonia XAML 中，`<Grid ColumnDefinitions="*,Auto">` 下的子控件如果**遗漏了** `Grid.Column="1"`，默认都会绘制在 `Column 0`！
- 这会导致左侧文本（如“硬件与推理加速”）与右侧徽标（如状态 Border）在同一坐标完全重叠。
- **强制规范**：在包含 `ColumnDefinitions` 或 `RowDefinitions` 的 Grid 中，每个直接子控件必须显式指明 `Grid.Column` 与 `Grid.Row`。

### 3.4 GPU 推理与着色器拓扑规整化
- 小沐 OCR 支持 CPU SIMD 与 GPU (Vulkan Compute) 双轨引擎：
  - GPU 推理依赖基于着色器的融合注意力与卷积优化；
  - ONNX 模型的卷积中间通道数必须为 4 的倍数（$C \pmod 4 = 0$），且转置（Transpose）算子不能有多余的 5 维动态冗余拓扑，否则 GPU 着色器构建失败将发生静默回退（Silent CPU Fallback）；
  - 原生直连 `vulkan-1.dll` (`vkEnumeratePhysicalDevices`) 枚举所有物理显卡并识别 **[独显]** / **[核显]** 类型；
  - **GPU 选择器显隐规则与反射调用保护铁律**：
    1. **显隐规则**：`SettingsViewModel.ShowGpuDeviceSelector` 在纯 `CPU` 模式下必须隐藏（`false`）；在 `GPU` 模式下当检测到物理显卡时显示（`true`）；在 `Auto` 模式下仅当 `PaddleOcrV6Engine.ProbeBackendInfo().AutoSelectsGpu == true` 时显示。
    2. **反射与 Vulkan 设备生命周期保护**：`PaddleOcrV6Engine.SetSelectedGpuIndex` 必须提前校验 `deviceIndex` 合法性（越界或不在已枚举物理设备列表中直接安全 `return`），内层反射调用 `VkDevice.Create` 与构造函数初始化必须包裹 `try-catch` 兜底，且已创建的 `VkDevice` 实例需缓存复用（严禁在旧 `PaddleOcrAll` 引擎仍持有 `VkBuffer`/`VkPipeline` 时直接销毁底层 `VkDevice` 导致 `0xC0000005` 原生闪退）；`SettingsViewModel.OnSelectedGpuIndexChanged` 同样必须包裹 `try-catch` 并通过 `StatusMessage` 反馈异常。
  - 位图直通必须保持“零拷贝直通”（Zero-Copy Pixel Direct Pass），截图像素直接写入引擎，严禁二次缓冲复制。

### 3.5 Native AOT 裁剪安全性
- 全局使用 Native AOT 编译，禁止无约束的反射调用。
- 数据持久化一律使用强类型序列化（如 `System.Text.Json` 代码生成器，或 `BinarySpanReader` 二进制紧凑读写器）。
- 原生 Native 互操作严禁使用过时的 `[DllImport]`，一律使用 .NET 7+ 编译期源代码生成属性 `[LibraryImport]`。

### 3.6 MuClip 局域网协议规范
- `Libs/x64/muclip_core.dll` 是唯一的原生 DLL 存放地，严禁在项目根目录复制冗余文件。
- **配对协议**：最新 Rust 核心使用安全的单流 Diffie-Hellman + PIN 协商机制：
  - 发起端调用 `_sync.RequestPair(addr, pin, timeout)`；
  - 响应端接收 `IncomingPairRequestReceived` 事件，并通过 `_sync.RespondPair(reqId, pin, accept)` 确认；
  - 旧版的 `SyncSendPairRequest` 在底层原生库中已作为 stub 返回 -1，**不要在业务逻辑中依赖它**。
- **双向解除配对**：只有在两端已建立网络心跳或活跃对端连接的前提下，调用 `RemovePairedDevice` 才会向对端发送 `UnpairNoticePayload` 并触发 `RemotePeerUnpaired`。

---

## 4. 常用构建与验证命令

在修改任何代码后，请按以下顺序执行验证：

```powershell
# 1. 编译全解决方案（必须 0 警告、0 错误）
dotnet build

# 2. 执行全量单元测试（必须全部 174 项测试通过）
dotnet test

# 3. 运行特定模块测试（如仅测试模型选择或 MuClip 同步）
dotnet test --filter "FullyQualifiedName~ModelSelectionTests"
dotnet test --filter "FullyQualifiedName~PaddleOcrV6EngineTests"
dotnet test --filter "FullyQualifiedName~MuClipMemoryPoolTests"

# 4. 验证 Native AOT 发布（可选，用于检查裁剪与依赖）
dotnet publish ShawMuOCR.csproj -c Release -r win-x64 --self-contained
```

---

## 5. 给后续 AI Agent 的行为守则

1. **保持文档与注释完整性**：绝不随意删除既有的功能说明、架构注释与中英双语文档。
2. **测试驱动安全**：若修改了核心逻辑，必须保证 `tests/ShawMuOCR.Tests` 中原有的 174 个测试 100% 保持通过，若引入新功能需补齐相应的 xUnit 单元测试。
3. **输出清晰的引用链接**：与用户交流时，涉及代码文件与符号时必须使用标准 Markdown 链接（如 `[OcrEngineFactory.cs](file:///c:/ShawMuOCR/OCR/OcrEngineFactory.cs)`）。
