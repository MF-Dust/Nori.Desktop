# PurismCore 原生运行时

当前随包使用 **PurismCore v1.1.0 / v6 ABI**，不再包含官方 Live2D Cubism Core。保留 `Live2DCubismCore` 动态库文件名仅为兼容现有 C# P/Invoke，不代表这些文件由 Live2D 提供。上层 C# SDK、OpenGL 渲染器、模型及图片资产的许可不随此替换改变。

## 固定来源

- 上游：<https://github.com/SakuraMotion/PurismCore>
- 标签：<https://github.com/SakuraMotion/PurismCore/releases/tag/v1.1.0>
- 标签提交：`1069334965522df5d0b791e97f01e26b19c45456`
- 归档：<https://github.com/SakuraMotion/PurismCore/releases/download/v1.1.0/PurismCore-v1.1.0.zip>
- 归档 SHA-256：`ae1b32af28a06b27f47cda61f8b5f5d4caa047dc856cdde1d6d839b850b6a70c`（与 GitHub release API 的 asset digest 一致）
- 许可：[MIT 全文](PurismCore.LICENSE.txt)，来自上述归档的 `LICENSE`，并与固定提交的 [LICENSE](https://github.com/SakuraMotion/PurismCore/blob/1069334965522df5d0b791e97f01e26b19c45456/LICENSE) 逐字节核对。版权为 `Copyright (c) 2026 Sakura Motion Project. <https://sakura2d.org/>`。

所有二进制均直接取自归档的 `PurismCore-v1.1.0/dll/`，只改名、未修改内容，**不是 `dll-v5/`**：

| RID | 归档 `dll/` 下路径 | 仓库 `native/` 下路径 | SHA-256 |
| --- | --- | --- | --- |
| win-x64 | `windows/x86_64/PurismCore.dll` | `win-x64/Live2DCubismCore.dll` | `261c484adfea81cf59eb2aaa051b55be3a8576567b7285c770c194a79df4f6af` |
| linux-x64 | `linux/x86_64/libPurismCore.so` | `linux-x64/libLive2DCubismCore.so` | `237b36498e85d8e5fe0278418af93b794a063e2a96dc17409a3058eaf4ec90e4` |
| linux-arm64 | `linux/arm64/libPurismCore.so` | `linux-arm64/libLive2DCubismCore.so` | `d4cedff155f441cb1bb35d538b6d0db6c3472484d3c3282d9a5f963e87309497` |
| osx-x64、osx-arm64 | `macos/universal/libPurismCore.dylib` | `macos/libLive2DCubismCore.dylib` | `8f9336f5b62e8ceb575c965cbd26dcd63743c4068cf1fc1905d2ee3af12110db` |

## 构建与验证

`Nori.Desktop.csproj` 按显式 RID 或无 RID 构建的 SDK 宿主架构复制一份库，并把 `PurismCore.LICENSE.txt` 放在同目录。`publish.bat` / `publish.sh` 沿用此复制结果；发布结构检查拒绝缺少该许可的包。Linux 库为 ELF64 x86-64 / AArch64，要求 GLIBC ≥ 2.27；macOS 库为 x86_64 + arm64 universal。

`Nori.Live2D.NativeModel.Update()` 必须先 reset、再 update，否则 PurismCore 会在渲染器读取前清除本帧遮罩变化标记。保留现有上层行为管线，没有后端选择配置。

在 `app/desktop` 执行 `dotnet test Nori.Desktop.Tests -c Release -m:1`，无需外部模型资源。发布脚本调用 `scripts/validate-publish-structure.mjs` 检查发布结构。

以下为 PurismCore 原生库替换阶段的验证（Windows 主机）；后续自有模型层的当前职责与验证见 [Nori.Live2D](../Nori.Live2D/README.md)：

- Desktop Release 测试 542 项通过，启用真实模型资源，0 跳过；新增回归检查固定的 PurismCore 版本、两模型连续三帧的可见性、遮罩变化标记、有限顶点、参数形变和有效且唯一的 renderOrder。
- 五个 RID 均通过现有发布脚本的 framework-dependent 发布及结构检查；逐个确认只有对应原生库、SHA-256 与上表一致、MIT 全文随库复制。Windows ZIP 解压检查通过。
- 仓库外临时 Avalonia GL harness 复用 `ModelPreparation`、`LAppDelegateOpenGL`、`AvaloniaGlApi` 和现有投影，对两模型分别执行普通/高精度遮罩、720×480 / 1920×1080（1080p 使用离屏目标）检查；每组合独立进程连续 60 帧，无 GL 错误，首末帧均有非空模型和透明边界。此项不等同于完整 PetWindow 交互或长时间运行验证；私有模型与截图未加入仓库。

Linux/macOS 的交叉发布和静态检查不代表原生运行通过。历史截图及性能数据见 [历史评估](../../../docs/purismcore-evaluation.md)，不据此声称性能提升或任意模型完全兼容。
