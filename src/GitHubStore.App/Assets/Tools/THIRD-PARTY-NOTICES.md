# 内置组件与第三方声明

## aria2 1.37.0

应用内嵌未修改的官方 Windows x64、x86 ZIP；首次使用时在应用数据根的 `tools/aria2/1.37.0/<architecture>/` 离线准备组件。两个包的 ZIP 与 EXE SHA-256 固定在同目录 `tools.manifest.json` 中。ZIP 摘要与 Microsoft winget-pkgs 公布的 1.37.0 安装清单一致；运行前核验归档和 EXE，旧用户设置中的路径不会成为程序执行源。

- 官方发行版：https://github.com/aria2/aria2/releases/tag/release-1.37.0
- 对应源码：https://github.com/aria2/aria2/releases/download/release-1.37.0/aria2-1.37.0.tar.xz
- 构建说明：https://github.com/aria2/aria2/tree/release-1.37.0
- 摘要依据：https://github.com/microsoft/winget-pkgs/blob/master/manifests/a/aria2/aria2/1.37.0/aria2.aria2.installer.yaml
- 原始 GPLv2 许可证、OpenSSL 许可证及 Windows 构建说明随输出保留在 `licenses/aria2/`。

发行 ZIP 的原始 README、COPYING、LICENSE.OpenSSL 等文件也保留在嵌入归档中。ARM64 当前使用官方 x86 兼容包，不声称是原生 ARM64 aria2。

## Windows Package Manager (winget)

自动使用当前用户已注册的 Microsoft Windows App Installer。此应用不复制系统受保护目录中的组件、不覆盖系统 winget、不要求用户选择 exe，也不随包冒充提供 App Installer 的完整系统依赖。系统缺少或禁止该 Windows 组件时会如实报告不可用。

官方项目：https://github.com/microsoft/winget-cli

## Xget

Xget 以应用内置的 URL 重写和回退逻辑接入，没有本地可执行文件。仅在用户启用 Xget 时，把支持的 GitHub 下载交给配置内置的 HTTPS 服务；默认关闭。第三方服务的可用性由服务端决定；失败按应用策略回退原始地址。
