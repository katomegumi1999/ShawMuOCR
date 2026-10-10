# AI 协同开发指南 (AI Agents Guide)

本项目的完整架构说明、设计模式、踩坑避雷规则与维护指南已归档至：

👉 **[docs/agents.md](docs/agents.md)**
👉 **[docs/index.md](docs/index.md)**

请在修改代码前查阅上述文档，重点关注：
1. **模型发布规则**：`Models/` 目录严禁在 `dotnet publish` 时打包分发（设置已锁定为 `<CopyToPublishDirectory>Never</CopyToPublishDirectory>`）。
2. **Native AOT 裁剪安全**：禁止无约束反射，P/Invoke 一律使用 `[LibraryImport]`。
3. **Avalonia ComboBox 选型避坑**：避免清空集合导致 `SelectedItem` 置 null 写崩设置。
4. **单源原生库**：所有原生 DLL 统一保留在 `Libs/x64/muclip_core.dll`。
5. **单元测试门禁**：改动前后必须保证 `dotnet test` 全量 174 项测试 100% 通过。
