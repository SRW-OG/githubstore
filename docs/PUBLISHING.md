# 上传到 GitHub 与发布

[项目说明](../README.md) · [开发与验证](../DEVELOPMENT.md) · [安装包构建](../packaging/README.md) · [目录库维护](../catalog/README.md)

源码、测试、独立软件库、构建脚本和内嵌依赖应提交；`artifacts`、`dist`、`bin`、`obj`、测试临时数据、真实配置及凭据不提交。规则见 [.gitignore](../.gitignore)。

## 首次推送

先在 GitHub 创建一个空仓库。为避免首次合并冲突，创建时不要额外生成 README、gitignore 或许可证文件。
在本项目根目录运行以下命令，使用你自己的真实身份与仓库 URL：

```powershell
# 仅在当前 Git 环境尚未配置身份时设置；不要照抄占位符。
git config user.name "你的提交姓名"
git config user.email "你的提交邮箱或 GitHub noreply 邮箱"

# 先查看即将提交的文件，再创建首次提交。
git status --short
git add .
git diff --cached --stat
git commit -m "Initial import"

# 将 URL 替换为你创建的仓库；不要提交 GitHub Token。
git remote add origin https://github.com/OWNER/REPOSITORY.git
git push -u origin main
```

也可使用 GitHub Desktop 的“Add existing repository”导入本目录并发布。建议通过 Git Credential Manager、GitHub Desktop 或 SSH 管理登录，不把 Token 写进远端 URL 或项目文件。

推送前用 `git remote -v` 核对目标仓库，用 `git branch --show-current` 确认分支；示例使用 `main`，按实际分支调整。

## 软件目录库单独维护

[软件库文件](../catalog/catalog.json) 可与客户端源码共用一个仓库，也可以放到独立仓库。
上传后复制它的 Raw 地址，在客户端“设置与关于 → 软件目录库”中保存。示例：

```text
https://raw.githubusercontent.com/OWNER/REPOSITORY/main/catalog/catalog.json
```

目录文件的更新可以单独提交，客户端同步后使用新目录，不需要重新打包 EXE。
文件格式与合并规则见 [软件目录库说明](../catalog/README.md)。

## 自动验证

[Windows 验证工作流](../.github/workflows/verify.yml) 在 push、pull request 或手动触发时：

1. 安装仓库要求的 .NET SDK。
2. 在 Windows x64 上分别构建 Debug / Release。
3. 运行全部同进程 xUnit 测试，包括独立软件库格式验证。
4. 将本次日志作为 Actions artifact 保留 7 天。

首次推送后在 GitHub 的 Actions 页查看实际运行结果。本地通过不能替代尚未运行的远端工作流。

## 发布安装包

EXE 安装包放在 GitHub **Releases** 的附件中，`dist` 不进入源码提交。构建过程见 [安装包说明](../packaging/README.md)。

每个版本建议上传：

- 对应版本的 `GitHubStore-Setup-<版本>-win-x64.exe`。
- 对应的 `.exe.sha256` 校验文件。
- 简短的版本说明，列出功能变化、已验证范围及剩余限制。

当前没有配置代码签名证书，生成的客户端安装包未签名。发布用证书和密码应保存在发布环境或 GitHub Secrets 中，不能放进仓库。

## 许可证

当前项目尚未指定自身源码许可证。公开前应选定许可证并添加根目录 `LICENSE`；公开仓库不等于授予开源授权。

内嵌第三方组件已有自己的许可和来源，见 [第三方声明](../src/GitHubStore.App/Assets/Tools/THIRD-PARTY-NOTICES.md)，应保留这些文件。
