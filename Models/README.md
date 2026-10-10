# 模型存放目录 (Models Directory)

小沐 OCR 采用**纯代码 Native AOT 与模型解耦架构**：
- 主程序 `ShawMuOCR.exe` 为纯机器码，体积已缩减至 ~35MB；
- 所有 OCR 识别与检测权重均存放在本 `models/` 目录下，后续更新软件版本只需替换 `ShawMuOCR.exe`，无需重复下载或覆盖庞大的模型。

---

## 默认目录结构

- `models/ch_ppocr_mobile_v2.0_cls.onnx`：**公共共享方向分类模型**（所有模型通用复用，省去每个模型重复存放的冗余体积）
- `models/PP-OCRv6_small/`：官方 PP-OCRv6 高精度模型（包含检测 det、识别 rec、字典 dict）
- `models/PP-OCRv6_tiny/`：官方 PP-OCRv6 极速轻量模型（包含检测 det、识别 rec、字典 dict）

---

## 自定义扩展 PaddleOCR 模型

如需使用自定义训练或特定语言的 Paddle 模型：

1. **新建子文件夹**：例如在此目录下新建 `my_model/`；
2. **放置 ONNX 模型与字典文件**：
   - 文字检测模型：命名中包含 `det`（如 `*det*.onnx`）
   - 文字识别模型：命名中包含 `rec`（如 `*rec*.onnx`）
   - （选填）方向分类模型：命名中包含 `cls`（如 `*cls*.onnx`，缺省将自动复用通用分类模型）
   - （选填）自定义字典文件：命名为 `dict.txt` 或 `*.txt`（缺省将自动复用通用字典）
3. **选用识别**：
   - 打开软件【系统设置】➔【OCR 模型设置】➔ 勾选“开启扫描”，即可在下拉列表中直接选用您的外部模型。
