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
- **屏幕录制底座 (MuRecorder)**：C# P/Invoke + Rust 引擎（DXGI 桌面复制 / OpenH264 / MP4 封装 / WASAPI 音频，`Libs/x64/murecorder_core.dll`），Rust 工程独立在 `C:\Rust\MuRecorder`
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
├── Libs/x64/murecorder_core.dll     # 【唯一权威】录屏引擎原生库（由 C:\Rust\MuRecorder 构建产出）
├── src/
│   ├── ShawMuOCR.Core/              # 核心业务接口与模型（IOcrEngine, ITranslationService, AppSettings 等）
│   ├── ShawMuOCR.Windows/           # Win32 原生集成（RegisterHotKey 消息循环泵, GDI+ DPI 自适应截图）
│   ├── MuRecorder/                  # 录屏子系统（Interop / Services / ViewModels / Views，原生活全在 Rust 侧）
│   └── MuClip/                      # 局域网跨设备剪贴板同步子系统（纯 C# MVVM + Rust FFI）
└── tests/ShawMuOCR.Tests/           # 单元测试工程（包含 351 项自动化测试，其中 36 项标记 Category=Slow）
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

### 3.7 单文件代码规模红线与高内聚服务/UI组件化规范
为避免单个源文件代码膨胀（例如原先单文件接近 2000 行导致阅读困难、维护性降低与并发编辑冲突），项目全面推行高内聚模块化拆分规范：
1. **单文件规模红线**：
   - 业务逻辑文件（`.cs`）与 XAML 页面文件（`.axaml`）建议控制在 800 行以内，严禁无节制堆砌代码超过 1200 行。
2. **大型服务领域分部类与契约下沉**：
   - 对于职责丰富的大型核心服务（如 `MuClipService`），必须抽离细粒度接口契约（如 `IClipboardHistoryService`、`IClipboardPhraseService`、`IClipboardSyncService`、`IClipboardFileTransferService`），主服务接口（如 `IMuClipService`）继承所有子接口作为门面（Facade），确保对既有外部依赖注入调用 100% 零破坏兼容；
   - 实现端采用 `partial class` 按业务领域物理拆分文件（例如 `MuClipService.cs`、`MuClipService.History.cs`、`MuClipService.Phrases.cs`、`MuClipService.Sync.cs`、`MuClipService.FileTransfer.cs`），共享原生引擎句柄与同步锁，消除各子功能间循环引用与多锁死锁隐患。
3. **庞大 UI 视图的组件化抽离**：
   - 包含多子 Tab、复杂二级配置或浮动面板的 View（如 `ClipboardView.axaml`），必须将独立的二级面板提取为独立的 `UserControl` 组件（置于 `Views/Components/` 目录，如 `ClipboardLanSyncPanel.axaml`、`ClipboardFileTransferPanel.axaml`）；
   - 主视图直接通过 xmlns 命名空间引入并嵌入子组件标签，极大精简主视图树层级，提升 XAML 渲染性能与可视化维护体验。

### 3.8 剪贴板纯中间层（Core）与主工程统一 UI/主题规范
为了解决过去剪贴板独立分发导致的 UI 割裂、资源重复与循环引用问题，工程采用了**整洁分层架构（Clean Architecture）**：
1. **MuClip 职责下沉为纯核心中间层**：
   - `src/MuClip` 项目定位为纯 Class Library（`MuClip.Core`），仅承载 Rust `muclip_core.dll` 的原生互操作（P/Invoke）、数据模型契约、分部业务服务（History / Sync / FileTransfer）和无重型 UI 的 ViewModels；
   - 移除 MuClip 中所有具体的 Window 窗体和视图实现，彻底解除 Avalonia UI 框架对核心逻辑的侵入。
2. **UI 视图收归主工程与主题大一统**：
   - 所有剪贴板视图统一归纳在主工程 `Views/Clipboard/`（包含 `ClipboardView`、`ClipboardQuickPanelWindow`、`PairRequestDialog` 等）；
   - `ClipboardQuickPanelWindow` 直接复用主软件的 `ThemeService` 和 `AppSettings.ThemeFloatingWindowBackgroundColorHex`，与 `TranslateFloatingWindow` 同步换肤，完全消除了两套调色盘和各画各的割裂；
3. **单程序自适应独立剪贴板模式**：
   - 主程序 `Program.cs` 原生支持 `--clip` 或 `--muclip` 启动参数（或者 exe 改名为 `MuClip.exe`）；
   - 命中时直接启动轻量剪贴板独立模式（仅驻留托盘 + 响应快捷键，不加载庞大的 PaddleOCR 模型，零多余内存开销），无需维护双重打包流水线。

### 3.9 安全边界铁律（HTTP API / 凭据落盘 / 配置持久化）
内置 HTTP 服务与本地凭据是外部可触达面，以下规则为**不可回退的硬约束**，改动相关代码前必须逐条对照：

1. **禁止任何"由服务端按路径读取文件"的入参**。
   - `?path=` 与 JSON 体 `imagePath` 已在 2026-10 移除，保留分支仅为返回明确的 400 提示；新增接口同样禁止接受文件路径、URL 或目录参数。
   - 原因：这类入参会把接口变成未授权的任意文件读取入口，且浏览器页面可借 CORS 直接消费返回值。

2. **CORS 仅放行本机来源**（`localhost` / `127.0.0.1` / `[::1]`），**禁止 `Access-Control-Allow-Origin: *`**。
   - 非本机 `Origin` 一律 403 且不下发任何 CORS 响应头（防 DNS 重绑定与任意网页跨域调用）。
   - 无 `Origin` 的直连客户端（curl / HttpClient）不受限制。

3. **局域网暴露默认必须令牌鉴权，失败即关闭（Fail-Closed）**。
   - `HttpOcrServer.StartAsync` 在 `allowRemote = true` 且令牌为空时直接抛异常，不允许"默认无鉴权的局域网暴露"。
   - `HttpApiViewModel` 在用户勾选局域网访问时自动生成 48 位十六进制令牌并持久化到 `AppSettings.HttpApiAccessToken`。
   - 令牌非空即全局强制 `Authorization: Bearer <token>`，比较使用 `CryptographicOperations.FixedTimeEquals`。
   - **唯一例外**：用户显式勾选"允许局域网无令牌访问"（`HttpApiAllowAnonymousRemote = true` + `StartAsync(..., allowAnonymousRemote: true)`）时，允许空令牌开放局域网。该选项默认 `false`，仅在设置页可见于局域网开关打开后，且开启时令牌必须清空（两者互斥）。这是给"临时分享给同网段同事"留的口子，**不得改为默认行为**。
   - 无令牌模式只放开鉴权，**不得**因此放宽第 1、2 条（路径参数与 CORS 限制照旧生效，见 `StartAsync_RemoteAnonymous_WorksWhenExplicitlyAllowed`）。

4. **请求体必须有硬上限，并发必须有非阻塞准入**。
   - 单请求体上限 `MaxRequestBodyBytes = 20MB`：先看 `ContentLength64`，读取过程中再累计校验，超出抛 `PayloadTooLargeException` → 413。
   - 准入闸门 `SemaphoreSlim` 配额 = `max(2, MaxCores × 2)`，使用 `Wait(0)` 非阻塞获取，取不到直接 429；**严禁**把请求排进无界队列。
   - 位图解码必须放在 `scheduler.ExecuteAsync(...)` **内部**，保证排队等待期间不持有解码后的位图内存。

5. **所有云厂商凭据必须加密落盘**。
   - 当前加密字段清单：`BaiduAppId`、`BaiduSecretKey`、`BaiduApiKey`、`GoogleApiKey`、`OpenAiApiKey`、`VolcengineAccessKey`、`VolcengineSecretKey`、`AliyunAccessKeyId`、`AliyunAccessKeySecret`、`HttpApiAccessToken`。
   - **新增任何凭据字段时，必须同时加入 `JsonSettingsService.DecryptSensitiveFields` 与 `EncryptSensitiveFields` 两处**，否则就是明文落盘。
   - 兼容规则：无 `enc:` 前缀的旧明文原样读入（下次保存自动加密升级）；`enc:` 密文解不开时（换机/换账户）内存返回空但**落盘原样写回**，绝不销毁用户凭据。

6. **配置持久化必须原子写，失败不得静默**。
   - 写入路径固定为「同目录 `.tmp` + `FileOptions.WriteThrough` + `File.Replace`」，禁止直接 `File.WriteAllText` 覆盖目标文件。
   - 加载失败：设置 `LastLoadError`、备份为 `settings.json.bad-<时间戳>`、触发 `LoadFailed` 事件（`App.axaml.cs` 已接到日志）。
   - 保存失败：设置 `LastSaveError` 并触发 `SaveFailed`，先回退 AppData，两处都失败时留痕而非静默吞掉。

7. **回归测试位置**：`tests/ShawMuOCR.Tests/HttpOcrServerTests.cs`（路径参数拒绝 / CORS / 令牌 / 413 / 429）与
   `SettingsPersistenceSecurityTests.cs`（原子写 / 凭据加密 / 旧明文升级 / 不可解密文保全 / 损坏配置备份）。**改动上述任一处逻辑必须同步维护这些用例**。

### 3.10 录屏引擎（MuRecorder）构建约定与桌面复制语义

1. **DLL 只能由脚本产出**，禁止手工拷贝：
   ```powershell
   powershell -ExecutionPolicy Bypass -File C:\Rust\MuRecorder\scripts\build.ps1 -Configuration release
   ```
   脚本用**绝对路径** `-ShawMuOCRDir`（默认 `C:\ShawMuOCR`）定位目标，并在复制后比对 SHA256。
   历史坑：早期用 `..\ShawMuOCR` 相对路径推算，静默复制到 `C:\Rust\ShawMuOCR` 影子目录，
   真正的 `Libs/x64` 一直留旧 DLL，表现为「测试全是 0 帧」的假故障。**怀疑产物没生效时先比哈希**：
   `Get-FileHash target\release\murecorder_core.dll` vs `Get-FileHash Libs\x64\murecorder_core.dll`。
2. **DXGI 桌面复制只在画面变化时交付帧**：完全静止的桌面连首帧都没有，光标移动也不算变化
   （光标不在复制帧内）。因此 `capture/gdi.rs` 保留一条 GDI 首帧兜底（启动 300ms 无帧时触发），
   之后用「重发上一帧副本」维持 CFR。**改动采集循环时不要删掉这两条保命逻辑**。
3. **不要写依赖真实桌面活动的测试**：进程内自建窗口在本机（无桌面 VM）不会出现在被捕获的桌面上。
   「编码 → 封装」的确定性覆盖在 Rust `recorder::tests::pipeline_encodes_synthetic_frames_into_valid_mp4`
   （合成帧驱动真实 encode/mux 线程）；C# 侧 `RecordingServiceIntegrationTests` 的真实录制项做成双路径
   （有帧校验产物结构 / 无帧校验「失败并清理残留文件」）。

---

### 3.11 启动顺序：窗口优先，重活靠后（性能红线）

**铁律：主窗口呈现之前，只允许做「毫秒级」的事。** 用户能感知到的启动时间 = 进程启动 → 主窗口可见，
任何放在 `Show()` 之前的同步耗时都会 1:1 变成"点了图标没反应"。

Release AOT 实测（本机，含自定义背景图）：

| 环节 | 耗时 | 处置 |
| --- | --- | --- |
| OCR 引擎实例化（3 个 ONNX 会话） | 105~160 ms | **必须**在后台线程，`OcrCoordinator.EnsureEngineAsync()` 惰性加载 |
| 小沐剪贴板服务（sqlite + 局域网监听） | 100~200 ms | **必须**后台，`EnsureMuClipServiceInitialized()` 默认异步 |
| 主题背景图「解码→模糊→JPEG 重编码」 | 50~90 ms | 结果按「路径+大小+mtime+模糊半径」落盘缓存，命中即跳过 |
| 物理显卡枚举 | 20~80 ms | `SettingsViewModel.EnsureGpuDevicesLoaded()` 后台枚举后回填 |
| 其余页面装配 + 截图编辑器预热 | 数百 ms | 首帧后逐页装配，页间让出一帧（`MainWindow.TryPreloadNextPage`） |

落地后的关键约束，改动时不要破坏：

1. `OcrCoordinator` **构造函数禁止**创建引擎。首次识别与启动预载共用同一次后台加载，
   靠 `Task<IOcrEngine>? _engineReadyTask` 去重；`IsEngineFaulted` 在引擎未就绪时返回 `false`
   （未就绪 ≠ 故障），`TryRecoverFaultedEngine` 也必须对 null 放行。
   守卫测试：`OcrEngineLazyInitTests`（构造函数不加载 / 并发识别只加载一次 / 预载不影响识别）。
2. 启动预热统一走 `App.RunStartupWarmup`：先起引擎预载（后台），`Task.Delay` 让出首帧，
   再做剪贴板服务 → 模型缺失提醒 → 逐页装配（每页间隔）→ 最后才是截图编辑器预热。
   **不要把这些步骤合并成一个 `Dispatcher.Post`**，那等于把分帧的卡顿重新拼成一次长阻塞。
3. 全局热键在 `Show()` 之前注册（只是一组 `RegisterHotKey`），但热键回调**不得**依赖
   `_muClipService` 非空 —— 剪贴板服务是启动后异步就绪的，回调里再触发 `EnsureMuClipServiceInitialized()`。
4. 诊断入口：日志里的 `[启动] ... +N ms` 里程碑（基础服务 / ViewModel / 窗口对象 / 主窗口呈现 /
   后台预热全部完成）就是量化依据。改启动路径后请贴这组数字做前后对比，不要凭感觉。

---

### 3.12 退出路径：UI 先消失，清理限时

**铁律：托盘图标与窗口的消失时机，不能依赖进程结束。** 托盘图标（`Shell_NotifyIcon`）只有在显式
`Dispose` / `IsVisible=false` 时才立即从通知区域移除；否则要等进程真正退出后由资源管理器代为清理
—— 进程活多久，图标就残留多久。历史现象"退出后图标过几秒才消失"就是这条链路：
`OnDesktopExit` 里 `.Wait(2s)` 等录屏 + `MuClipService.Dispose()` 同步 800ms + 热键线程 `Join(1s)`。

现行顺序（`App.OnTrayExitClick` → `OnDesktopExit`）：

1. `RemoveTrayIconsNow()` → 托盘图标立即消失（实测 42 ms 内完成）。
2. 关闭所有浮动窗 / 便笺窗 → `_mainWindow.ExplicitExit()`（任务栏按钮同时消失）。
3. 重活全部改为**后台并行任务**（幂等，只发起一次）：
   - `StartRecordingShutdown()`：停录制 + 回填 moov，**必须等**（预算 1500 ms，否则 mp4 不可播）。
   - `StartMuClipShutdown()`：Rust 侧释放，**不必等**（sqlite 每笔写入即提交），预算 250 ms。
4. `StartShutdownWatchdog()`：总预算 2500 ms，超时 `Environment.Exit(0)`，杜绝"僵尸退出"。
5. `OnDesktopExit` 只做剩余预算等待 + 同步释放（`>=50ms` 的步骤会打 `[退出] ... 释放耗时 N ms`）。

改动时的注意点：

- `OnDesktopExit` 阶段 dispatcher 已停止，**不要新建窗口或 Post 到 UI 线程**；`_coordinator.Dispose()`
  是例外（它要关浮窗，必须在 UI 线程执行）。
- 新增任何服务清理前先问一句：**它需不需要"完整性保证"**？需要才进阻塞预算，否则一律后台 + 短预算。
- 已知未优化项：`MuClipNative.SyncStop`（`muclip_core.dll`）单次约 800 ms，属 Rust 侧线程收尾，
  要压下去得改 `C:\Rust\MuClip` 并重出 DLL，不要试图在 C# 侧绕。
- 验证方式：日志里的 `[退出]` 系列时间戳。Debug 实测（含剪贴板服务）：图标 42 ms、清理全部完成 332 ms。

---

## 4. 常用构建与验证命令

验证分**快档**与**全量**两档。**不要每次改动都跑全量** —— 全量约 2 分钟，而其中 98% 的耗时来自真实推理/内存池测试，其余纯逻辑测试只需 4 秒。

### 4.1 改动期间：快档（约 4 秒 / 315 项）

```bash
# 1. 编译（必须 0 警告、0 错误）
dotnet build

# 2. 跳过慢测试跑一遍，确认没伤到别处
dotnet test --filter "Category!=Slow"
```

迭代更快的做法是按 4.2 的映射表**只跑受影响的那部分**：

```bash
dotnet test --filter "FullyQualifiedName~OcrTableLayout"
dotnet test --filter "FullyQualifiedName~TableLineDetector"
```

`--filter` 支持 `~`（包含）、`!~`（不包含）与 `&`/`|` 组合，例如 `--filter "FullyQualifiedName~OcrCoordinator&Category!=Slow"`。

### 4.2 改动区域 → 应跑的测试

| 改动位置 | 必跑测试类 |
| --- | --- |
| `src/ShawMuOCR.Core/Sorting/` | `OcrTableLayoutBuilderTests`、`OcrTableLayoutHtmlTests`、`TableLineDetectorTests`、`OcrTableLayoutLineAwareTests`、`DefaultOcrTextSorterTests` |
| `src/ShawMuOCR.Core/Clipboard/`、`src/ShawMuOCR.Windows/Clipboard/` | `CfHtmlBuilderTests`、`OcrCoordinatorTableCopyTests`、`OcrCoordinatorSelectionTests` |
| `src/ShawMuOCR.Core/Settings/`、`ViewModels/SettingsViewModel.cs` | `SettingsAndLoggingPathTests`、`SettingsAutoSaveTests`、`SettingsPersistenceSecurityTests` |
| `Services/OcrCoordinator.cs` | `OcrCoordinatorTableCopyTests`、`OcrCoordinatorSelectionTests` |
| `Services/Pdf/`、PDF 分支 | `PdfPageRendererTests`（慢）、`FileOcrTests` |
| `Services/Batch/` | `BatchOcrTests`、`BatchExportHelperTests`、`CpuThrottleSchedulerTests` |
| `OCR/`、`src/ShawMuOCR.Core/OCR/` | `PaddleOcrV6EngineTests`（慢）、`ModelSelectionTests`、`ExternalApiOcrEngineTests` |
| `Services/Http/`、HTTP 服务端 | `HttpOcrServerTests` |
| 翻译相关 | `TranslationServiceTests`、`PhraseReorderAndConditionalRuleTests` |
| MuClip 同步 | `MuClipMemoryPoolTests`（慢） |
| `src/ShawMuOCR.Windows/ScreenCapture/`、`src/ShawMuOCR.Windows/Win32/` | `ScreenCaptureServiceTests`、`DibBufferPackerTests`、`ScreenRectTests`（含 GDI 句柄泄漏校验，需真实桌面会话） |
| 长截图（位移估计 / 拼接 / 采集循环） | `LongScreenshotStitcherTests`、`LongScreenshotCapturerTests`、`OcrCoordinatorLongScreenshotTests` |
| `.axaml` / 纯样式 | 只需 `dotnet build`；行为改动再看对应 ViewModel 的测试 |

### 4.3 收尾：全量（约 2 分钟 / 345 项）

以下三种情况**必须**跑全量 `dotnet test`：

1. `dotnet publish` 发布前；
2. 提交 / 合并前；
3. 改动了跨模块公共契约（`IClipboardService`、`OcrTextBlock`、`OcrResult`、`AppSettings`、`ISettingsService` 等）或测试工程配置。

### 4.4 慢测试清单（`Category=Slow`）

| 测试类 | 项数 | 耗时 | 慢的原因 |
| --- | --- | --- | --- |
| `PaddleOcrV6EngineTests` | 9 | ~84s | 真实 onnx 模型加载 + 推理 |
| `MuClipMemoryPoolTests` | 18 | ~19s | 大块内存分配与 GC 压力测试 |
| `PdfPageRendererTests` | 2 | ~4s | PDF 栅格化 + 真实 OCR |
| `OcrTableLayoutRealOcrTests` | 4 | ~2s | 真实 OCR 端到端 |
| `OcrTableLayoutLineAwareTests.RecognizeAsync_*` | 1 | ~0.6s | 真实 OCR 端到端 |

**新增测试如果单次超过 1 秒，必须打上 `[Trait("Category", "Slow")]`**（可以打在类上，也可以只打在某个方法上），否则快档会逐渐退化成全量。

### 4.5 验证 Native AOT 发布（发布前）

```bash
dotnet publish ShawMuOCR.csproj -c Release -r win-x64
```

发布前若 `ShawMuOCR.exe` 正在运行会报 `MSB3027`（文件被占用），需先关闭该进程。

---

## 5. 给后续 AI Agent 的行为守则

1. **保持文档与注释完整性**：绝不随意删除既有的功能说明、架构注释与中英双语文档。
2. **测试门禁（分层执行，不要每次全量）**：
   - 改动期间跑 `dotnet test --filter "Category!=Slow"`（约 3 秒），或按 4.2 映射表只跑受影响的类；
   - 发布 / 提交前跑全量 `dotnet test`（约 2 分钟）；
   - 引入新功能需补齐对应的 xUnit 单元测试，耗时超过 1 秒的记得打 `Category=Slow`。
3. **输出清晰的引用链接**：与用户交流时，涉及代码文件与符号时必须使用标准 Markdown 链接（如 `[OcrEngineFactory.cs](file:///c:/ShawMuOCR/OCR/OcrEngineFactory.cs)`）。
