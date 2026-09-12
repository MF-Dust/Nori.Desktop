# Codex Cloud 环境

本文件记录 `MF-Dust/Nori.Desktop` 在 Codex Cloud 中的推荐环境配置。项目的具体开发、构建和测试约束继续以仓库根目录的 `AGENTS.md` 为准。

## 环境设置

在 Codex 的环境设置中选择本仓库，并使用默认 `universal` 镜像。

包版本：

- Node.js：在 Codex Cloud 面板选择 `22`
- Codex `universal` 当前内置 Node 版本选择最高为 `22`；setup script 会通过镜像自带的 `mise` 额外安装并激活 Nori.Desktop 要求的 Node.js `24`
- 其他 Codex 内置语言运行时保持默认
- .NET 10 SDK 由 setup script 检查并在缺失时安装到 `~/.dotnet`
- pnpm 由 setup script 固定到 `11.x`

Setup script：

```bash
bash app/desktop/scripts/codex-cloud-setup.sh
```

Maintenance script：

```bash
bash app/desktop/scripts/codex-cloud-maintenance.sh
```

建议配置的环境变量：

```text
DOTNET_CLI_TELEMETRY_OPTOUT=1
DOTNET_NOLOGO=1
NUGET_XMLDOC_MODE=skip
CI=1
```

普通构建、测试和代码修改不需要额外 secret。涉及发布、Sentry 上传或其他外部服务时，再为对应任务单独配置所需凭据，避免把长期凭据混入通用开发环境。

## 网络访问

Setup script 需要联网安装系统依赖、Node.js 24、.NET SDK、pnpm、npm 依赖和 NuGet 依赖。Codex Cloud 的 setup 阶段本身允许访问互联网。

Agent 阶段推荐使用“常用依赖项”域名允许列表，并只开放 `GET`、`HEAD`、`OPTIONS`。这样在修改依赖版本或新增依赖时仍可读取 npm、NuGet 与源码托管资源，同时避免给普通代码任务开放不必要的写网络请求。

如果任务只修改现有代码，且 setup 已完整还原依赖，也可以关闭 Agent 阶段互联网访问。

## Setup script 做了什么

`app/desktop/scripts/codex-cloud-setup.sh` 会：

1. 在 apt 系 Linux 环境安装 `libgtk-3-0` 与 `libwebkit2gtk-4.1-0`，与 Linux CI 保持一致。
2. 检测当前 Node.js；当 Codex 面板提供的 Node.js 22 低于项目要求时，通过 `mise` 安装并激活 Node.js 24。
3. 固定 pnpm 11。
4. 验证 .NET 10 SDK；缺失时安装到 `~/.dotnet`，并将路径持久化到 `~/.bashrc`。
5. 在 `app/desktop` 执行 `pnpm install --frozen-lockfile`。
6. 执行 `dotnet restore Nori.slnx`。
7. 额外预还原 `linux-x64` 发布依赖，避免 Agent 阶段发布时再次联网还原。

Maintenance script 用于恢复缓存容器后的依赖同步。它会重新定位由 `mise` 安装的 Node.js 24 和 `~/.dotnet`，再同步 pnpm 与 NuGet 依赖。

## 环境验证

环境创建后，可以让 Codex 在 `app/desktop` 中执行以下门禁：

```bash
node --version
pnpm --version
dotnet --version

pnpm check:todo
pnpm lint
pnpm build
pnpm test

dotnet build Nori.slnx --configuration Release
dotnet test Nori.slnx --configuration Release --no-build --no-restore -m:1
```

其中 `node --version` 应显示 `v24.x`。Codex 面板选择 Node.js 22 只决定基础环境版本，真正运行 Nori.Desktop 构建时会使用 setup script 准备的 Node.js 24。

覆盖率任务按需运行：

```bash
pnpm coverage
pnpm coverage:dotnet --no-build --no-restore
```

Windows 发布、Windows WebView2 和 Windows 启动冒烟测试无法在 Codex Cloud 的 Linux 容器中原生复现，继续以 GitHub Actions 的 Windows job 为最终平台门禁。

## 官方参考

- Codex Universal 镜像：https://github.com/openai/codex-universal
- Codex Cloud 环境：https://learn.chatgpt.com/docs/environments/cloud-environment
- Codex Cloud：https://learn.chatgpt.com/docs/cloud
