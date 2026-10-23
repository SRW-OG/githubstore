# 软件源与 JSON 目录协议

> 状态：设计提案 v1（2026-10-19）
>
> 适用范围：GitHubStore 官方源、用户添加的第三方源、企业/个人精选源
>
> 本文是软件源文件格式、同步语义、来源身份与信任策略的唯一真源；领域类型与 SQLite 表以 `01-领域模型与数据契约.md` 为准。

---

## 本章结论（TL;DR）

1. 软件目录采用 **HTTPS 托管的版本化 JSON 快照**。官方源可托管在 GitHub 仓库并由 GitHub Actions 定期生成；客户端也允许用户添加第三方 `source.json`。
2. `source.json` 只是一个很小的可变入口，指向不可变的 `snapshots/{revision}/index.json`。客户端必须先完整下载、验证，再以 SQLite 事务切换快照；失败继续使用上一次成功版本。
3. 软件身份使用 `(SourceInstanceId, PackageId)`，不能只按名称、GitHub 仓库或源声明的 `id` 覆盖。安装记录永久绑定实际来源；跨源切换必须重新确认。
4. JSON 是不可信数据。软件源只能声明类型化元数据，**不能携带 PowerShell、CMD、任意命令、安装前后脚本或 DLL 插件**。
5. 官方源必须使用客户端内置公钥验证；第三方源可为“已验证签名”或“未签名”。首次信任第三方密钥需要用户核对指纹，换钥必须重新确认。哈希保证文件一致性，签名才用于确认源发布者身份。
6. v1 下载完整快照，不做增量补丁。源更新、GitHub 元数据刷新和应用安装是三类独立操作，不得混为一体。

---

## 1. 目标与边界

### 1.1 目标

- 无需发布新版客户端即可新增软件、更新版本、修正分类与安装适配信息。
- 允许社区、企业和个人维护自己的精选源。
- 离线时继续浏览上一次成功同步的目录。
- 同一应用来自多个源时，来源清晰、更新不会串源、卸载不会越权。
- 格式可以演进，并能在导入前完成确定性的结构校验和大小限制。

### 1.2 非目标

- 不托管或镜像第三方安装包。
- 不把软件源当作远程脚本系统或插件系统。
- 不允许软件源降低客户端的哈希、签名、路径或提权策略。
- 不因为删除/禁用软件源而卸载已安装软件。
- v1 不合并多个源对同一软件的字段，也不自动选择“版本更高”的来源。

---

## 2. 仓库布局与发布模型

推荐每个软件源使用独立 GitHub 仓库：

```text
catalog/
├── source.json
├── source.json.sig                 # 可选；官方源必需
└── snapshots/
    └── 42/
        ├── index.json
        └── apps/
            ├── sharex.json
            └── powertoys.json
```

- `source.json`：稳定入口，只保存源元数据、当前 revision 和索引摘要。
- `snapshots/{revision}/`：发布后不可修改。更正数据必须生成更大的 revision。
- `index.json`：浏览、搜索和详情定位所需的轻量索引。
- `apps/*.json`：安装器、版本和展示详情；v1 在激活 revision 前全部下载并验证，激活后从本地快照读取。README、截图等非目录媒体仍可按需获取。
- 小型源允许将详情内联到 `index.json`，但协议语义不变。

发布顺序必须是：上传完整快照 → 验证所有文件可读取 → 最后原子更新 `source.json`。不能先更新入口再逐个上传详情。

---

## 3. `source.json` 入口格式

```json
{
  "schemaVersion": 1,
  "id": "githubstore.official",
  "name": "GitHubStore 官方软件源",
  "description": "精选 Windows 开源软件",
  "homepage": "https://github.com/example/windows-catalog",
  "revision": 42,
  "updatedAt": "2026-10-19T00:00:00Z",
  "index": {
    "url": "./snapshots/42/index.json",
    "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
    "size": 183240
  },
  "signing": {
    "algorithm": "ed25519",
    "keyId": "official-2026-01",
    "publicKey": "BASE64_ENCODED_32_BYTE_ED25519_PUBLIC_KEY"
  }
}
```

### 3.1 字段约束

| 字段 | 约束 |
|---|---|
| `schemaVersion` | v1 只接受整数 `1`；未知大版本拒绝导入但保留旧快照 |
| `id` | `[a-z0-9][a-z0-9.-]{2,63}`；只用于展示和诊断，不作为本地唯一身份 |
| `name` | 1–80 个 Unicode 字符；按纯文本显示 |
| `homepage` | 可选 HTTPS URL；打开前显示完整目标 |
| `revision` | 非负、严格递增的 64 位整数；小于本地已见值视为回滚 |
| `updatedAt` | RFC 3339 UTC；只用于显示，不参与新旧判定 |
| `index.url` | HTTPS 绝对 URL或相对入口 URL；禁止 `file:`、UNC、重定向到 HTTP |
| `index.sha256` | 64 位小写十六进制，覆盖索引原始字节 |
| `index.size` | 下载前预算；实收不得超过声明值和客户端上限 |
| `signing` | 可选；存在时 `algorithm` v1 仅允许 `ed25519`，`publicKey` 是 32 字节公钥的 Base64，`source.json.sig` 是对 `source.json` 原始 UTF-8 字节生成的 64 字节签名再 Base64 编码 |

`source.json.sig` 必须是只含 Base64 文本的独立响应（允许末尾换行），不是 JSON。客户端解码后要求恰好 64 字节。官方源忽略清单自报公钥并使用客户端内置公钥验签；第三方源首次添加时用清单公钥计算并展示 SHA-256 指纹，只有用户确认后才固定到该 `SourceInstanceId`。

相对 URL按最终成功获取 `source.json` 的 URL 解析。所有重定向都重新执行 HTTPS、私网地址和大小策略检查。

---

## 4. 索引与应用详情

### 4.1 `index.json`

```json
{
  "schemaVersion": 1,
  "sourceRevision": 42,
  "generatedAt": "2026-10-19T00:00:00Z",
  "apps": [
    {
      "packageId": "sharex.sharex",
      "name": "ShareX",
      "summary": "屏幕截图与工作流工具",
      "repository": "https://github.com/ShareX/ShareX",
      "categories": ["graphics", "utilities"],
      "tags": ["screenshot", "productivity"],
      "license": "GPL-3.0-or-later",
      "stars": 32000,
      "latestVersion": "17.0.0",
      "updatedAt": "2026-09-01T00:00:00Z",
      "detail": {
        "url": "./apps/sharex.json",
        "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
        "size": 4096
      }
    }
  ]
}
```

`stars` 等快速变化字段必须配合 `generatedAt` 显示采集时间。客户端可以再通过 GitHub API刷新展示数据，但不得用 API 结果偷偷改写源声明的安装器或信任信息。

### 4.2 应用详情

```json
{
  "schemaVersion": 1,
  "packageId": "sharex.sharex",
  "name": "ShareX",
  "repository": "https://github.com/ShareX/ShareX",
  "license": "GPL-3.0-or-later",
  "releases": [
    {
      "releaseKey": "stable:17.0.0",
      "version": "17.0.0",
      "channel": "stable",
      "publishedAt": "2026-09-01T00:00:00Z",
      "installers": [
        {
          "installerId": "windows-x64-setup",
          "architecture": "x64",
          "type": "inno",
          "url": "https://github.com/ShareX/ShareX/releases/download/v17.0.0/example-setup.exe",
          "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
          "size": 1000000,
          "scope": "user",
          "strategies": ["default", "custom"],
          "silentArgs": ["/VERYSILENT", "/NORESTART"]
        }
      ]
    }
  ]
}
```

示例 URL、版本和摘要值仅说明格式，不是可用的发布记录。`releaseKey` 必须在一个 `PackageId` 内稳定且唯一；`installerId` 必须在一个 `releaseKey` 内稳定且唯一。两者都是受长度和字符集限制的源内字符串，不使用 GitHub release/asset 数字 ID。客户端以 `AppId + releaseKey + installerId` 构造本地键。

### 4.3 v1 允许的安装描述

- `type` 仅允许客户端认识的枚举：`msi`、`inno`、`nsis`、`msix`、`zip-portable`、`winget`；未知值拒绝该安装器。
- HTTP 类型必须提供 `url`、`sha256`、`size`；可执行文件固定为客户端已下载并验证的资产，或固定的系统组件 `msiexec.exe`。源不能改变执行文件。
- `winget` 类型不提供 `url`/`sha256`，必须提供 `wingetPackageId` 与 `wingetVersion`，且 v1 只允许客户端内置的 `winget` community source。客户端固定使用 `winget install --id <id> --version <version> --source winget --exact` 的类型化参数，源不得声明其他 winget source；此类仅支持 `default` 策略。
- 参数必须是字符串数组，通过 `ProcessStartInfo.ArgumentList` 逐项传递；禁止完整命令行字符串。
- 禁止 shell 元字符解释，禁止 `cmd.exe`、PowerShell、WSH、脚本文件和安装前后 hook。
- `strategies`、`scope` 和 `silentArgs` 是源的声明，不是客户端的安全结论。客户端仍须能力探测并在执行前复校。
- `sha256` 缺失的 HTTP 资产在 v1 官方源不得发布；第三方源缺失时不可自动或提权安装。

---

## 5. 来源身份、冲突与安装绑定

### 5.1 两层身份

- `SourceInstanceId`：客户端添加源时生成的 UUID，代表这次订阅；用户不能编辑。
- `PackageId`：源内稳定 ID；同一源内不得复用给不同项目。
- 全局目录键：`(SourceInstanceId, PackageId)`。

源声明的 `id`、名称、仓库地址都不能代替 `SourceInstanceId`。这样可以避免恶意源伪装成官方源，也避免两个同名社区源互相覆盖。

### 5.2 重复软件

客户端可按规范化 GitHub 仓库 URL 提示“其他源也提供此软件”，但不自动合并记录。默认搜索结果可以折叠展示重复项，展开后明确显示来源和信任级别。

安装确认时固定：`SourceInstanceId + PackageId + SourceRevision + releaseKey + version + installerId + channel + 策略 + 目标路径`；HTTP 通道另固定实际资产 URL 与 SHA-256，winget 通道另固定 `wingetPackageId + wingetVersion`。后续更新默认只从原来源和原通道获取。切换来源或通道等同新的安装决策，必须重新展示发布者、版本、URL/包 ID 和权限差异并让用户确认。

禁用或删除源后：

- 保留安装记录及最后一次成功快照的来源信息；源订阅在数据库中软删除，`SourceInstanceId` 不复用给其他入口。
- 不卸载软件，不自动换源。
- 更新入口显示“原软件源不可用”；用户可重新添加同一入口并确认恢复原订阅身份，或显式迁移。
- 修改入口 URL 是订阅设置变更，不生成新身份，但必须重新预览域名和信任信息；若密钥变化仍按换钥流程重新确认。

---

## 6. 订阅、同步与回滚保护

### 6.1 添加源

1. 用户粘贴 `source.json` 的 HTTPS 地址。
2. 客户端下载到临时区域，执行重定向、内容类型、大小、JSON 深度和 Schema 校验。
3. 若有签名，展示发布者指纹与验证状态；若无签名，展示明确警告。
4. 展示源名称、入口域名、软件数、请求权限和信任状态。
5. 用户确认后生成 `SourceInstanceId` 并首次同步；仅“添加源”不会启动任何安装。

官方源的 URL、公钥和 `SourceInstanceId` 由客户端内置，不允许第三方源获得“官方”徽标。

### 6.2 刷新源

```text
条件请求 source.json（ETag / Last-Modified）
  → 验签和 revision 单调检查
  → 下载 index 到临时缓存
  → 核对 size + SHA-256 + Schema
  → 下载 index 引用的全部详情并逐个核对 size + SHA-256 + Schema
  → 在 SQLite 暂存表导入
  → 单事务切换 active_revision
  → 提交后清理旧临时文件
```

任何一步失败都不得污染当前活动快照。错误状态记录在源上，浏览继续使用旧数据。v1 不应用差量补丁；源规模证明确有需要后再增加增量协议。

### 6.3 刷新频率

- 默认每 24 小时后台检查一次，启动后随机延迟，避免所有客户端同时请求。
- 用户手动刷新不受日周期限制，但单源至少间隔 60 秒。
- 支持 `ETag`/`If-None-Match`；304 不重新导入。
- 连续失败采用指数退避，最大 24 小时；设置页始终允许手动重试。

---

## 7. 信任模型

| 等级 | 条件 | UI 与能力 |
|---|---|---|
| Official | 客户端内置源 + 内置公钥验签成功 | “官方源”徽标；仍不绕过安装包校验 |
| Verified | 第三方源签名有效，且用户已确认公钥指纹 | “签名已验证”；密钥变化时暂停刷新 |
| Unsigned | 没有源签名 | 显示警告；允许浏览；安装前再次标明来源 |
| Invalid | 签名错误、revision 回滚、摘要不符或 Schema 非法 | 拒绝新快照，继续旧缓存 |

签名文件 `source.json.sig` 使用 Ed25519 对 `source.json` **原始 UTF-8 字节**签名，避免 JSON 规范化歧义。`source.json` 内的 SHA-256 再逐层绑定索引和详情。

第三方源首次出现的公钥不能仅凭同一服务器提供就自动可信：客户端采用用户核对指纹的 TOFU 流程，或允许用户预先粘贴公钥/指纹。公钥变化不是普通更新，必须暂停同步并重新确认。一个 `SourceInstanceId` 一旦固定为 `Official` 或 `Verified`，后续响应缺少 `signing`、缺少 `.sig` 或验签失败都属于 `Invalid`，不得自动降级为 `Unsigned`，也不得激活新 revision；只有首次添加时本来就是未签名源，用户才可选择 `Unsigned`。官方源密钥轮换需要客户端内置的旧密钥签署轮换声明，或随客户端版本更新。

源签名不代表其中软件安全；安装包仍按 `01`/`06`/`07` 的哈希、Authenticode、发布者和提权策略独立判定。

---

## 8. 客户端界面

设置页新增“软件源”：

- 列表字段：名称、入口域名、信任等级、软件数、活动 revision、上次成功时间、当前错误。
- 操作：添加、启用/禁用、刷新、查看详情、移除。
- 默认源可以禁用，但不能伪装或被第三方条目覆盖。
- 移除前说明“不会卸载已安装软件，但将停止从该源获取更新”。
- 详情页、搜索结果、安装确认页和已安装页都显示来源。
- 同一软件来自多个源时提供“查看其他来源”，不提供静默自动切换。

---

## 9. 官方源的 GitHub Actions 流程

```text
定时或手动触发
  → 读取策展清单与上次快照
  → 查询 GitHub Release / winget / Scoop 等候选数据
  → 按明确规则匹配 Windows 资产
  → 计算哈希并运行 Schema、URL、重复 ID、安全策略检查
  → 生成候选快照与变更摘要
  → 高风险变化转人工 PR 审核
  → 上传不可变快照
  → 签名并更新 source.json
```

可自动接受：Stars、描述、发布时间等非执行字段变化，以及规则明确且哈希已计算的新版本。

必须人工审核：新增软件、仓库 owner 变化、安装器类型或执行参数变化、签名发布者变化、下载域名变化、权限从 user 变 machine、已有版本资产被替换。

Scoop、winget 和 GitHub API 都是候选信息来源，不直接成为客户端的信任根。生成器必须把每个关键字段的来源记录进构建报告，以便审计。

---

## 10. 资源限制与解析规则

v1 默认限制，可在后续基准测试后调整：

| 项 | 上限 |
|---|---:|
| `source.json` | 256 KiB |
| `index.json` 压缩后/解压后 | 10 MiB / 50 MiB |
| 单个详情文件 | 1 MiB |
| 单源应用数 | 20,000 |
| JSON 嵌套深度 | 32 |
| 单字符串 | 64 KiB；名称、摘要等字段使用更小的字段级限制 |
| 重定向 | 5 次，且每一跳必须为 HTTPS |

解析使用 `System.Text.Json` 源生成上下文和严格 Schema 校验：拒绝重复关键字段、非有限数值、非法 Unicode、未知关键枚举和超限集合。扩展字段只能放入显式 `extensions` 对象；客户端忽略不认识的扩展，但绝不执行其中内容。

所有文本以纯文本呈现；图标和截图走独立的媒体下载策略，不允许 `data:`、`file:`、SVG 脚本或远程 XAML。

---

## 11. 版本兼容

- `schemaVersion` 是协议大版本；客户端只接受明确支持的版本。
- 新增可选字段不提升大版本；改变字段语义或移除字段必须提升大版本。
- 客户端拒绝未知大版本，但保存入口 URL和旧活动快照，并提示“需要更新 GitHubStore 才能读取此源”。
- `revision` 只表示同一源的发布顺序，不能与 `schemaVersion` 或应用版本比较。
- 源维护者不得重写已发布 revision；客户端发现同 revision 的字节/摘要变化时按篡改处理。

---

## 12. 测试与验收

P0 用例：

1. 首次添加有效签名源、未签名源和签名错误源；已验证源后续缺少 `signing`/`.sig` 时拒绝降级。
2. 相对 URL、重定向、HTTP 降级、私网地址和超大响应。
3. revision 正常递增、回滚、相同 revision 内容变化和密钥轮换。
4. 任一索引/详情未下载或摘要不符时不得激活；覆盖下载中断、JSON 截断、重复字段和解压炸弹。
5. 导入中崩溃后仍使用旧快照，重启能清理暂存状态。
6. 两个源声明相同 `id`/`PackageId` 时不会互相覆盖。
7. 禁用和移除源不会删除安装记录或触发卸载。
8. 已安装应用不会因其他源版本更高而自动换源。
9. JSON 中出现命令、脚本、未知安装器类型或危险 URL 时拒绝导入/执行。
10. 源签名有效但安装包哈希或 Authenticode 失败时，安装仍被阻止。

---

## 13. 待验证与风险

| ID | 项目 | 验证方式 | 目标阶段 |
|---|---|---|---|
| TBD-10-1 | 500/5,000/20,000 条索引的下载、解析和 SQLite 导入耗时 | 真实 JSON 基准，记录峰值内存与 P95 | M1 |
| TBD-10-2 | GitHub Pages、raw.githubusercontent.com 与 Release asset 的 ETag/缓存行为 | 条件请求实测 | M1 |
| TBD-10-3 | .NET Ed25519 API/依赖与密钥轮换实现 | 签名向量和篡改测试 | M1 |
| TBD-10-4 | 第三方源 TOFU 指纹交互是否易理解 | 可用性测试 | M2 |
| TBD-10-5 | 私网地址/重定向检查在代理和 IPv6 下的正确性 | SSRF 测试矩阵 | M2 |

| 风险 | 影响 | 缓解 |
|---|---|---|
| 第三方源发布恶意安装包 | 高 | 来源警示、源签名、资产校验、执行策略不可由源放宽 |
| 源仓库或发布密钥被攻陷 | 高 | 不可变 revision、回滚保护、密钥轮换、安装包独立发布者校验 |
| 同一软件跨源冲突 | 中 | 复合身份、安装绑定、切源显式确认 |
| 快照过大或解析炸弹 | 中 | 下载/解压/深度/数量限制，临时区解析 |
| GitHub API/winget/Scoop 字段漂移 | 中 | 仅作为生成器输入，Schema 门禁和人工审核高风险变化 |

---

## 14. 与其他章节的接口

- `00`：FR-6 软件源订阅、同步和离线验收。
- `01`：`CatalogSource`、`AppId`（`SourceInstanceId + PackageId`）、`ICatalogSourceService` 与 SQLite 表。
- `04`：设置页“软件源”及各页面来源标识。
- `05`：GitHub API、winget、Scoop 是官方源生成与元数据刷新输入；本协议负责客户端目录分发。
- `06`：安装计划绑定来源、revision、installerId 和哈希；源不能绕过能力探测。
- `07`：源签名、TOFU、回滚保护和不可信 JSON 边界。
- `08`：协议解析、事务切换、冲突、签名和安全测试。
- `09`：ADR-012 与路线图。
