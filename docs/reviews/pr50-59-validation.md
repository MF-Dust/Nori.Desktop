# PR 50–59 接续修复与验证

日期：2026-09-18。接续分支 `local/pr50-59`，起点 `0312a80d2b76b511f0c72cc2d2a5831b223346cf`。
交付入口：[PR #63](https://github.com/MF-Dust/Nori.Desktop/pull/63)。本记录补充原交接，不能把历史 WIP 或新增的未执行 C# 测试视作验收通过。

## 实现与提交

| 问题 | 提交 |
| --- | --- |
| 麦克风状态机 | `89a5fc5` |
| 通知授权 | `0f40bcc` |
| 首次运行导入 | `c7faf13` |
| MiniMax WAV | `81f3706` |
| WASAPI 生命周期 | `749a2f6` |
| 插件卡片隔离 | `24f94d8` |
| 原生首页与视觉验证 | `92ba012` |
| 播放回调并发 | `9d55dd7` |

- 麦克风补全显式 Completed 状态与唯一设备释放者；保留 VoiceService 依赖的待提取会话语义。23 个 Fact 包含 12 个新增用例，移除固定延时，覆盖双 Start、Open 失败、Stop/Dispose 竞态、取消等待与空 WAV。
- 通知 Show/Hide 失败不再阻断待决授权完成；Open 忽略失效通知。Allow/Deny 的 UI 线程与不抢焦点契约保留。
- 首启窗口关闭取消独立生命周期，停止等待文件选择器及后续导入；保留 Init Hide 时停止计时器的现有实现。
- 播放事件离开设备状态锁，电平按订阅者分别检查当前 owner，防止回调内启动下一段后继续分发旧电平。WASAPI 释放枚举器/端点并将设备失效转换为领域异常。
- MiniMax 非流式请求使用 WAV，补请求格式、实际 WAV 解码和显式 MIME 契约测试。其余 OpenAI/GPT-SoVITS WAV 请求沿用交接修复。Windows native 不自动创建非 WAV WebView 回退。
- 原生与 Vue 卡片只允许脚本 sandbox，核验来源窗口并绑定宿主插件身份。原生包装页增加独立随机凭据、导航限制和请求生命周期；公开插件资源仅对 opaque origin 开放无凭据 GET/HEAD CORS，主资产与媒体端点不开放。
- 首页四个快捷入口采用原生 Button，支持键盘操作。新增原生结构检查，Windows CI 截取中英文 720×480 与 1920×1080 首页并上传。

## 已实际执行

所有项目命令工作目录均为 `app/desktop`。

| 命令 | 结果 |
| --- | --- |
| `pnpm exec vitest run tests/chat/plugin-widgets.test.ts` | 24/24 通过 |
| `pnpm lint` | 通过 |
| `pnpm exec vue-tsc --noEmit` | 通过 |
| `pnpm build` | 通过，Vite 编译 3107 modules |
| `pnpm test` | 32 files / 176 tests 通过 |
| `pnpm coverage` | 通过全部阈值 |
| `git diff --check` | 通过 |
| `pnpm check:todo` | 本地已物化源码扫描通过，完整仓库由 CI 再扫 |
| `dotnet --info`、定向 `dotnet test` | 无 .NET SDK，退出 127，未执行 C# 编译/测试 |

覆盖率：statements 56.49%、branches 51.03%、functions 50.81%、lines 58.11%。初次全套验证因稀疏检出缺少图片/后端契约源文件失败，补齐指定提交的原始文件后以上完整前端验证通过；没有修改或跳过失败断言。

## Git 与范围核对

本地通过 GitHub 指定提交的原始树与文件建立稀疏检出，重建基准提交并校验其 SHA。远程提交按上表顺序单父提交接续基准，使用非强制 ref 更新，没有合并其他分支。PR 的 base SHA 仍为交接给定的 `cc96fe1d102876441d075e16116fe8937abf1e44`。

全程未读取 PR #60 页面、diff、head ref 或实现，未 fetch/merge/cherry-pick #60。没有修改 `components.d.ts`。本次没有进行新的架构迁移：Main / Init / FirstRun 保持原生，独立音频宿主仅服务现有显式兼容选择及尚未原生化平台，HTML 仅保留公开插件卡契约。

## 仍由 CI / 实机确认

[首轮 Actions](https://github.com/MF-Dust/Nori.Desktop/actions/runs/35358352816) 已触发。完整后端 Release 构建、Core/Desktop/PluginRuntime/Launcher 测试、覆盖率、Windows 视觉与发布冒烟以最终提交的 Actions 结果为准，未在本地执行。

真实 WASAPI 设备拔出、三平台音频/麦克风、Linux/macOS 隐藏音频宿主后台行为及原生 WebView 卡片需要实机验收。同步设备 Open 没有取消参数，Stop/Dispose 可请求取消，但资源最终释放需等 Open 返回。opaque sandbox 不允许卡片直接使用 localStorage，持久化应通过所属插件动作。

原生视觉截图由新增 CI 步骤生成；本地没有可供查看的截图，因此不宣称已完成视觉验收。保留 Draft 状态直到后端和实机门禁确认。
