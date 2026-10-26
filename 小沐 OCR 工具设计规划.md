# 小沐 OCR 工具设计规划

## 一、项目目标

开发一个基于 **C# + .NET + Avalonia UI** 的 Windows 桌面 OCR 工具。

核心体验参考 Umi-OCR，但第一阶段只专注于一个核心工作流：

> **快捷键 → 截屏 → 鼠标框选 → OCR → 自动复制文字**

程序启动后常驻后台，在 Windows 系统任务栏托盘运行。

程序需要具备良好的模块化设计，使 OCR 引擎未来可以方便替换，例如：

- PP-OCRv6
- PP-OCRv7
- PaddleOCR-VL
- 其他 ONNX OCR 模型
- 自定义 OCR 引擎

第一版不要过度设计功能，但代码架构必须为后续升级留下明确接口。

---

# 二、技术栈

## 基础框架

- C#
- .NET 11
- Avalonia UI
- Windows 10 / Windows 11

## UI

使用 Avalonia。

主要负责：

- 主窗口
- OCR 图片预览
- OCR 结果显示
- 设置页面
- 快捷键设置
- 状态显示
- 关于页面
- 托盘菜单相关 UI 状态

不要让 Avalonia UI 层直接承担底层 Windows API 工作。

---

# 三、整体架构

采用分层架构：

```text
OCRApplication
│
├── UI
│   ├── MainWindow
│   ├── SettingsWindow
│   ├── ViewModels
│   └── Views
│
├── Core
│   ├── OCR
│   ├── Capture
│   ├── Selection
│   ├── Clipboard
│   ├── Hotkey
│   ├── Settings
│   └── Services
│
└── Platform.Windows
    ├── ScreenCapture
    ├── GlobalHotkey
    ├── Clipboard
    ├── TrayIcon
    └── WindowsInterop
```

原则：

```text
Avalonia UI
    ↓
Core 接口
    ↓
Windows 实现 / OCR 实现
```

不要让：

```text
MainWindow.axaml.cs
```

里面出现大量 Win32 API、OCR 推理和图片处理代码。

---

# 四、核心 OCR 架构

定义统一 OCR 接口：

```csharp
public interface IOcrEngine
{
    Task<OcrResult> RecognizeAsync(
        OcrImage image,
        CancellationToken cancellationToken = default);
}
```

OCR 结果：

```csharp
public sealed class OcrResult
{
    public string Text { get; init; } = string.Empty;

    public IReadOnlyList<OcrTextBlock> Blocks { get; init; }
        = Array.Empty<OcrTextBlock>();

    public TimeSpan Elapsed { get; init; }
}
```

文字块：

```csharp
public sealed class OcrTextBlock
{
    public string Text { get; init; } = string.Empty;

    public float Confidence { get; init; }

    public Rect BoundingBox { get; init; }
}
```

未来可以实现：

```text
IOcrEngine
    │
    ├── PaddleOcrV6Engine
    │
    ├── PaddleOcrV7Engine
    │
    ├── PaddleOcrVlEngine
    │
    └── CustomOcrEngine
```

UI 和业务逻辑只依赖 `IOcrEngine`，不允许直接依赖某个具体 PaddleOCR 模型。

---

# 五、第一版 OCR 引擎

第一版使用：

**PP-OCRv6**

优先选择：

**PP-OCRv6_small 或 PP-OCRv6_medium**

PP-OCRv6 官方目前提供：

```text
tiny     1.5M
small    7.7M
medium   34.5M
```

small / medium 支持中文、英文、日文等 50 种语言。

对于本项目：

```text
默认：
PP-OCRv6_small

可选：
PP-OCRv6_medium
```

最终通过实际 Windows CPU 测试决定默认模型。

---

# 六、OCR 推理后端

OCR 模型和 OCR Runtime 必须解耦。

设计：

```text
IOcrEngine
    ↓
PaddleOcrV6Engine
    ↓
IOcrRuntime
    ↓
ONNX Runtime
```

例如：

```csharp
public interface IOcrRuntime
{
    Task<OcrRuntimeResult> RunAsync(
        OcrInput input,
        CancellationToken cancellationToken = default);
}
```

这样未来可以更换：

```text
ONNX Runtime
OpenVINO
DirectML
CUDA
TensorRT
其他 Runtime
```

而不用修改上层 OCR 逻辑。

PP-OCRv6 官方已经提供 ONNX Runtime 高性能推理路径，因此项目应优先考虑 ONNX Runtime，而不是在桌面程序中直接嵌入完整 Python/Paddle 环境。

---

# 七、不要在第一版嵌入 Python

程序最终目标：

> 下载 / 解压 → 双击 → 使用

因此不要采用：

```text
C# Avalonia
    ↓
启动 Python
    ↓
Python PaddleOCR
    ↓
返回 JSON
```

这种方案虽然开发初期比较容易，但会带来：

- Python 环境
- PaddlePaddle
- DLL
- PATH
- 环境变量
- Python 进程启动时间
- 模型加载
- 发布体积
- 用户电脑环境问题

第一版尽量做成：

```text
C#
 ↓
ONNX Runtime
 ↓
PP-OCRv6 ONNX
```

最终发布为 Windows 自包含程序。

---

# 八、截屏流程

用户按下 OCR 快捷键：

```text
Global Hotkey
       ↓
开始截图
       ↓
获取所有显示器
       ↓
创建透明全屏选择层
       ↓
用户拖动鼠标
       ↓
确定矩形区域
       ↓
截图并裁剪
       ↓
关闭选择层
       ↓
提交 OCR
```

例如：

```text
Ctrl + Shift + C
```

触发：

```text
ScreenCaptureService.StartSelection()
```

---

# 九、选区界面

选区界面使用 Avalonia 创建透明 Overlay。

要求：

- 覆盖当前所有显示器
- 半透明背景
- 鼠标拖动选择矩形
- 显示当前选择区域
- 显示选区宽度和高度
- 鼠标松开后确认
- ESC 取消
- 不保存截图到磁盘

效果类似：

```text
┌──────────────────────────────────────────────┐
│                                              │
│       半透明遮罩                             │
│                                              │
│        ┌──────────────────────┐              │
│        │                      │              │
│        │     被选择区域       │              │
│        │                      │              │
│        └──────────────────────┘              │
│                                              │
└──────────────────────────────────────────────┘
```

---

# 十、Windows DPI 和多显示器

这是项目必须重点处理的问题。

必须考虑：

- Windows 缩放 100%
- 125%
- 150%
- 175%
- 多显示器
- 不同显示器 DPI
- 主显示器 / 副显示器
- 显示器坐标可能存在负数

内部统一定义一个屏幕坐标系统。

例如：

```csharp
public readonly record struct ScreenRect(
    int X,
    int Y,
    int Width,
    int Height);
```

截图和 Avalonia 坐标转换必须集中处理。

禁止在多个 UI 文件里到处写：

```text
* 1.25
/ 1.5
```

这种魔法数字。

---

# 十一、图片处理

截屏完成以后：

```text
Screen Bitmap
      ↓
Crop
      ↓
OcrImage
      ↓
OCR
```

OCR 使用的图片全部保存在内存中。

第一版：

**不要把用户截图保存到临时 PNG/JPG 文件。**

避免：

```text
截图
 ↓
C:\Temp\xxx.png
 ↓
OCR
 ↓
删除
```

优先：

```text
截图
 ↓
MemoryStream / Bitmap
 ↓
OCR
 ↓
释放
```

---

# 十二、内存管理

这是项目的重要设计目标。

一次 OCR 任务的数据生命周期：

```text
创建截图
    ↓
OCR
    ↓
生成结果
    ↓
复制文字
    ↓
释放截图 Bitmap
    ↓
释放 OCR 输入 Buffer
    ↓
释放 OCR 临时对象
```

不保存历史图片。

不保存 OCR 原始图片。

不默认保存 OCR JSON。

不默认保存识别记录。

---

# 十三、当次 OCR 数据模型

可以建立：

```csharp
public sealed class OcrSession : IDisposable
{
    public OcrImage? Image { get; }

    public OcrResult? Result { get; }

    public void Dispose()
    {
        // 释放本次 OCR 使用的资源
    }
}
```

原则：

> OCR 完成以后，只有 UI 当前需要显示的内容继续存在。

例如：

```text
截图 Bitmap
    ↓
OCR
    ↓
Text
    ↓
复制 Clipboard
```

截图 Bitmap 可以立即释放。

---

# 十四、UI 图片预览

主界面显示最近一次 OCR 的截图。

布局建议：

```text
┌──────────────────────────────────────────────┐
│  OCR Tool                         ⚙ 设置     │
├──────────────────────┬───────────────────────┤
│                      │                       │
│                      │   OCR 识别结果        │
│     图片预览          │                       │
│                      │   这里显示文字        │
│                      │                       │
│                      │                       │
├──────────────────────┴───────────────────────┤
│ 状态：识别完成       识别耗时：85 ms         │
└──────────────────────────────────────────────┘
```

左侧：

```text
Image Preview
```

右侧：

```text
OCR Result
```

结果区域使用可复制的文本控件。

---

# 十五、自动复制

OCR 成功以后：

```text
OCR Result
    ↓
Clipboard
```

默认行为：

> 自动将识别结果复制到 Windows 剪贴板。

例如：

```csharp
await clipboard.SetTextAsync(result.Text);
```

同时 UI 显示：

```text
✓ 已复制
```

如果 OCR 失败：

```text
✕ OCR 失败
```

不能静默失败。

---

## 15.1 表格智能分列（HTML + 纯文本双格式剪贴板）

截图识别表格后直接粘贴到 Excel，若剪贴板只有空格分隔的纯文本，整行会挤进一个单元格，
需要手工「分列」。因此自动复制前先判断识别结果是否呈表格结构。

```text
OCR Blocks (含 BoundingBox) + 源图像
    ↓
【路径 A】表格线检测（优先）
    下采样 → 逐列统计暗像素占比 → 竖线候选 → 合并相邻/剔除外框
    → 内部竖线 + 图像左右边界 = 列区间
    ↓ 有内部竖线？                ↓ 没有
    ↓（用线分列）                 ↓（路径 B 回退）
    ↓                        行聚类（垂直中心 Y）
    ↓                            ↓
    ↓                        X 轴逐行投影 → 列间隙 → 列区间
    └──────────────┬──────────────┘
                   ↓
按行/列填充网格（空单元格留空；合并表头记录 colspan）
    ↓
判定：列数 ≥ 2 && 单元格密度 ≥ 30%
（只要能分出多列就按检测框分列，不再要求"行数 ≥ 2 / 跨列行 ≥ 2"——
 旧版这些门槛会让「一行数据」和「紧凑表格」直接判不出来，退化成空格纯文本）
    ↓
┌───────────────┴───────────────┐
是                              否
↓                               ↓
剪贴板写两种格式：                剪贴板只写纯文本
  CF_UNICODETEXT = 原纯文本        （与旧版完全一致）
  CF_HTML        = <table> 表格
```

为什么要写两种格式：只写 TSV 会把制表符带进聊天窗口、搜索框，出现宽空档；
写 HTML + 纯文本后，Excel / WPS / Word 会优先读 CF_HTML 落成单元格，
而聊天窗口、记事本、搜索框只会读到纯文本，两类目标都正确。

为什么必须加「表格线优先」路径：OCR 文本检测（DB 系）会按固定比例向外膨胀文本框，
膨胀量随文字高度同步增长。当左右两列的真实空白较窄时，膨胀后的两个框会贴合甚至重叠，
检测框之间的空隙就从"够宽"掉到"不够宽"的临界点附近。实测同一张 2 列坐标表
（311×270，列间隙 10px、行高 29px、投影阈值 10.15px）—— 图上没有任何变化，
判定却会随机翻转，这正是用户反馈的「有时候能识别出两列有时候不行」。
表格自带的竖线是像素级稳定证据，不受膨胀影响，因此优先采用。

关键取舍：

| 决策 | 说明 |
| --- | --- |
| 表格线优先于投影法 | 竖线是像素级证据；实测同一张图缩放 0.35~3.0 倍，投影法 7 次全败，线法 7 次全成 |
| 逐行投影而非全体投影 | 每行对某像素列最多计 1 次，居中合并的表头不会抹平列间隙 |
| 列间隙阈值 = 0.35~0.6 × 行高（按行数分档） | 定成 1 倍行高时，紧贴表格（长数字并排，间隙仅 0.4~0.5 行高）会被合并成一列 |
| 列间隙绝对下限 10px | 低于约一个字符宽的空白视为同一单元格内的碎框间隔，避免"华南 区"多切一列 |
| 不要求最小行数 | 单行数据（一行经纬度/一行记录）同样需要分列 |
| 合并单元格（整行单框且跨多列） | 固定落到第 1 列，并用 `colspan` 表达跨列数，粘贴到表格后不会因原图居中而跳格 |
| 单元格内多个框 | CJK 直连、西文补空格，规则与 `DefaultOcrTextSorter` 共用 `OcrCjkTextHelper` |
| 竖线覆盖率阈值 55% | 文字笔画只在局部行出现，整列 55% 以上是暗像素才算线，可排除文字与背景纹理 |
| 两档墨迹阈值 | 绝对深色（128）抓黑线，失败再按"背景亮度 − 42"放宽，覆盖 Excel 默认浅灰网格线（#D0D0D0） |
| 只认内部竖线 | 贴图像左右边缘 2% 内的线是截图外框，不算分隔线；只有外框时回退投影法 |
| 剔除整列无字的列区间 | 装饰线、窗口分隔条会带来空列，否则粘到 Excel 会多一列 |
| 下采样到约 20 万采样点 | 分列判定在每次识别后执行：311×270 约 5.6ms、1080p 约 13.9ms、4K 约 14.5ms，与图像面积基本无关 |
| CF_HTML 偏移量 | 全部按 UTF-8 **字节**偏移计算（中文会暴露按字符数计算的错误），头部偏移字段固定 10 位 |
| HTML 写入失败 | 纯文本已先写入，剪贴板仍可用；不因增强格式失败导致「粘不出东西」 |
| 非表格内容 | 返回 null，行为与旧版完全一致 |

实现：
- `src/ShawMuOCR.Core/Sorting/OcrTableLayoutBuilder.cs`（行列还原）、`OcrTableLayout.cs`（`ToHtmlTable` / `ToTsv`）
- `src/ShawMuOCR.Windows/Clipboard/CfHtmlBuilder.cs`（CF_HTML 包装）、`WindowsClipboardService.SetTextWithHtmlAsync`

设置项：`AppSettings.AutoCopyTableAsCells`（默认开启，设置页可关闭）

---

## 15.2 PDF 识别与整页栅格化回退

PdfPig 只能拿到「文字层」和「内嵌位图」，而 GIS / CAD / 排版软件导出的 PDF 常把文字转成路径：
既没有文字层（`page.Text` 为空），也没有内嵌图片（`GetImages()` 返回 0），
旧逻辑会输出「只有页码标题的一页」。识别流程改为三级回退：

```text
打开 PDF
    ↓
page.Text 非空 ──是──→ 直接用文字层（最快，无需 OCR）
    ↓ 否
GetImages() 有图 ──是──→ 逐张位图 OCR（扫描件，保真）
    ↓ 否
整页渲染成位图（PdfPig.Rendering.Skia）──→ OCR
    ↓ 渲染失败
输出「[该页无法渲染为图像：原因]」+ 写入日志，不再静默空白
```

| 决策 | 说明 |
| --- | --- |
| 渲染目标宽度 1600px | 太小影响小字识别，太大拖慢 OCR；按页面实际宽度算 scale 并钳制在 1.0~3.0 |
| 渲染结果不再旋转 | 渲染器已按 PDF `/Rotate` 输出视觉正确方向，再套一次旋转会歪 |
| 增加依赖 `PdfPig.Rendering.Skia` | 唯一能在 AOT 下把 PDF 栅格化的方案（另带 DCT/JBIG2/JPX 过滤器），已实测矢量型 PDF 可正常渲染 |

实现：`Services/Pdf/PdfPageRenderer.cs`，接入 `MainViewModel.RecognizePdfFileAsync` 与 `BatchOcrProcessor.ProcessPdfFileAsync`

---

## 15.3 屏幕捕获零拷贝（DIB Section）

截图的旧链路是「两步走」：

```text
GetDC(桌面)
    ↓
CreateCompatibleDC → CreateCompatibleBitmap   ← 建 DDB（可能落在显存）
    ↓
BitBlt(SRCCOPY | CAPTUREBLT)                  ← 先把屏幕画进 DDB
    ↓
GetDIBits(SKBitmap.GetPixels())                ← 再从 DDB 转成 DIB 拷进 SKBitmap（整图一次）
```

`GetDIBits` 是纯浪费：它做一次格式转换 + 一次整图拷贝，而且当 DDB 被 GDI 留在显存时，
这次拷贝要跨 PCIe 回读，代价会突然翻倍。改成一步：

```text
GetDC(桌面)
    ↓
CreateCompatibleDC → CreateDIBSection         ← 直接建 DIB，系统把像素内存映射进进程地址空间
    ↓
BitBlt(SRCCOPY | CAPTUREBLT)                  ← 屏幕像素直接落在这块内存里
    ↓
SKBitmap.InstallPixels(bits, rowBytes, releaseProc)  ← 零拷贝挂接，不复制
```

| 决策 | 说明 |
| --- | --- |
| 用 `CreateDIBSection` 而不是 `CreateCompatibleBitmap` | 前者返回可直接读写的 `ppvBits`，BitBlt 结果就在里面；后者还得再 `GetDIBits` 拷一次 |
| 行跨度取 `width * 4` | 32bpp + `BI_RGB` 时无行对齐填充，`RowBytes` 必然等于 `width * 4`（不能像 24bpp 那样猜 padding） |
| `biHeight` 用负数 | 自顶向下，避免图像上下颠倒后再翻转一次 |
| `InstallPixels` 带 releaseProc | SKBitmap 不拥有这块内存，必须在 Dispose 时回调用 `DeleteObject` 归还 DIB，否则每次截图漏一个 GDI 对象 |
| releaseProc 的 context 用包装类而非裸句柄 | 句柄只删一次，重复回调不会误删已被系统复用的句柄值 |
| `InstallPixels` 失败时退化为一次 `Buffer.MemoryCopy` | 极端兜底，绝不返回空图；纯 memcpy 比 `GetDIBits` 便宜得多 |

实测（1920×1080，轮转交错采样剔除屏幕内容漂移）：

| 场景 | 旧路径 | 新路径 |
| --- | --- | --- |
| 受控内存源 DC（纯提取开销） | 4.3~5.2 ms | 1.3~1.7 ms（约 3 倍） |
| 真实桌面 DC（端到端） | 14.6~21.5 ms | 13.5~19.3 ms |

端到端只省 1.5~2.2 ms，因为屏幕回读的 `BitBlt` 本身就要 12~19 ms，那才是硬底。
**捕获速度的天花板在 `BitBlt` 从桌面 DC 读像素这一步，不在格式转换。**
换 DIB Section 的另一个隐性收益：不再每次分配一块 8 MB 的 `SKBitmap`，长截图循环里 GC 与页表压力明显下降。

实现：`src/ShawMuOCR.Windows/ScreenCapture/WindowsScreenCaptureService.cs`，
`src/ShawMuOCR.Windows/Win32/Gdi32.cs` 的 `CreateDIBSection` / `CreateTopDownBgraInfo`

### 同一套做法的第二个落点：截图遮罩层

截图热键按下后会建三块全屏缓冲——原图、暗化底图、后台双缓冲。旧做法是每块都 `CreateCompatibleBitmap`，
原图和暗化图各来一次 `SetDIBits`（各一次整图转换），暗化图还要先 `Marshal.AllocHGlobal` 一块 8 MB 临时缓冲再算。
三块全改 `CreateDIBSection` 后：原图一次 `memcpy` 直接进 DIB 内存，暗化图**直接写进目标 DIB**，
`AllocHGlobal` 与两次 `SetDIBits` 全部消失。

| 阶段 | 原实现 | 全 DIB | 混合（呈现缓冲保留 CompatibleBitmap） |
| --- | --- | --- | --- |
| 初始化 | 9.83 ms | **5.92 ms** | 5.24 ms |
| 每帧重绘（暗底全屏 + 选区抠图） | 0.396 ms | **0.362 ms** | 0.406 ms |

初始化省约 4 ms，每帧重绘不退化。**没有选混合方案**：它初始化更快 0.7 ms，但每帧慢了 0.04 ms，
而每帧重绘在一次拖拽里要跑上百次——一次性的 0.7 ms 不值得拿交互流畅度去换。

裸指针那两段（逐行拷贝、暗化）抽到了 `DibBufferPacker`，因为它跑在自建消息循环线程里无法驱动单元测试，
而这类代码最容易写错行跨度与通道顺序。

---

## 15.4 长截图（跟随用户手动滚动）

长截图有两种做法：**控速滚动**（我们替用户发滚轮/键盘事件，节奏由程序定）和**跟随手动滚动**（用户自己滚，我们只负责认帧）。
本版选后者，理由是不去抢输入焦点、不需要识别目标应用是浏览器还是 PDF 阅读器、也不会因为应用把动画做得很慢而导致截出来的图缺行。

```text
框选区域 → 连续抓帧 → 相邻两帧比对算出「内容上移了多少像素」→ 只把新露出的那几行接到画布底部
                                  ↑                                              ↓
                          用户停止滚动 700ms 就收尾          拼好的长图直接进剪贴板
```

整条链路拆成三块，各自可独立测试：

| 模块 | 位置 | 职责 | 不负责 |
| --- | --- | --- | --- |
| `VerticalShiftEstimator` | `src/ShawMuOCR.Core/Capture/` | 给定前后两帧，算出内容上移了多少像素，并判断这次估计是否可信 | 不碰屏幕、不碰画布 |
| `LongScreenshotStitcher` | `src/ShawMuOCR.Core/Capture/` | 按位移把新行追加到画布，管理扩容与高度上限 | 不管帧从哪来、位移怎么算 |
| `LongScreenshotCapturer` | `src/ShawMuOCR.Windows/ScreenCapture/` | 抓帧节奏、退出条件、结果封装 | 不做像素算法 |

### 位移估计为什么不能用「逐像素滑窗对位」

第一版按 8 像素步长跳采做二维模板匹配，7 项测试全挂：真实位移 0 被估成 150，300 被估成 41。
根因是跳采时 `previous` 的模板带与 `next` 的候选区域落在「同一组行号错位」的行上，
位移不是步长整数倍时整条带都在错行，真实峰（`score=0.00`）被整个跳过，
最优解被噪声（26~28 分）抢走——实测 `top=384 score=32.05 / top=392 score=48.70 / top=400 score=32.17`，而真值在 `top=390`。

最后改成**行投影（row profile）**：

```text
每帧 → 把每一行压成一个平均亮度 → 得到长度 = 帧高的 1 维信号
     → 取帧高 65% 处一条 25% 高的模板带 → 在 next 的投影上滑窗找最小 SAD
     → 天然 1 像素精度，不存在「不是步长整数倍就错行」的问题
     → 再用二维 SAD 复核前几个候选，确认不是周期性纹理骗出来的假峰
```

三个必须处理的坑：

| 坑 | 表现 | 处理 |
| --- | --- | --- |
| 周期性混叠 | 等距行高的页面（表格、代码清单）会让多个位置的投影分数完全相等，「取前 K 名」按扫描顺序会吃掉前面的并列项，真实位移进不了候选表 | 用**容差收候选**（`min + 2.0 + 25% × min`，上限 12 个）而不是固定取前 K，并按距离去重 |
| 模板带位置 | 带越靠上，能检测的前向位移上限越小（带会整条离开视野） | 取帧高 65% 处，1080p 下可检测约 700 px，同时保留反向余量 |
| 相似但无关的画面 | 同一套模板、换个数据，逐像素全不同但行结构相似，SAD 只有 26（低于阈值 42），会被误判成可信 | 置信度判据收紧到「最优 / 次优 < 0.75」，并要求二维复核平均差 < 42 |

性能：1080p 单帧估计 **1.65 ms**（初版 9.97 ms）。三处优化——横向采样步长 8→16、
行投影改成单趟融合不分配中间灰度数组、候选挑选不再整体排序。

### 采集循环的三重退出

用户可能一直不滚、滚两下就停、或者在长列表上来回滚不停。三种都要收得住：

| 条件 | 阈值 | 结果 |
| --- | --- | --- |
| 一直没检测到滚动 | 12 s | 返回「等待滚动超时」 |
| 滚过之后静止 | 700 ms | 正常收尾出图 |
| 连续估计失败 | 8 帧 | 返回「没有检测到有效滚动（原因）」 |
| 帧数上限 | 1500 帧 | 强制收尾 |
| 拼接高度上限 | 16000 px | 标记 `IsFull`，后续内容不再收入 |

用户往回滚不算「停止操作」：负位移由拼接器记一笔 `IgnoredScrollBackCount` 后丢弃，但仍然刷新空闲计时，
否则一次回看就会被误判成结束。

帧间隔上有个容易踩的坑：Windows 的 `Task.Delay` 最小粒度约 15 ms，无条件 `Delay(1)` 会把循环硬压到 ~30 fps。
现在改成「记录上一帧耗时，只有快于 12 ms 才补差」，而单次捕获本身要 14~19 ms，所以实际不会额外等待，
循环节奏就是捕获节奏（约 50~60 fps），同时捕获慢的时候也不会空转吃满一个核。

### 已知瓶颈（都不在捕获速度上）

1. **应用渲染延迟**——滚动后目标窗口要重排重绘，抓得太快会截到「半渲染」的中间态，导致位移估计失败。靠「连续失败 8 帧才放弃」兜住，不重试同一帧。
2. **配准误匹配**——大面积纯色区域或重复图标列表（例如等距头像墙）没有可用的行投影特征，只能报不可信。
3. **BitBlt 抓不到 DirectComposition 图层**——部分 UWP/Electron 应用的内容走在合成器里，`BitBlt + CAPTUREBLT` 可能拍成黑块。这类应用只能走窗口渲染层方案（`PrintWindow` / 应用内截图 API），本版不做。

### 本版范围

只做「框选 → 跟随手动滚动 → 拼好进剪贴板」这一条最小闭环：

- 已接入：热键 `Alt+L`（`AppSettings.LongScreenshotHotkey`）、托盘菜单「长截图」、采集中再按一次热键即收尾、
  完成后写入剪贴板并在主窗口预览。
- 未接入：不落盘、不接 OCR/翻译、不做中途预览、设置页暂不提供热键录制入口（复用 `ShowCaptureImageNotification` 的气泡开关）。
- 代价：exe 从 35.50 MB 增至 **35.54 MB**。

实现：`src/ShawMuOCR.Core/Capture/VerticalShiftEstimator.cs`、`LongScreenshotStitcher.cs`、
`src/ShawMuOCR.Windows/ScreenCapture/LongScreenshotCapturer.cs`；
回归：`LongScreenshotStitcherTests`（20 项）、`LongScreenshotCapturerTests`（9 项）、
`OcrCoordinatorLongScreenshotTests`（2 项，验证协调器接线）。
拼接器的验证方式是「一张 3000 px 高的合成文档按已知偏移切视口」，拼完与原文档**逐字节比对**。

---

# 十六、OCR 文本排序

PP-OCR 会返回多个文字区域。

不能简单按照模型返回顺序直接拼接。

第一版采用：

```text
按照 Y 坐标排序
    ↓
同一行按照 X 坐标排序
```

例如：

```text
Hello
World
123
```

最终：

```text
Hello
World
123
```

后续可以增加：

- 行聚类
- 中英文混排
- 多列检测
- 表格识别
- 阅读顺序优化

---

# 十七、全局快捷键

提供：

```text
GlobalHotkeyService
```

负责：

- 注册快捷键
- 注销快捷键
- 检测快捷键冲突
- 修改快捷键

默认快捷键：

```text
Ctrl + Shift + C
```

用户可以在设置中修改。

快捷键设置 UI：

```text
截图 OCR 快捷键

[ Ctrl + Shift + C ]

        [修改]

提示：
按下新的组合键即可设置
```

必须支持：

- Ctrl
- Shift
- Alt
- Win
- 字母
- 数字
- F1-F12

---

# 十八、托盘运行

程序启动以后：

```text
应用程序
   ↓
主窗口
   ↓
关闭窗口
   ↓
不退出程序
   ↓
后台托盘运行
```

任务栏托盘菜单：

```text
┌─────────────────────┐
│ 截图 OCR             │
├─────────────────────┤
│ 开始截图 OCR         │
│ 打开主界面           │
│ 设置                 │
├─────────────────────┤
│ 退出                 │
└─────────────────────┘
```

关闭主窗口默认：

> 隐藏窗口，而不是退出程序。

真正退出通过托盘菜单：

```text
退出
```

---

# 十九、启动行为

设置中提供：

```text
☑ 开机自动启动

☑ 启动后隐藏主窗口

☑ OCR 完成后自动复制

☑ OCR 完成后显示主窗口
```

第一版可以只实现：

```text
开机启动
启动后隐藏
```

其他功能后续增加。

---

# 二十、设置系统

设置保存为：

```text
%AppData%\MiniOCR\settings.json
```

例如：

```json
{
  "hotkey": "Ctrl+Shift+C",
  "autoCopy": true,
  "showWindowAfterOcr": true,
  "startWithWindows": false,
  "model": "PP-OCRv6_small"
}
```

使用强类型：

```csharp
public sealed class AppSettings
{
    public string Hotkey { get; set; } = "Ctrl+Shift+C";

    public bool AutoCopy { get; set; } = true;

    public bool ShowWindowAfterOcr { get; set; } = true;

    public bool StartWithWindows { get; set; }

    public string Model { get; set; } = "PP-OCRv6_small";
}
```

---

# 二十一、设置页面

设置页面第一版包含：

## 通用

```text
快捷键
☑ OCR 后自动复制
☑ OCR 后显示主窗口
☑ 开机启动
```

## OCR

```text
OCR 模型：

○ PP-OCRv6 Small
● PP-OCRv6 Medium

推理设备：

● CPU
○ GPU
```

GPU 选项即使第一版暂时不实现，也可以预留接口。

---

# 二十二、模型管理

不要把模型路径硬编码到：

```text
PaddleOcrV6Engine
```

应该使用：

```csharp
public interface IOcrModelProvider
{
    string GetModelPath(string modelName);
}
```

模型目录：

```text
Models/
    PP-OCRv6/
        small/
        medium/
```

未来可以：

```text
Models/
    PP-OCRv6/
    PP-OCRv7/
    PaddleOCR-VL/
    Custom/
```

这样升级模型的时候不需要重新设计整个程序。

---

# 二十三、OCR Engine Factory

使用工厂：

```csharp
public interface IOcrEngineFactory
{
    IOcrEngine Create(string model);
}
```

例如：

```text
PP-OCRv6_small
        ↓
PaddleOcrV6Engine

PP-OCRv6_medium
        ↓
PaddleOcrV6Engine

PaddleOCR-VL
        ↓
PaddleOcrVlEngine
```

以后增加模型不会污染 UI。

---

# 二十四、程序生命周期

启动：

```text
Program
 ↓
Load Settings
 ↓
Initialize Tray
 ↓
Initialize Global Hotkey
 ↓
Initialize OCR Engine
 ↓
Show/Hide MainWindow
```

OCR：

```text
Hotkey
 ↓
Selection
 ↓
Capture
 ↓
OCR
 ↓
Result
 ↓
Clipboard
 ↓
Update UI
 ↓
Release Temporary Data
```

退出：

```text
Unregister Hotkey
 ↓
Dispose OCR Engine
 ↓
Dispose Tray
 ↓
Release Resources
 ↓
Exit
```

---

# 二十五、线程设计

UI 线程不能执行 OCR。

错误：

```csharp
private void StartOcr()
{
    var result = ocr.Recognize(image);
}
```

这会导致 UI 卡死。

应该：

```text
UI Thread
    │
    ├── 显示 UI
    ├── 鼠标
    └── 设置
         
OCR Worker
    │
    └── OCR 推理
```

使用：

```csharp
Task
CancellationToken
async / await
```

OCR 完成后再切回 UI 更新界面。

---

# 二十六、并发策略

第一版不允许同时执行多个 OCR。

如果用户疯狂按快捷键：

```text
OCR Running
    ↓
新的 OCR 请求
    ↓
拒绝 / 忽略
```

避免同时加载多个模型实例。

OCR Engine 在整个程序生命周期中保持一个实例：

```text
Application
    ↓
Singleton IOcrEngine
```

模型只加载一次。

这样可以避免每次截图都重新加载模型。

---

# 二十七、错误处理

需要明确区分：

```text
截图失败
快捷键注册失败
OCR 模型加载失败
OCR 推理失败
剪贴板失败
配置保存失败
```

不要出现：

```text
Exception
```

然后什么都不告诉用户。

日志可以写到：

```text
%LocalAppData%\MiniOCR\Logs\
```

但正常 OCR 数据不要写日志。

尤其不要把用户截图内容写入日志。

---

# 二十八、隐私设计

这个软件默认：

**完全本地 OCR。**

截图：

```text
只存在内存
```

OCR：

```text
本地模型
```

网络：

```text
默认不需要
```

识别文字：

```text
只存在内存
```

用户主动复制以后才进入：

```text
Windows Clipboard
```

程序退出以后：

```text
截图数据释放
OCR 临时数据释放
```

不要上传任何图片或文字。

---

# 二十九、项目目录

推荐：

```text
MiniOCR/
│
├── MiniOCR.sln
│
├── src/
│   │
│   ├── MiniOCR.App/
│   │   ├── App.axaml
│   │   ├── Program.cs
│   │   ├── Views/
│   │   ├── ViewModels/
│   │   └── Assets/
│   │
│   ├── MiniOCR.Core/
│   │   ├── OCR/
│   │   │   ├── IOcrEngine.cs
│   │   │   ├── IOcrRuntime.cs
│   │   │   ├── IOcrModelProvider.cs
│   │   │   ├── OcrResult.cs
│   │   │   └── OcrTextBlock.cs
│   │   │
│   │   ├── Capture/
│   │   ├── Selection/
│   │   ├── Clipboard/
│   │   ├── Hotkey/
│   │   ├── Settings/
│   │   └── Services/
│   │
│   └── MiniOCR.Windows/
│       ├── Win32/
│       ├── ScreenCapture/
│       ├── GlobalHotkey/
│       ├── Clipboard/
│       └── Tray/
│
├── models/
│   └── PP-OCRv6/
│
└── tests/
    ├── MiniOCR.Core.Tests/
    └── MiniOCR.Ocr.Tests/
```

---

# 三十、第一阶段 MVP

第一阶段严格限制功能，只实现：

```text
1. Avalonia 主窗口

2. Windows 托盘

3. 全局快捷键

4. 全屏透明截图选择层

5. Windows 截图

6. 图片裁剪

7. PP-OCRv6

8. OCR 文字排序

9. 自动复制

10. 主界面图片预览

11. 主界面 OCR 结果显示

12. 基础设置

13. 模型接口
```

完成这些以后才进入下一阶段。

---

# 三十一、第二阶段

加入：

```text
1. 多显示器优化

2. DPI 完整适配

3. OCR 历史记录

4. 一键重新识别

5. 图片放大

6. OCR 区域可视化

7. OCR 置信度显示

8. GPU 推理

9. 更多 OCR 模型
```

---

# 三十二、第三阶段

再考虑：

```text
PaddleOCR-VL

表格识别

公式识别

复杂文档识别

PDF OCR

批量 OCR

OCR 翻译

自定义模型

模型下载/更新
```

这些全部不要进入第一版。

---

# 三十三、性能目标

第一版性能目标：

### 程序启动

尽量做到：

```text
启动速度 < 1 秒
```

因此：

- 不启动 Python
- 不启动外部 OCR 服务
- 不等待网络
- UI 和 OCR 模型初始化可以考虑异步化

### OCR

目标：

```text
按快捷键
 ↓
框选
 ↓
松开鼠标
 ↓
尽快得到结果
```

OCR Engine 只初始化一次。

不要每次 OCR：

```text
加载模型
 ↓
OCR
 ↓
释放模型
```

应该：

```text
程序启动
 ↓
加载模型
 ↓
保持 Engine
 ↓
多次 OCR
```

---

# 三十四、内存原则

遵循：

> **模型常驻，任务数据短命。**

模型：

```text
PP-OCRv6
长期驻留内存
```

截图：

```text
一次 OCR
 ↓
创建
 ↓
使用
 ↓
释放
```

OCR Buffer：

```text
一次 OCR
 ↓
创建
 ↓
使用
 ↓
释放
```

结果：

```text
当前 UI 需要
 ↓
保留字符串
```

不建立无限历史缓存。

如果用户没有开启历史功能：

```text
上一张图片
 ↓
下一次 OCR 开始
 ↓
释放上一张图片
```

---

# 三十五、架构核心原则

整个项目遵守以下原则：

### 1. UI 与业务分离

Avalonia 不负责 OCR。

### 2. OCR 与模型分离

不能把 PP-OCRv6 写死在整个程序里面。

### 3. OCR 与 Runtime 分离

以后可以从 ONNX Runtime 切换其他推理后端。

### 4. Windows API 集中管理

所有 Win32 调用放入：

```text
MiniOCR.Windows
```

### 5. 截图默认不落盘

使用内存。

### 6. OCR 模型只加载一次

避免重复初始化。

### 7. UI 永远不能被 OCR 阻塞

OCR 使用后台线程。

### 8. 默认本地运行

不上传图片。

### 9. 第一版少功能

先把：

```text
快捷键
→
框选
→
OCR
→
复制
```

做到足够快、足够稳定。

### 10. 所有未来功能通过接口扩展

而不是不断修改 MainWindow。

---

# 三十六、最终用户体验

用户打开软件：

```text
MiniOCR
```

程序进入托盘。

用户看到网页上一段文字。

按：

```text
Ctrl + Shift + C
```

屏幕出现选择框。

拖动：

```text
┌─────────────────────┐
│ PaddleOCR is fast   │
│ and accurate.       │
└─────────────────────┘
```

松开鼠标。

后台：

```text
Capture
 ↓
PP-OCRv6
 ↓
Text
 ↓
Clipboard
```

用户直接：

```text
Ctrl + V
```

得到：

```text
PaddleOCR is fast
and accurate.
```

同时主窗口显示：

```text
[截图预览]

识别结果：
PaddleOCR is fast
and accurate.

✓ 已自动复制
识别耗时：XX ms
```

整个过程不需要用户操作第二次。

这就是第一版最重要的产品目标。