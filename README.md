# FastLink 直连助手

- 自动检测 `FastLink Tunnel`。
- Fastlink 启用后自动打开直连模式，关闭后自动清理。
- 支持精确域名、域名后缀及 `*` / `?` 通配规则。
- 通过 Windows 登录计划任务自启动；平时驻留系统托盘。

规则示例：

- `portal.example.com`：只匹配这个域名。
- `.example.com`：匹配 `example.com` 及其所有子域名。
- `*.example.com`：匹配所有子域名。
- `api-?.example.com`：`?` 匹配一个字符。
- `http://10.0.0.25/app/`：自动提取 IPv4 地址并直连，路径会被忽略。

首次运行会申请管理员权限，因为添加直连路由和 DNS 规则需要管理员权限。
