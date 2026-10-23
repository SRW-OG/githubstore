# Windows EXE 安装包

使用 Inno Setup 6 编译 x64 安装程序。默认按当前 Windows 用户安装到
`%LOCALAPPDATA%\Programs\GitHubStore`，无需管理员权限，可在向导中更改目录。

安装包包含 .NET 8 和 Windows App SDK 运行库，不要求目标电脑安装 .NET SDK。
开始菜单会创建 GitHubStore 入口，桌面快捷方式为可选项；Windows“已安装的应用”中提供卸载入口。
卸载只删除客户端安装文件与快捷方式，保留 `%LOCALAPPDATA%\GitHubStore` 中的用户数据、
下载缓存和用户通过客户端安装的软件。

## 重新打包

先安装从 [Inno Setup 官网](https://jrsoftware.org/isdl.php) 下载的编译器，再在项目根目录运行：

```powershell
.\scripts\build-installer.ps1 -Version 0.1.0 -CompilerPath 'C:\Path\To\ISCC.exe'
```

脚本执行 Release 自包含发布，每次使用独立暂存目录，然后生成：

- `dist\GitHubStore-Setup-0.1.0-win-x64.exe`
- 对应的 `.exe.sha256` 校验文件
- `dist\installer-build.json` 构建元数据

为避免覆盖已交付文件，同一版本的安装包已存在时脚本会停止；新发布应使用新版本号。
打包定义见 [GitHubStore.iss](GitHubStore.iss)，构建脚本见 [build-installer.ps1](../scripts/build-installer.ps1)。
当前未配置代码签名证书，因此生成的 GitHubStore 安装包未签名。
