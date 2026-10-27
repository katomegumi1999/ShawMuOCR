# ShawMuOCR

<p align="center">
  <a href="README.md">中文</a> | <a href="README.en.md">English</a>
</p>

<p align="center">
  <img src="Assets/logo.png" alt="ShawMuOCR Logo" width="96" height="96" />
</p>

<p align="center">
  <strong>Lightweight · Lightning-Fast · Fully Offline · Modern Windows Desktop OCR, Screenshot Annotation, Translation & Cross-Device Collaboration</strong>
</p>

<p align="center">
  <a href="https://www.23771139.xyz/ShawMuOCR/"><img src="https://img.shields.io/badge/Website-ShawMuOCR-0078D4?logo=google-chrome&logoColor=white" alt="Official Website" /></a>
  <a href="https://dotnet.microsoft.com/download/dotnet/11.0"><img src="https://img.shields.io/badge/.NET-11.0-512BD4?logo=dotnet&logoColor=white" alt=".NET 11" /></a>
  <a href="https://avaloniaui.net/"><img src="https://img.shields.io/badge/UI-Avalonia%2012-7A43B6?logo=avalonia&logoColor=white" alt="Avalonia UI" /></a>
  <a href="https://github.com/sdcb/SimdPaddleOCR"><img src="https://img.shields.io/badge/Inference-CPU%20SIMD%20%2F%20Vulkan%20GPU-0078D4" alt="Sdcb.SimdPaddleOCR" /></a>
  <a href="https://learn.microsoft.com/dotnet/core/deploying/native-aot/"><img src="https://img.shields.io/badge/Publish-Native%20AOT%20Ready-success" alt="Native AOT" /></a>
  <a href="LICENSE"><img src="https://img.shields.io/badge/License-MIT-blue.svg" alt="License: MIT" /></a>
  <a href="https://www.microsoft.com/windows"><img src="https://img.shields.io/badge/Platform-Windows%2010%20%2F%2011%20(x64)-0078D6?logo=windows&logoColor=white" alt="Windows" /></a>
  <a href="tests/ShawMuOCR.Tests"><img src="https://img.shields.io/badge/Tests-174%20Passing-brightgreen" alt="174 Tests Passing" /></a>
</p>

---

## 📖 Introduction

**ShawMuOCR** is an open-source desktop OCR and productivity tool tailored for Windows users.

Unlike traditional OCR software that relies on heavy dependencies like Python environments, CUDA drivers, or bulky external runtimes, ShawMuOCR is built on **.NET 11 + Avalonia UI 12 + SkiaSharp + Sdcb.SimdPaddleOCR 2.0 + Rust (MuClip)**:
- **Dual-Track CPU SIMD / GPU Vulkan Engine**: Supports both AVX2 instruction set memory-level inference and high-performance GPU compute shader inference, with smart detection and manual switching in multi-GPU environments;
- **Zero-Copy Pipeline**: Screenshot pixels flow directly into neural network tensors via `Bgra32` with no intermediate format conversion or temporary bitmap buffers;
- **Fully Offline & Privacy-Safe**: Text recognition, layout organization, and clipboard management all execute locally on your machine;
- **Highly Integrated Ecosystem**: Deep integration with system-level quick screenshots, professional annotation editor, user-controlled long screenshot, high-performance screen recording ([MuRecorder](https://github.com/katomegumi1999/MuRecorder)), floating selection translation, batch OCR conversion, LAN HTTP API service, and a cross-device clipboard sync system ([MuClip](https://github.com/katomegumi1999/MuClip)) powered by pure Rust.

---

## ✨ Core Features

### 1. ⚡ CPU SIMD / GPU Dual-Track Lightning Inference & Zero-Copy Bitmap
- **Dual-Track Acceleration**:
  - **CPU Mode**: x64 AVX2 / SIMD hardware instruction set in-place color unpacking, extremely low cold start and resident memory overhead;
  - **GPU Mode**: Vulkan shader-optimized high-performance parallel inference, more efficient for long images and batch processing;
  - **Discrete/Integrated GPU Smart Detection & Manual Switching**: Natively enumerates all physical GPUs labeled `[Discrete]` / `[Integrated]`, defaults to discrete GPU priority, with manual selection available in settings;
- **Zero-Copy Pixel Direct Pass**: Screenshot pixels directly feed model input tensors with zero intermediate allocation;
- **Out-of-the-Box Models & Dynamic Scanning**: Uses sibling `models/` directory as model library, defaults to `PP-OCRv6_small`, supports `PP-OCRv6_tiny`, `PP-OCRv6_medium`, and any custom Paddle model, with one-click 🔄 refresh and 📂 open directory;
- **Incremental Publishing**: Uses `<CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>` strategy, automatically skips copying identical model files in the target directory, preventing model deletion by publish cleanup while maintaining fast incremental publishing.

### 2. ✂️ Full-Scenario Screenshot & Long Screenshot Ecosystem
- **Screenshot OCR (`Alt+Z`)**: Full-screen high-DPI 1:1 physical pixel lossless crop ➔ auto extract text ➔ auto copy to clipboard ➔ lightweight bubble notification at screen bottom;
- **Smart Table Column Detection**: Simultaneously populates clipboard with rich HTML table and clean plain text when tables are detected;
- **Quick Screenshot Copy (`Alt+C`)**: System-level clipboard experience, directly writes selected image to system clipboard after selection;
- **Advanced Screenshot Annotation Editor (`Alt+X`)**:
  - Supports rectangle, ellipse, arrow, freehand brush, highlighter, text input, numbered badges (incremental bubbles), mosaic and Gaussian blur;
  - Integrated **Long Screenshot** and **Region Recording** launch buttons on the annotation action bar to start seamlessly from the selected region;
  - Supports lossless undo/redo, pin to top (stick to screen desktop), one-click save (PNG/JPG/WEBP/BMP, custom quality and scale), one-click send for translation;
- **User-Controlled Long Screenshot (`Alt+L`)**:
  - Select any region and scroll naturally; employs **dynamic contrast band detection and zero-shift priority matching** to seamlessly stitch frames;
  - Non-intrusive capture: no simulated scroll events, no focus stealing, and zero pixel inflation when stationary;
  - **Completely user-controlled termination**: eliminates automatic timeout cutoffs; stop whenever ready via floating bar 【✓ Done】, <kbd>Enter</kbd>/<kbd>Space</kbd> key, or pressing the hotkey again;
- **Pin-to-Top Details**: 1:1 pure seamless pin-to-top, precisely restored to screenshot physical coordinates; electric blue positioning prompt on pin, zero border and zero obstruction normally;
- **Cross-DPI Strict Alignment**: Fully adapts to Windows multi-monitor, mixed DPI scaling environments, completely eliminating blur and misalignment.

### 3. 🎥 High-Performance Screen Recording ([MuRecorder](https://github.com/katomegumi1999/MuRecorder))
- **Pure Rust Hardware-Accelerated Engine**: In-house [MuRecorder](https://github.com/katomegumi1999/MuRecorder) core delivers ultra-lightweight, low-latency video and audio recording (shipped as `Libs/x64/recorder_core.dll`);
- **Full Screen & Custom Region Recording (`Alt+R`)**: Supports full desktop capture or custom selection box, with direct one-click transition from screenshot annotation box;
- **Flat Audio Device Multi-Selection & Smart Presets**:
  - Flat enumeration of all system audio input/output devices using 100% Native AOT-compliant Win32 Direct COM VTable calls;
  - Checkbox multi-selection for independent device capture (unchecking all results in silent recording);
  - Four handy presets: Mute, System Sound Only, Mic Only, and System + Mic, bidirectionally synchronized with checkboxes;
- **Sleek Floating Recording Bar**: Translucent glowing bar, real-time elapsed timer, and on-screen pen doodle annotation during recording.

### 4. 🌐 Floating Selection Translation & Multi-Source LLM Integration
- **Floating Translation Window (`Alt+T`)**: Select any screen paragraph, translation floating window docks and expands in place, OCR extraction and translation unidirectional pipeline push, zero flicker, non-obstructing;
- **Window Position Lock**: Floating window automatically maintains coordinates after adjustment; supports hover to view original text and one-click copy;
- **Deep Multi-Translation Provider Adaptation**:
  - **Aliyun Translate**: Alibaba Cloud Machine Translation API with customizable endpoints and AccessKey authentication;
  - **Volcengine Translate**: ByteDance Volcengine MT API with multi-region deployment and HMAC-SHA256 signature verification;
  - **Baidu Translate**: Full support for latest Baidu LLM translation (`aiTextTranslate`) and general text translation;
  - **Google Translate**: Integrated Google Cloud NMT v2, automatically handles HTML entity escape encoding;
  - **OpenAI Compatible API**: Compatible with GPT-4o-mini, DeepSeek, Qwen, Tongyi Qianwen, Kimi, and various compatible endpoints;
- **Monthly Quota Protection Hard Interception**: Locally tracks character and token consumption precisely, intercepts immediately when exceeded, never sends an extra cloud call.

### 5. 🚀 Batch OCR Conversion & HTTP API Service
- **Batch OCR Conversion**: Supports multi-file, multi-folder batch import of images and multi-page PDFs, supports queued parallel processing, exports TXT/JSON/Markdown or one-click copy;
- **LAN HTTP API Service**: Built-in lightweight HTTP server, supports standard RESTful POST requests, easy integration with external scripts, automation workflows, or third-party software.

### 6. 📋 Cross-Device Clipboard Sync & Collaboration ([MuClip](https://github.com/katomegumi1999/MuClip))
- **Pure Rust High-Performance Core**: cross-device discovery, pairing and transfer are provided by [MuClip](https://github.com/katomegumi1999/MuClip), shipped as `Libs/x64/muclip_core.dll`;
- **End-to-End Encrypted Transfer**: SPAKE2 password-authenticated handshake (the PIN never leaves the device) + XChaCha20-Poly1305, with a fresh forward-secret session key per connection;
- **Fast Clipboard Panel (`Alt+Q`)** & **Quick Phrases Panel (`Alt+W`)**: Summon floating quick panels anytime, search history, double-click to quickly input phrases;
- **Password-Protected Encrypted Backup & Restore (`.mubc`)**: XChaCha20-Poly1305 + PBKDF2-HMAC-SHA256 (100k rounds) packs history, phrases, conditional rules and settings into a single file, with a backup passphrase independent of the local DB key. Import supports both *merge & dedupe* and *full overwrite*, so migrating to a new machine needs no manual DB surgery;
- **Large File Transfer**: 128 KiB chunked streaming with a whole-file SHA-256 integrity check.

### 7. 🛡 Kernel-Level Global Hotkeys & Modern Design
- **RegisterHotKey Kernel Mechanism**: Hotkeys maintained directly by Windows kernel, sleep-resistant, system-removal-proof, zero CPU usage, no false reports;
- **Fluent Modern Frosted Interface**: Based on Avalonia 12 Fluent theme, supports dark/light auto-adaptation and color emoji font stack;
- **Silent Auto-Save**: Any configuration modification immediately debounced to disk in background, never steals focus, absolutely never interrupts current workflow.

---

## ⌨️ Default Global Hotkeys

| Function | Default Hotkey | Description |
| :--- | :---: | :--- |
| **Screenshot OCR** | `Alt + Z` | Full-screen selection, auto extract text, copy and show notification |
| **Screenshot Copy** | `Alt + C` | Full-screen selection, directly copy selected image to clipboard |
| **Screenshot Annotation** | `Alt + X` | Open annotation editor, doodle, save, pin to desktop, or launch long screenshot / screen recording |
| **Long Screenshot** | `Alt + L` | Select region, scroll naturally to stitch, finish anytime via floating bar, Enter, or hotkey |
| **Screen Recording** | `Alt + R` | Summon desktop floating bar or start recording (MuRecorder hardware-accelerated core) |
| **Selection Translation** | `Alt + T` | Select screen paragraph, floating window translates in place |
| **OCR Popup** | `Alt + V` | Select screen area, show OCR results in floating window without auto-copy |
| **Quick Note** | `Alt + N` | Floating quick note window for spontaneous memo and inspiration |
| **MuClip Clipboard** | `Alt + Q` | Summon floating clipboard panel, real-time history search and paste |
| **Quick Phrases Panel** | `Alt + W` | Summon MuClip phrases panel, click or press key to paste |

> 💡 **Tip**: All hotkeys can be freely modified or cleared in [System Settings] (press `Backspace` to clear).

---

## 🏗 Project Architecture

This project follows layered decoupling and NativeAOT safety standards:

```
ShawMuOCR
├── ShawMuOCR (Main Application Layer / Avalonia 12 MVVM)
│   ├── ViewModels/          # MainViewModel, SettingsViewModel, BatchOcrViewModel, HttpApiViewModel, etc.
│   ├── Views/               # MainWindow, SettingsWindow, ScreenshotEditorWindow, Pages, etc.
│   ├── OCR/                 # OcrEngineFactory, OcrModelProvider model parsing and management
│   ├── Assets/              # Icons, logos, vector resources and style dictionaries
│   └── Libs/x64/            # muclip_core.dll & recorder_core.dll authoritative native dependencies
│
├── src/ShawMuOCR.Core (Core Domain Models & Universal Service Abstractions)
│   ├── OCR/                 # IOcrEngine contract, PaddleOcrV6Engine inference, geometric grid topological sorting
│   ├── Translation/         # ITranslationService, Aliyun/Volcengine/Baidu/Google/OpenAI providers, quota management
│   ├── Capture/             # Screenshot contract, ScreenRect physical bounds, VerticalShiftEstimator
│   ├── Hotkey/              # Cross-platform hotkey gestures and parsing definitions
│   └── Settings/            # AppSettings configuration model and JsonSettingsService
│
├── src/ShawMuOCR.Windows (Windows Native Low-Level Integration)
│   ├── ScreenCapture/       # Win32 GDI+ native screenshot, LongScreenshotCapturer adaptive stitcher
│   ├── Hotkey/              # Win32 RegisterHotKey kernel-level global hotkey and message loop thread
│   └── Clipboard/           # Managed and Win32 native clipboard communication services
│
├── src/MuRecorder (High-Performance Hardware-Accelerated Screen Recording System)
│   ├── Interop/             # Rust native FFI bindings (recorder_core.dll)
│   ├── Audio/               # Pure Win32 Direct COM VTable audio device enumeration and selection models
│   └── ViewModels/ & Views/ # Recording page, floating recording controls, and screen doodle overlay
│
├── src/MuClip (LAN Cross-Device Clipboard & File Sync System)
│   ├── Interop/             # Rust native FFI bindings (MuClipNative, MuClipSync, BinarySpanReader)
│   ├── Services/            # Clipboard monitoring, history cache, single-stream encryption pairing and chunked file transfer
│   └── ViewModels/ & Views/ # Clipboard views and quick summon panels
│
├── docs/ (Architecture & Developer Guides)
│   ├── index.md             # Documentation index overview
│   └── agents.md            # AI collaborative development & code maintenance guide (must read)
│
└── tests/ShawMuOCR.Tests (Automated Unit Test Suite)
    └── Comprehensive tests covering OCR inference, GPU/CPU engines, long screenshot shifting, audio devices, translation, and MuClip handshake
```

---

## 🛠 Build & Run Guide

### 1. Environment Preparation
- OS: **Windows 10 / Windows 11 (x64)**
- Development Environment: [.NET 11.0 SDK](https://dotnet.microsoft.com/download/dotnet/11.0) or higher
- Recommended IDE: Visual Studio 2022 / 2026, JetBrains Rider, or VS Code (with C# Dev Kit)

### 2. Get Source Code & Build
```bash
# Clone repository
git clone https://github.com/katomegumi1999/ShawMuOCR.git
cd ShawMuOCR

# Restore dependencies and build
dotnet build -c Release

# Run application
dotnet run -c Release
```

### 3. Run Automated Unit Tests
```bash
# Fast-tier test run during development: skips slow inference/memory tests (~4s)
dotnet test --filter "Category!=Slow"

# Targeted tests for affected modules
dotnet test --filter "FullyQualifiedName~LongScreenshot|FullyQualifiedName~AudioDeviceEnumerator"

# Full suite run before commit/release (~2m)
dotnet test
```

### 4. Native AOT Standalone Publishing (Portable Green Version)
The project natively supports Native AOT. Publishing includes no redundant symbol files and does not automatically bundle large models:

```bash
# Publish highly optimized Windows x64 Native AOT green single-directory/single-file version
dotnet publish ShawMuOCR.csproj -c Release -r win-x64 --self-contained
```
Publish output is located at `bin/Release/net11.0-windows/win-x64/publish/`. Place required models in the sibling `models/` directory, then double-click `ShawMuOCR.exe` for millisecond lightning-fast startup!

---

## 🧩 Model Download & Management

The program automatically scans the `models/` folder in the same directory at runtime:

1. **Default Model Structure**:
   - `models/PP-OCRv6_small/` (default built-in recommendation): Balances accuracy and lightning-fast response;
   - `models/PP-OCRv6_tiny/`: Ultra-fast lightweight model;
   - `models/PP-OCRv6_medium/`: High-precision large model;
   - Dictionary file (`ppocrv6_dict.txt`) and direction classification model (`ch_ppocr_mobile_v2.0_cls.onnx`) can be placed in `models/` root directory for reuse by sub-models;
2. **External Custom Paddle Models**:
   - Directly create any named subfolder under `models/`, place corresponding `*det*.onnx` and `*rec*.onnx` files;
   - Open ShawMuOCR [Settings] ➔ click 🔄 refresh button next to model selection, switch directly in the dropdown list!

---

## 🤝 Contribution & Developer Documentation

- Developer and AI collaboration guidelines: **[docs/agents.md](docs/agents.md)**
- Issues and Pull Requests welcome! Please ensure all test gates pass before submitting.

---

## 💖 Acknowledgments & Open Source Ecosystem

ShawMuOCR's creation would not be possible without the strong foundation of the open source community. Heartfelt thanks to the following excellent projects:

- [Avalonia UI](https://github.com/AvaloniaUI/Avalonia) - Excellent cross-platform XAML .NET UI framework
- [Sdcb.SimdPaddleOCR](https://github.com/sdcb/SimdPaddleOCR) - Pure C# SIMD / Vulkan GPU optimized PP-OCR inference library
- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) - Baidu's excellent open source PaddlePaddle deep learning OCR toolkit
- [SkiaSharp](https://github.com/mono/SkiaSharp) - Powerful 2D vector graphics rendering cross-platform engine
- [PdfPig](https://github.com/UglyToad/PdfPig) - Pure managed high-performance PDF document parsing tool
- [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) - Simple and efficient modern MVVM toolkit
- [MuClip](https://github.com/katomegumi1999/MuClip) - In-house pure-Rust cross-device clipboard sync & encrypted file transfer engine (open sourced)
- [MuRecorder](https://github.com/katomegumi1999/MuRecorder) - In-house pure-Rust high-performance hardware-accelerated screen recording engine (open sourced)

---

## 👤 Author & Community

- **Developer**: Mu Xun Yu (沐洵羽)
- **Official Website**: [https://www.23771139.xyz/ShawMuOCR/](https://www.23771139.xyz/ShawMuOCR/)
- **Bilibili**: [@沐洵羽](https://space.bilibili.com/192158726)
- **GitHub Repository**: [katomegumi1999/ShawMuOCR](https://github.com/katomegumi1999/ShawMuOCR)
- Feel free to give us a ⭐️ **Star** on GitHub. Your support is the greatest motivation for continuous maintenance and iteration!

---

## 📜 License

This project is open-sourced under the [MIT License](LICENSE).
You may freely use, modify, distribute and commercialize this software, but please retain the original author's copyright information and open source declarations.
