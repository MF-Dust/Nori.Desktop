# 更新记录

## [Unreleased]

### Changed

- 浏览器 DOM 自动化拆分为独立插件 [Nori.Plugins.Browser](https://github.com/MF-Dust/Nori.Plugins.Browser)（`io.nori.browser`）：主程序移除 Microsoft.Playwright 依赖、`automation_browser_*` 桥接命令、`automation_browser_enabled` 配置与设置页浏览器控件；历史浏览器审计记录仍可读取。
- 宿主 Plugin API 升至 2.3：`IPluginActionContribution.Permission` 可声明 safe/confirm/dangerous，插件动作工具按级别逐次请求授权；对应 `Nori.PluginSDK` 2.3.0 已同步页面与权限合同并移除 WebView 合同。

### Fixed

- 修复公网工具请求在发送前被全局系统代理探测误拦截的问题：默认公网客户端明确禁用代理，连接时解析、校验并连接同一组 IP，保留 TLS 校验与手动重定向策略。
- 保留显式 `allow_public_system_proxy` 配置与模型客户端行为；显式代理模式不保证代理端 DNS 解析的私网隔离。
- 涉及 `NoriHttpClients`、`UrlAccessPolicy`、工具调用与启动配置，新增代理路由和 DNS 策略回归测试。
