# 更新记录

## [Unreleased]

### Fixed

- 修复公网工具请求在发送前被全局系统代理探测误拦截的问题：默认公网客户端明确禁用代理，连接时解析、校验并连接同一组 IP，保留 TLS 校验与手动重定向策略。
- 保留显式 `allow_public_system_proxy` 配置与模型客户端行为；显式代理模式不保证代理端 DNS 解析的私网隔离。
- 涉及 `NoriHttpClients`、`UrlAccessPolicy`、工具调用与启动配置，新增代理路由和 DNS 策略回归测试。
