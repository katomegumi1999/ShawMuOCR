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