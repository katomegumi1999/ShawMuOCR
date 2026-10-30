# AI 协同开发指南 (AI Agents Guide)

本项目的完整架构说明、设计模式、踩坑避雷规则与维护指南已归档至：

👉 **[docs/agents.md](docs/agents.md)**
👉 **[docs/index.md](docs/index.md)**

请在修改代码前查阅上述文档，重点关注：
1. **模型发布规则**：`Models/` 目录设置为 `<CopyToPublishDirectory>PreserveNewest</CopyToPublishDirectory>`（仅在新文件或文件更新时增量复制，且避免发布时误删目标目录的模型）。
2. **Native AOT 裁剪安全**：禁止无约束反射，P/Invoke 一律使用 `[LibraryImport]`。
3. **Avalonia ComboBox 选型避坑**：避免清空集合导致 `SelectedItem` 置 null 写崩设置。
4. **单源原生库**：所有原生 DLL 统一保留在 `Libs/x64/`（`muclip_core.dll`、`murecorder_core.dll`），禁止在项目根目录冗余复制；`murecorder_core.dll` 只能由 `C:\Rust\MuRecorder\scripts\build.ps1` 生成并复制（该脚本必须用绝对路径 `-ShawMuOCRDir`，复制后校验 SHA256）。
5. **启动顺序红线**：主窗口 `Show()` 之前只允许毫秒级操作。OCR 引擎实例化、MuClip 服务、GPU 枚举、页面装配、编辑器预热一律首帧后在后台/分帧完成（详见 [docs/agents.md](docs/agents.md) 第 3.11 节）。守卫测试 `OcrEngineLazyInitTests` 不允许变红。
6. **测试门禁（分层执行，不要每次全量）**：
   - 改动期间：`dotnet test --filter "Category!=Slow"`（约 4 秒 / 315 项），或按 [docs/agents.md](docs/agents.md) 第 4.2 节「改动区域 → 应跑测试」映射表只跑受影响的类；
   - 发布 / 提交前：`dotnet test` 全量（约 2 分钟 / 351 项，含真实 OCR 推理与内存池压力测试）；
   - 新增测试单次超过 1 秒，必须打 `[Trait("Category", "Slow")]`，否则快档会逐渐退化成全量。
