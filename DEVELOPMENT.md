# 开发与验证

返回 [项目说明](README.md)。功能现状见 [架构文档](docs/ARCHITECTURE.md)，待办见 [开发计划](docs/ROADMAP.md)，上传与发布步骤见 [GitHub 仓库发布说明](docs/PUBLISHING.md)。

## 环境

- Windows 11 或满足项目最低要求的 Windows 10；编译目标为 `net8.0-windows10.0.19041.0`。
- .NET SDK 基准版本 **8.0.425**，由 [global.json](global.json) 设置 `rollForward=latestPatch`；同一 feature band 内允许使用后续补丁版本（历史 0.1.15 验证使用 8.0.426）。
- PowerShell 7。Visual Studio 用户需要相应的 .NET / Windows 开发组件。
- 首次还原需要访问 NuGet；普通构建和测试不需要 GitHub Token 或 AI 密钥。
- x64 为当前验证平台；项目也有 ARM64 配置，ARM64 设备兼容性仍需单独验证。

## 克隆后的构建与测试

在仓库根目录运行，构建前先关闭正在运行的开发程序：

```powershell
pwsh -File scripts\verify.ps1 -Configuration Debug -InProcess
pwsh -File scripts\verify.ps1 -Configuration Release -InProcess
```

[验证脚本](scripts/verify.ps1) 会创建输出目录、还原依赖、编译整个解决方案，再运行同进程 xUnit 测试。失败会中止，不以旧二进制替代构建失败。

等价的主要命令：

```powershell
dotnet restore GitHubStore.sln -p:Platform=x64 --disable-parallel -m:1 -nr:false
dotnet build GitHubStore.sln -c Debug -p:Platform=x64 --no-restore -m:1 -nr:false -p:UseSharedCompilation=false
dotnet run --project tests\GitHubStore.Core.Tests\GitHubStore.Core.Tests.csproj -c Debug --no-build --no-restore -- --result artifacts\test-results\local-Debug.json
```

普通开发机也可使用 `dotnet test`。部分受限环境无法启动 VSTest testhost，因此保留同进程入口；它运行同一测试程序集，不启动 WinUI。

GitHub 的 [Windows 验证工作流](.github/workflows/verify.yml) 在 push、pull request 和手动触发时执行 Debug / Release x64 构建及测试，并保存本次日志。工作流还会通过测试检查独立 [软件库文件](catalog/catalog.json) 的格式。

## 启动桌面程序

```powershell
pwsh -File scripts\run-desktop.ps1 -Configuration Debug
```

[桌面启动脚本](scripts/run-desktop.ps1) 把完整构建文件放到正常用户程序目录，再启动程序。普通开发构建包含 Windows App SDK 运行库，需要系统 .NET 8 Runtime；[EXE 安装包](packaging/README.md) 同时包含 .NET 运行库。

在带 Low 完整性标签的工作区中直接运行构建 EXE，可能无法保存普通用户目录中的配置；上述脚本通过正常用户目录的独立发布解决，不修改工作区权限。

默认数据根为 `%LOCALAPPDATA%\GitHubStore`。测试时可给进程设置 `GITHUBSTORE_DATA_DIR`，指向独立目录；它与设置中的下载缓存、软件安装根是不同配置。不要用真实用户目录做卸载或清理测试。

## 软件目录库

- [catalog/catalog.json](catalog/catalog.json) 是可独立发布的远端软件库文件；托管与格式见 [目录库说明](catalog/README.md)。
- [内置精选库](src/GitHubStore.Core/Catalog/CuratedCatalog.seed.json) 是客户端的离线兜底快照。
- [策展数据](data/curation/selection.json) 与种子分片用于重建内置库，生成入口为 [build-curated-seed.ps1](scripts/build-curated-seed.ps1)。
- 修改远端目录不要求同时修改内置库，但更新客户端离线兜底内容时应显式维护并验证两者。
- 目录同步只修改目录缓存；已安装登记和用户设置不在同步文件中。

## 安装包

按 [Windows 安装包说明](packaging/README.md) 准备 Inno Setup 6，再运行：

```powershell
pwsh -File scripts\build-installer.ps1 -Version 0.1.22 -CompilerPath 'C:\Path\To\ISCC.exe'
```

本次交付版本为 v0.1.22，已生成自包含 Release 安装包及 SHA256 文件，写入 `dist`；上述命令展示对应打包方式，已存在的同版本安装包不会被静默覆盖。后续打包应按实际发布计划递增版本号。本次发布产物、源码验证与远端发布的边界见下方“2026-11-02：当前启动器验证与版本状态”。

默认客户端安装位置是 `%LOCALAPPDATA%\Programs\GitHubStore`，按当前用户安装，无需管理员权限。卸载客户端保留用户数据和通过客户端安装的软件。

安装包可用 [verify-installer.ps1](scripts/verify-installer.ps1) 在本机唯一测试目录验收。脚本会先检查正式安装和快捷方式，避免覆盖；该测试会实际安装、启动和卸载客户端，适合专用测试环境，不在 CI 中自动执行。

## 本地产物与仓库边界

构建日志、测试 JSON/TRX、截图、临时配置和验收结果写入 `artifacts`，安装包写入 `dist`，两者均被 Git 忽略。首次克隆不会带上历史测试结果；请重新运行验证获得当前结果。

内嵌的 aria2 ZIP 是构建输入，必须随源码保留；其版本、摘要和许可见 [第三方声明](src/GitHubStore.App/Assets/Tools/THIRD-PARTY-NOTICES.md)。不要用全局 `*.zip` 忽略规则排除这些输入。

用户 AI 密钥和代理密码由当前 Windows 用户 DPAPI 保护，不能通过复制设置文件在另一用户环境中解密。真实设置、安装登记、下载缓存和凭据均不应上传。

## 2026-11-02：当前启动器验证与版本状态

本节记录当前源码状态。后续按日期保留的 0.1.15 等章节属于历史验证，不代表当前版本或本次新增验收；自动测试、真实程序启动、UI 点击验收与安装包发布分别记录。

- **本次 Release x64 验证：** v0.1.22 测试项目 Release 构建 0 警告、0 错误，同进程完整回归 1,908 通过、0 失败、0 跳过；报告 [release-0.1.22-Release-x64.json](artifacts/test-results/release-0.1.22-Release-x64.json) 的 `success=true`，`errors` 为空。客户端 Release 自包含 `dotnet publish` 与 Inno Setup 编译成功。以上为本次发布流程实际执行的本地验证，远端 CI 结果仍应以实际运行记录为准。
- **同日 Debug x64 验证：** App 与测试项目构建均为 0 警告、0 错误，同进程回归同为 1,908 通过、0 失败、0 跳过；报告 [launcher-path-arguments-Debug-x64.json](artifacts/test-results/launcher-path-arguments-Debug-x64.json)。
- **新增测试范围：** 真实 Windows 快捷方式的中文路径、带引号参数与原生读取；嵌套快捷方式、循环/失效目标拒绝；添加时保存解析后的 EXE 与参数、重载后启动；旧 `.lnk` 编辑保存转换并保留身份/名称/分类/计数；重复目标保留既有参数；非法参数与单清单原子写失败均不改变原路径/参数；显式清空旧 customization 参数后重载仍为空。具体用例见 [LauncherProgramResolverTests](tests/GitHubStore.Core.Tests/LauncherProgramResolverTests.cs)，计数/分类、启动选项与热键的既有回归仍包含在完整测试集中。快捷方式读取使用真实 Windows COM；合成 EXE 的启动目标与参数通过注入启动委托核对，没有执行这些合成文件，也不等于逐款第三方应用的端到端启动验收。
- **真实程序启动：** 最新开发程序 Build ID `EDD3170783B7ED66`、PID `20024` 已实际启动并确认 `Responding=true`，于 2026-11-02 09:44（Asia/Shanghai）正常退出，退出码 0。Windows UI Automation 未能读取本次窗口内部控件，因此没有本次“打开属性 → 编辑路径/参数 → 保存”的真实点击验收；程序响应与自动测试通过不替代此项。
- **本次自包含发布输出启动：** 使用独立 `GITHUBSTORE_DATA_DIR` 启动 v0.1.22 发布输出，PID `18876`，窗口标题“GitHubStore · 开源应用目录”，窗口句柄 `395604`，`Responding=true`；正常请求关闭后已退出，退出码 0。证据 [release-0.1.22-smoke.json](artifacts/test-results/release-0.1.22-smoke.json)。此项覆盖发布输出启动/响应/退出，不是安装器安装卸载全流程，也不补足上述属性 UI 点击验收。
- **本次发布产物：** [GitHubStore-Setup-0.1.22-win-x64.exe](dist/GitHubStore-Setup-0.1.22-win-x64.exe) 已生成，50,656,049 字节，文件版本 `0.1.22.0`；SHA-256 为 `571c97b79340d9cbf00105631c1c61c62ead4c1c15ff8081c303203214d05a9f`，构建元数据见 [installer-build.json](dist/installer-build.json)。本包包含当前方卡、精简属性、快捷方式解析及路径/参数原子保存改动。
- **历史本地安装包：** 0.1.21 已于 2026-11-01 本地生成，对应当时 1,894 项测试通过；它不包含后续 128×128 方卡和精简属性编辑/快捷方式解析保存改动，不能用该旧包代表本次交付。
- **CI 兼容性修正：** 首轮 GitHub Windows Server 2022 验证在测试准备阶段给 `WScript.Shell.TargetPath` 设置中文目标路径时失败，尚未执行快捷方式解析器。测试夹具改用 Unicode `IShellLinkW` / `IPersistFile` 创建真实快捷方式，保留中文路径与引号参数输入，不跳过或弱化用例。本地重新执行 Debug / Release 各 1,908 项回归均通过，报告为 `artifacts/test-results/release-0.1.22-Debug-x64.json` 与上述 Release 报告；该修正只改测试和文档，不改变应用发布文件。
- **远端状态边界：** 本次交付版本为 v0.1.22，源码与安装包上传按发布流程执行；本节不提前宣称远端 CI 或正式 Release 已成功，远端状态以实际上传和运行结果为准。

复现本次 Debug x64 构建及同进程测试的主要命令如下；如依赖尚未还原，先运行前文的还原命令。构建失败时应停止，不运行旧测试二进制：

```powershell
dotnet build src/GitHubStore.App/GitHubStore.App.csproj -c Debug -p:Platform=x64 --no-restore -m:1 /nr:false -v:minimal
dotnet build tests/GitHubStore.Core.Tests/GitHubStore.Core.Tests.csproj -c Debug -p:Platform=x64 --no-restore -m:1 /nr:false -v:minimal
dotnet tests/GitHubStore.Core.Tests/bin/x64/Debug/net8.0/GitHubStore.Core.Tests.dll --result artifacts/test-results/launcher-path-arguments-Debug-x64.json
```

当前启动器的维护边界如下，服务分工见 [架构文档](docs/ARCHITECTURE.md#应用启动器)，用户操作见 [启动器使用说明](docs/LAUNCHER.md)：

- [IManualAppLauncher](src/GitHubStore.Core/Abstractions/ManualLauncherContracts.cs) / [ManualAppLauncher](src/GitHubStore.App/Services/Launcher/ManualAppLauncher.cs) 独立维护 `manual-launcher.json`；安装回执仍由 [ManagedAppLauncher](src/GitHubStore.App/Services/Launcher/ManagedAppLauncher.cs) 保存于 `managed-apps.json`，手动入口不授予安装/卸载所有权。
- [LauncherProgramResolver](src/GitHubStore.App/Services/Launcher/LauncherProgramResolver.cs) 将添加或重新选择的 EXE/`.lnk` 解析为程序路径与参数，快捷方式通过 STA 原生读取而不执行。新手动记录和简化属性保存将 path + arguments 写入同一清单并原子替换。手动 `Arguments=null` 兼容旧 customization 参数，`Arguments=""` 明确清空；旧 `.lnk` 在编辑保存后转换为 EXE 与参数。
- [LauncherCustomizationStore](src/GitHubStore.App/Services/Launcher/LauncherCustomizationStore.cs) 保存成功启动计数与兼容选项；[LauncherViewModel](src/GitHubStore.App/ViewModels/LauncherViewModel.cs) 按来源 + ID 隔离手动/受管项，按目录分类分组、成功启动次数降序排序，失败与取消不累计。受管分类来自目录并兼容旧覆盖，手动分类来自入口记录。
- [启动器界面](src/GitHubStore.App/Views/LauncherView.xaml) 使用 128×128 DIP 方卡；Store 使用主题默认配色，manual 使用青色，已有自定义颜色优先。当前属性框只编辑程序路径与参数，受管路径只读；添加入口仍可填写名称、选择分类并确认解析结果。旧名称/分类覆盖、图标、颜色、工作目录、提权、热键、备注等配置保留兼容，运行或显示所需选项仍生效，但不再作为属性框可编辑字段。
- [ShellIconReader](src/GitHubStore.Platform.Win32/Shell/ShellIconReader.cs) 补充原生 Shell/快捷方式图标读取；[LauncherHotkeyRegistry](src/GitHubStore.App/Services/Launcher/LauncherHotkeyRegistry.cs) 继续为已有热键配置注册主窗口 `WM_HOTKEY`，注册仅在应用运行期间有效。

更新与回退的当前实现也已核对：[PortableUpgradeService](src/GitHubStore.App/Services/Installation/PortableUpgradeService.cs) 在升级提交后删除被替换的旧目录，仅保留切换过程中供失败恢复的临时备份；显式回退从绑定软件源选择比当前版本更旧的最近可用 release，优先复用下载缓存，否则重新下载，并按同一流程替换安装。它不依赖长期保留一份本地旧目录。当前文档核对不新增绿色升级/回退的实机验收结论。

## 2026-11-02：安装器实际目录登记修复

EXE/MSI 优先使用设置中的安装目录（默认 `C:\githubapps\<应用名>`）：Inno 安装器使用交互式 `/DIR=` 参数预填，未知 EXE 仅在前 4 MiB 中发现 Inno 官方 setup-data 标记时使用该参数；MSI 继续读取包内公开目录属性。未识别的安装器由用户在向导中优先选择该目录，不猜测其命令行参数。

若安装器最终使用其他目录，安装服务比较安装前后的 Windows 卸载登记，按应用名或仓库名精确归一匹配，优先唯一新增或变化的登记，再回退唯一既有登记。核验实际目录、缓存隔离和主程序后保存 Store 回执，使应用出现在已安装页和启动器；多个候选或无法验证入口时不虚构安装成功。位置发现读取失败时，仍可核验原预设目录。

安装器回执可以指向外部目录，绿色便携包仍要求位于配置安装根的直接子目录。EXE/MSI 的文件由第三方卸载器管理；卸载前仍按回执重新核对 Windows 登记，不因目录例外而授予直接删除权限。已有但从未保存回执的旧安装不会被自动扫描认领，可在新版中重新运行该应用的安装流程完成核对登记。

本次验证：Debug x64 整个解决方案构建成功，0 警告、0 错误；同进程完整回归 1,932 通过、0 失败、0 跳过，报告 [external-installer-registration-Debug.json](artifacts/test-results/external-installer-registration-Debug.json)。新增 24 项用例覆盖目录发现与拒绝、Inno 预填、外部回执重载/启动/已安装/卸载接入以及便携目录限制。只读核查确认本机 Zed 1.26.2 有 Windows 安装与卸载登记；没有重新安装、移动或卸载真实 Zed。随后按用户要求完成 v0.1.23 的 Release x64 测试（1,932 通过、0 失败、0 跳过）、自包含发布与 Inno Setup 打包；安装包文件版本 `0.1.23.0`、50,639,171 字节，摘要和交付边界见 [安装包状态](packaging/README.md#当前产物状态2026-11-02)。首次还原遇到 NuGet 审计服务不可达，仅在重试打包进程中临时设置 `NuGetAudit=false`；未修改长期配置。没有替换当前客户端或上传远端 Release。

## 历史验证记录

以下保留原日期、版本和验证边界，便于追溯。

## 2026-10-22 AI 助手集成验证

- Debug x64 完整解决方案构建：0 警告、0 错误，日志 `artifacts/build-ai-Debug-x64.log`。
- 同进程完整测试：1096 通过、0 失败、0 跳过，报告 `artifacts/test-results/ai-integration-Debug-x64.json`。包含共享模型客户端、原 README 摘要兼容性、全目录语义搜索与真实 ID 校验、安装诊断快照、已安装问答身份关联、异步取消及旧结果隔离。
- 六类真实模型任务验收通过：用途搜索、安装诊断、安装包解释、版本解读、已安装问答、卸载说明；使用内置真实目录和隔离的合成安装登记，不修改真实安装清单。初次选包说明因模型思考内容占用预算而被截断，增加 MiniMax 最低 4096 token 预算后重试通过。报告 `artifacts/ai-live-acceptance/report.json` 与 `artifacts/ai-live-acceptance/report-retry.json`。
- 首次标准验证的还原步骤因 NuGet 审计源不可达产生 NU1900；本轮使用 `dotnet restore GitHubStore.sln -p:Platform=x64 -p:NuGetAudit=false --ignore-failed-sources --disable-parallel -m:1 -nr:false` 从缓存还原，再正常执行 `--no-restore` 构建。没有修改仓库的 NuGet 审计或依赖设置。
- 桌面开发版实际启动并保持响应，通过 Windows UI Automation 确认排行榜中的 AI 搜索、所选安装包解释、版本解读与失败诊断入口；设置界面在源码中同步更新了 AI 数据用途和当前功能状态说明。
- 当前用户的本地 OpenAI 兼容接口模型列表及实际 Chat Completions 已连通；模型 `minimax-cn/MiniMax-M3`，凭据由现有 PreferencesService 以 DPAPI 保护保存，没有加入源码。

## 2026-10-22 单页软件卡片与中文 README

- Debug x64 完整解决方案构建：0 警告、0 错误；日志 `artifacts/build-catalog-redesign-Debug-x64.log`。
- 同进程完整测试：1118 通过、0 失败、0 跳过；报告 `artifacts/test-results/catalog-redesign-Debug-x64.json`。新增覆盖卡片身份与字段、更新时间来源、截图惰性加载/并发/取消、分段翻译、缓存隔离、代码链接保留、原文切换和旧弹窗结果隔离。
- 实际启动桌面程序并截图检查单列卡片、40 DIP 搜索栏、收纳后的操作区及缺图占位；实际点击卡片确认中文 README 弹窗和 Markdown 中文内容渲染。验收文档为独立目录内明确标识的合成 README，不写入真实用户目录或安装清单。
- 使用已配置的 MiniMax 接口验证真实翻译与缓存恢复成功；真实 GitHub README 请求因当前环境 TLS 握手失败返回 `network_error`，未把合成文档作为真实仓库 README 验证。没有关闭证书校验。
- 列表截图来自实际目录/README 图片，不生成或猜测图片；网络失败与缺失图片保留明确占位。截图证据保存在 `artifacts/catalog-redesign/`。

## 2026-10-22 趋势首页与启动器拆分

- Debug x64 完整解决方案构建：0 警告、0 错误；日志 `artifacts/build-discovery-home-Debug-x64.log`。
- 同进程完整测试：1159 通过、0 失败、0 跳过；报告 `artifacts/test-results/discovery-home-Debug-x64.json`。新增覆盖完整真实榜单解析、统计周期与指标语义、缓存与快照回退、来源独立失败、超大响应、无效来源/身份拒绝、按需点评与取消。
- 数据来源经公开页面核实：Trendshift 每日榜（排名、趋势评分、当日新增与累计 Star、语言、描述，以及源站实际收录的 X 提及）与 Star History Weekly（当周新增 Star 与发布区间）。新增 Star 一律使用来源给出的完整整数，拒绝 `+133.4k` 这类四舍五入值；累计 Star 不会被当作新增量，仓库描述也不会被当作评价。
- 使用抓取的完整真实页面验收解析：Trendshift 25 条、Star History 20 条，字段与来源一致；接受平台上的真实提及仅 1 条关联到当日榜内仓库，其余条目不含文字评价，界面如实提示“源站未提供文字评价”。
- 随程序分发本次核实的带日期快照，仅在首次无缓存或抓取失败时回退，并始终标注缓存与过期、保留真实错误码；不写入缓存，因此不压制后续实时刷新。网络受限环境中的实际启动显示“来源缓存 · 数据已过期”与抓取失败提示，而非伪造实时数据。
- 实际桌面验收：程序默认进入新的“首页”，左右两栏显示两个来源、统计周期与来源链接；“启动器”导航显示“我的应用”图标网格；点击条目上的“AI 点评”才调用模型，结果标注为 AI 生成并与源站提及分开。加载榜单本身不调用模型。
- 桌面验收发现并修复：重建分类项时 NavigationView 会自动选中新分类，导致启动后跳到目录页；现在重建期间忽略该自动选择并恢复原选中项，启动稳定停留在首页。截图证据保存在 `artifacts/discovery-home/`。

## 2026-10-22 默认选包策略修复

- 问题：KeePassXC 同时发行 `KeePassXC-2.8.0-x64.zip` 与同名 `.msi`，默认却推荐 MSI。原因是策略要求压缩包文件名带 `windows` 或 `portable` 标记，该 ZIP 两者都没有，被判定为不可用后只剩 MSI。
- 审计范围不限于个案：用离线目录全部 70 个条目逐一比对推荐结果，发现同类漏选共 5 处、其中改推荐 MSI 的 1 处（KeePassXC）。
- 修复：新增“同名配对”证据——压缩包与同发行版中的 EXE/MSI 归一化后同名时，视为该产品自己的绿色分发件，优先级高于安装器。归一化会去掉 `setup`/`install` 等安装器词并统一分隔符，因此 `Paseo-0.12.0-x64.zip` 能与 `Paseo-Setup-0.12.0-x64.exe` 配对；同时以 `Setup`/`Install` 命名的压缩包本身被排除，避免把“安装器打包”误当成绿色版。
- 修复后重新审计全部 70 个条目：改推荐 MSI 的案例降为 0，KeePassXC 默认推荐为 `KeePassXC-2.8.0-x64.zip`；剩余 3 处未选压缩包均为 arm64 归档在同为 arm64 的对照下被正确拒绝为架构不匹配。
- Debug x64 完整构建 0 警告 0 错误（`artifacts/build-asset-policy-Debug-x64.log`），完整测试 1165 通过、0 失败（`artifacts/test-results/asset-policy-Debug-x64.json`），新增同名配对、安装器包排除、未配对压缩包不推荐、架构不匹配等回归用例。审计明细见 `artifacts/asset-audit/recommendations.json`。
- 实际桌面验收：在运行的程序中打开“系统工具”，KeePassXC 显示“默认推荐：KeePassXC-2.8.0-x64.zip”，MSI 仍可在列表中手动选择。截图见 `artifacts/asset-policy/`。

## 2026-10-22 KeePassXC 端到端安装与移除验收

- 代理按用户要求配置为 SOCKS5 `127.0.0.1:7890`（写入设置的网络分区，原设置已备份到 `artifacts/settings-backup-before-proxy.json`）。
- Debug x64 完整构建 0 警告 0 错误（`artifacts/build-removal-Debug-x64.log`），完整测试 1180 通过、0 失败（`artifacts/test-results/removal-Debug-x64.json`）。
- 真实下载安装：经代理下载 `KeePassXC-2.8.0-x64.zip`（70,348,471 字节，SHA-256 `48ff4f08…fc828`）；应用内安装成功，登记记录的 `artifactSha256` 与手工下载一致，安装资产确为 ZIP 而非 MSI。
- 启动器：登记后出现在“启动器”，自动化点击条目真实启动了 `KeePassXC.exe`；冻结版本复跑时读到了进程的真实路径 `C:\githubapps\keepassxc\KeePassXC-2.8.0-x64\KeePassXC.exe`，证明启动的是安装目录内的可执行文件，而不是同名的其他程序（ZIP 内含一层子目录，入口由程序自动识别）。
- “快捷方式”的实际含义：程序**没有**创建 Windows 桌面或开始菜单的 `.lnk` 快捷方式；它生成的是启动器内的托管入口（`managed-apps.json` 登记 + 图标网格项）。验收后核查桌面、开始菜单与安装目录内均无 `.lnk`。这是当前实现的真实边界，不宣称已创建系统快捷方式。
- 本轮同时补齐了此前**完全缺失**的删除能力：新增 [ManagedAppRemovalService](src/GitHubStore.App/Services/Inventory/ManagedAppRemovalService.cs)，只允许删除本程序自行解包的绿色/便携安装；授权依据是按安装 ID 重新读取的登记原文（伪造的界面快照无法授权删除），删除前独立重做“必须是安装根目录直接子目录”的路径校验并拒绝重解析点，顺序为先撤登记再删目录。
- 移除：已安装页“卸载”按钮可用，确认后登记与安装目录一并清除，启动器不再显示该条目；移除后 `managed-apps.json` 为 `entries: []`，`C:\githubapps\keepassxc` 与安装缓存均无残留，`C:\githubapps` 下无其他遗留。
- 单个脚本一次性跑通全流程（下载 → 解压安装 → 启动器启动 → 删除）的最终证据保存在 `artifacts/install-acceptance/e2e-result.json`。
- 验收脚本与截图保存在 `artifacts/install-acceptance/`（`04-launcher`、`05-launched`、`06-uninstall-before`、`07-uninstall-confirm`、`08-after-removal`）。
- 代理作用说明：本机该校验同时验证了“直连”与“经 SOCKS5 代理”两种情况，二者都能成功获取同一资产。因此本次验收能证明代理配置已生效并被应用使用，但**不能**证明没有代理就会失败；如实记录，不夸大代理的必要性。
- 验收环境说明：受限文件沙箱会拦截 .NET 的 TLS 连接，且工作区 `bin` 目录下启动的程序无法写入 `C:\githubapps`；因此真实下载与安装必须在放宽沙箱后，用部署到用户目录的开发版执行。这属于验证环境限制，不是程序缺陷。

## 2026-10-23 Windows 登记卸载程序

- 安装器应用从 Windows 的 HKLM/HKCU 卸载登记（32/64 位视图）匹配实际安装目录和软件名称，必须唯一匹配；旧 MSI 收据即使缺少产品码也可使用真实系统登记。执行前重新读取登记，不接受 UI 快照中的任意命令。
- 点击“卸载”后显示确认和管理员权限提示。MSI 使用系统目录的 `msiexec.exe /x <Windows 登记的产品码> /norestart`，不加静默参数；机器级安装请求 UAC 授权。EXE 使用经过校验的已登记卸载入口；本程序不直接删除安装器应用的文件。取消授权或执行失败保留登记。
- 卸载器返回后再次检查 Windows 登记；尚未消失时只报告卸载器已启动，保留记录；确认登记消失后才撤销本程序安装记录。
- Debug x64 构建 0 警告、0 错误；1226 项测试通过、0 失败。报告 `artifacts/test-results/windows-uninstall.json`。
- 主会话实际操作部署构建 `9EDEAC32BA161E19`，通过程序选择 KeePassXC 2.8.0 MSI，确认实际安装目录为 `C:\githubapps\keepassxc`、入口存在、Windows 有真实卸载登记。已选中的卸载按钮可用，实际点击确认；之后核查 HKLM/HKCU 产品项消失、应用登记数为 0、安装目录消失。证据 `artifacts/msi-acceptance/windows-uninstall-verified.json` 及 `windows-uninstall-real/uninstall-confirmation.png`。
- 不把仅出现向导当成验收成功；未直接记录向导目录页初始值或 UAC 同意过程。此前委派沙箱权限失败的报告保留，不作为通过依据。最终源码另修正 ProductCode 读取的大小写限制，该修正不改变已实测的 Windows 登记卸载路径。

## 2026-10-23 持久下载缓存

- 行为：下载与安装共用同一份持久缓存（`<缓存根>/packages-v1/<下载地址 SHA-256>/<文件名>`，带所有权标记）。安装结束（含取消与失败）后不再删除安装包；再次下载或安装前先校验（有上游 SHA-256 就核对上游，否则核对本程序在首次下载后写入的本地摘要），命中即复用、不联网。
- 只有设置页“清空全部下载缓存”会删除，且需要确认框；正在下载或安装时返回“使用中”而不删除任何文件。清空只处理本程序标记的缓存命名空间，保留目录中的其他文件与已安装程序；删除失败的文件会保留并如实统计，不谎报为 0。缓存根切换不会移动或删除旧根目录的内容。
- Debug x64 完整构建 0 警告、0 错误；1253 项测试通过、0 失败。报告 `artifacts/test-results/package-cache.json`。新增覆盖校验命中/未命中、大小或摘要变化导致失效、非法摘要拒绝、仅大小相同但内容被改的本地摘要校验失败、不同地址同文件名分槽、显式清空只动本程序缓存、连接点拒绝、租约期间清空返回忙碌、被占用文件保留等。
- 真实联网验收（隔离数据目录，经已配置代理）：首次安装真实下载 `Captura-Portable.zip` 到缓存（2,811,856 字节）并在安装后保留；删除已装目录后，用“任何网络请求都抛异常”的 HttpClient 再次安装仍成功，阶段中出现“使用缓存”且没有“下载”阶段，直接证明命中后确实不联网。汇总显示 1 个文件/2,811,856 字节；手动清空删除 2 项、释放 2,812,079 字节、剩余 0；再次清空删除 0。证据 `artifacts/cache-acceptance/`。
- 真实界面验收（部署构建 `FE8270574B3F4644`）：在“图形设计”中通过“下载所选包（不安装）”真实下载 3.9 MiB 到缓存；设置页“目录设置”显示“下载缓存：1 个文件，共 3.9 MiB”；点击“清空全部下载缓存”并确认后显示“已删除 2 项缓存，释放 3.9 MiB。已安装程序不受影响。”，占用回到 0。截图 `artifacts/cache-acceptance/ui/`。
- 仍待验证：进程重启后复用、切换缓存根后的旧目录处理、部分删除失败与占用文件残留的界面提示。已记入 [开发计划](docs/ROADMAP.md)。

## 2026-10-24 至 2026-10-25：0.1.9–0.1.14

近期功能记录见 [更新日志](docs/CHANGELOG.md)。本轮最终代码的验证：

| 阶段 | 完整回归结果 | 主要覆盖 |
|---|---|---|
| 0.1.9–0.1.10 | 1686 通过 | 每周榜内联脚本数据解析、启动早期异常退出反馈及已有回归 |
| 0.1.11 | 1712 通过 | winget 详情命令、中文/英文标签、多行说明、身份校验、缓存和旧响应隔离 |
| 0.1.13 | 1740 通过 | winget 独立安装参数、无路径/范围限制、并发、取消、超时及结果反馈 |
| 0.1.14 | 1750 通过 | PE 图标数字/命名资源、语言、高分辨率帧选择、损坏文件及既有功能回归 |

- 最终回归 0 失败、0 跳过，报告 `artifacts/test-results/launcher-icons.json`；winget 详情与安装的阶段报告分别为 `winget-details-final.json`、`winget-install-final.json`，位于同一测试结果目录。
- Windows x64 Release 客户端编译 0 警告、0 错误，自包含安装包生成成功；最终版本戳 `0.1.14.0`，安装包 SHA-256 `2a8ab476a9888076ff351cd316303d8929954ec634549da662135f7bf5fb2321`。
- 真实元数据查询：`winget show` 成功取得本机 7-Zip 的包身份、版本、发布者、许可证、主页和发行说明。`--locale en-US` 不会保证 CLI 标签转为英文，因此保留中文标签回归。此项不等同于真实 winget 安装通过。
- 开发阶段安装命令和界面状态通过模拟进程边界测试；开发验证没有实际安装第三方 winget 软件。用户后续已确认近期版本验收完成；这不代替不同设备、UAC 拒绝、超时和不同安装器的扩展测试矩阵。
- 生产图标读取器实际读取本机 DevToys/KeePassXC EXE，分别取得 512×512 紫色 PRE 图标和 256×256 绿色钥匙图标，成功解码并查看；图像提取结果位于 `artifacts/icon-probe/`。独立桌面测试未取得可靠的目标窗口截图，未把提取结果当成 WinUI 界面截图。当前近期版本验收状态以用户后续确认为通过。
- **用户实机确认（2026-10-25 14:25，Asia/Shanghai）：** 用户反馈“验收 和 DevToys 启动崩溃问题 已经解决”。据此记录近期版本验收通过，DevToys 的 WebView2 `0x800700AA` 启动问题已解决。未新增代码修复、独立复现或根因结论；不将该结果归因于图标读取修改。
- 本机曾出现安装器 Temp 目录 Error 5，当时诊断没有确认需要修改的目录权限，因此没有进行权限变更。用户后续确认近期版本验收完成；Temp 错误机制没有单独确认。
- 发布时英文 GitHub runner 暴露 7 项既有设置测试的系统语言依赖：测试初始化加载 `Language=system` 后覆盖了构造时的中文设置，但断言仍固定中文。现为相关测试显式保存 `Language=zh-CN`，不修改产品语言逻辑；该补充仅影响测试和文档，不改变已生成的 0.1.14 安装包。远端结果以对应 GitHub Actions 运行记录为准。
- `artifacts` 内含隔离测试数据与本机诊断，仅保留在本地，不纳入公开源码或 Release 附件。

## 2026-10-27：0.1.15 发布验证

- 使用本机 .NET SDK 8.0.426 完成 Windows x64 Release 解决方案还原与构建。
- 本次同进程回归：1752 通过、0 失败、0 跳过；报告 `artifacts/test-results/inprocess-tests-Release-x64.json`。
- 初次测试中 221 项因默认用户 Temp 目录访问受限而失败；仅为测试进程设置 `TEMP`、`TMP` 到工作区 `test-temp/release-0.1.15` 后，使用同一构建重跑全部通过，没有修改系统文件权限。需要复现该环境时，可在运行测试前创建该目录并设置这两个环境变量。
- 当前机器已有正式安装，未运行会安装、启动和卸载的整套安装器验收脚本，以免覆盖现有登记；当前版本不宣称新增全新安装或升级实机验收。安装包的版本、运行库与摘要检查结果见 [更新日志](docs/CHANGELOG.md)。
- 同步合并后的本地描述与多分类只在首次合并时保留，后续同步会采用远端内容；该限制来自源码路径核对，不是持续保留测试已经通过。

## 验证范围

当前已有软件目录、同步及离线缓存、设置独立保存、下载完整性、安装登记、ZIP 解压路径防护、启动器、winget 查询/安装边界、PE 图标与 Markdown 等自动测试。本机完成过早期安装包的隔离安装、启动、自带运行库加载和卸载保留数据验收；该历史结果不替代当前版本验收。

程序测试通过不等于逐款第三方软件安装兼容性验收；7z、真实绿色升级/回退、ARM64 运行，以及 aria2 / Xget / winget 的完整公网链路仍按 [开发计划](docs/ROADMAP.md) 单独验证。GUI 或安装器测试使用合成数据与独立目录，不卸载用户日常软件。
