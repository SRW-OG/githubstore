# Windows EXE 安装包

使用 Inno Setup 6 编译 x64 安装程序。默认按当前 Windows 用户安装到
`%LOCALAPPDATA%\Programs\GitHubStore`，无需管理员权限，可在向导中更改目录。

安装包包含 .NET 8 和 Windows App SDK 运行库，不要求目标电脑安装 .NET SDK。
开始菜单会创建 GitHubStore 入口，桌面快捷方式为可选项；Windows“已安装的应用”中提供卸载入口。
卸载只删除客户端安装文件与快捷方式，保留 `%LOCALAPPDATA%\GitHubStore` 中的用户数据、
下载缓存和用户通过客户端安装的软件。手动启动器记录 `manual-launcher.json`、启动次数及旧配置 `launcher-customizations.json` 同属用户数据，卸载客户端不会清空；移除启动器入口与卸载客户端/第三方软件是不同操作。

## 当前产物状态（2026-11-02）

本次交付版本为 **v0.1.22**，已生成 [GitHubStore-Setup-0.1.22-win-x64.exe](../dist/GitHubStore-Setup-0.1.22-win-x64.exe) 与 [SHA-256 文件](../dist/GitHubStore-Setup-0.1.22-win-x64.exe.sha256)，包含当前 128×128 方卡、只编辑路径/参数、快捷方式解析及路径/参数原子保存。安装包为 **50,656,049 字节**，文件版本 `0.1.22.0`，SHA-256 为 `571c97b79340d9cbf00105631c1c61c62ead4c1c15ff8081c303203214d05a9f`；构建元数据见 [installer-build.json](../dist/installer-build.json)。

Release x64 测试项目构建 0 警告、0 错误，完整回归 **1908 通过、0 失败、0 跳过**，报告 [release-0.1.22-Release-x64.json](../artifacts/test-results/release-0.1.22-Release-x64.json)；自包含 `dotnet publish` 与 Inno Setup 编译成功。发布输出使用隔离数据目录启动，PID `18876`，窗口标题“GitHubStore · 开源应用目录”，句柄 `395604`，`Responding=true`，请求正常关闭后退出码 0，报告 [release-0.1.22-smoke.json](../artifacts/test-results/release-0.1.22-smoke.json)。该结果不代替安装器安装/卸载全流程或最新属性界面点击验收；本节也不提前宣称远端 CI 或正式 Release 已成功。

历史本地 0.1.21 包包含早期手动添加、计数排序与完整属性编辑，不包含后续方卡及精简属性/解析改动；其 2026-11-01 产物为 50,631,969 字节，文件版本 `0.1.21.0`，SHA-256 为 `25e716577d6497949e92c8d8f5524b88001a0c7617a320dee9d635b7497b89ad`，对应当时 1894 项测试。更多版本区别见 [近期更新](../docs/CHANGELOG.md#当前产物与验证边界)，详细证据范围见 [开发与验证](../DEVELOPMENT.md)。

## 重新打包

先安装从 [Inno Setup 官网](https://jrsoftware.org/isdl.php) 下载的编译器，再在项目根目录运行：

```powershell
.\scripts\build-installer.ps1 -Version 0.1.22 -CompilerPath 'C:\Path\To\ISCC.exe'
```

上述命令展示本次 v0.1.22 产物的打包方式，该版本现已生成；再次打包应按实际发布计划设置新版本号，避免覆盖已有产物。脚本执行 Release 自包含发布，每次使用独立暂存目录，然后生成：

- `dist\GitHubStore-Setup-0.1.22-win-x64.exe`
- 对应的 `.exe.sha256` 校验文件
- `dist\installer-build.json` 构建元数据

为避免覆盖已交付文件，同一版本的安装包已存在时脚本会停止；新发布应使用新版本号。
打包定义见 [GitHubStore.iss](GitHubStore.iss)，构建脚本见 [build-installer.ps1](../scripts/build-installer.ps1)。
当前未配置代码签名证书，因此生成的 GitHubStore 安装包未签名。
