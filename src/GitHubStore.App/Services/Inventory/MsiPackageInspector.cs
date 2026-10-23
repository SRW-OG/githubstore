using System.Runtime.Versioning;
using GitHubStore.Core.Abstractions;

namespace GitHubStore.App.Services.Inventory;

/// <summary>Directory 表的一行：只保留发现“安装根目录”所需的目录键与它的父目录键。</summary>
public sealed record MsiDirectoryRow(string Directory, string? Parent);

/// <summary>
/// 只读打开 MSI 数据库所需的最小表面。测试可以整体替换它，从而完全不依赖 COM。
/// </summary>
public interface IMsiDatabaseReader : IDisposable
{
    IReadOnlyList<MsiDirectoryRow> ReadDirectories();
    string? ReadProperty(string name);
}

/// <summary>
/// 从安装包本身读到的事实。任何一项读不到都保持 null——本类型不作任何推测：
/// 没有公开的目录属性就让调用方退回默认行为，绝不臆造属性名。
/// </summary>
public sealed record MsiPackageInspection
{
    /// <summary>数据库是否成功以只读方式打开。</summary>
    public bool Opened { get; init; }

    /// <summary>Property 表中的 ProductCode；格式不合法时同样保持 null。</summary>
    public string? ProductCode { get; init; }

    /// <summary>
    /// Directory 表中代表安装根的“公开”（全大写）目录属性名；读不到时为 null，
    /// 调用方必须据此退回“不预填、让用户在向导里选择”。
    /// </summary>
    public string? InstallRootProperty { get; init; }

    /// <summary>失败原因的稳定短码，仅用于本地诊断，不进入 UI。</summary>
    public string? FailureCode { get; init; }

    public static MsiPackageInspection Failed(string failureCode) => new() { FailureCode = failureCode };
}

/// <summary>
/// 只读检查一个 MSI 安装包，回答两个问题：“哪个公开目录属性代表安装根”和“产品码是什么”。
/// <list type="bullet">
/// <item>只用 WindowsInstaller COM（晚绑定，不引用 PIA）以只读方式打开数据库，绝不修改安装包；</item>
/// <item>遍历有硬上限，文件不是 OLE 复合文档时连 COM 都不调用；</item>
/// <item>任何失败都返回 <see cref="MsiPackageInspection.Failed"/>，绝不向安装路径抛异常；</item>
/// <item><b>绝不猜测属性名</b>：候选只能来自 Directory 表的实际行，且键必须是公开属性。</item>
/// </list>
/// 本类型不依赖 WinUI，因此可以被测试项目直接链接。
/// </summary>
public static class MsiPackageInspector
{
    /// <summary>Directory 表遍历硬上限；异常或恶意安装包不能让这里无限循环。</summary>
    private const int MaximumDirectoryRows = 20_000;

    /// <summary>单个字段/属性值的长度上限。</summary>
    private const int MaximumValueLength = 512;

    // MSI SQL 用反引号引用标识符。这里用 \u0060 转义写成常量：
    // 源码里既不出现裸反引号，也不使用字符串内插，避免任何花括号在写入路径上被处理程序吞掉。
    private const string DirectoryQuery =
        "SELECT \u0060Directory\u0060, \u0060Directory_Parent\u0060 FROM \u0060Directory\u0060";

    /// <summary>
    /// 可以作为“应用安装根”的父目录属性名。顺序即优先级，用于多个候选时的稳定取舍：
    /// 先看应用自己的目录（Program Files 的 64 位/32 位视图、当前用户应用数据），再看驱动器根 TARGETDIR。
    /// 名称来自 Windows Installer 的预定义文件夹属性。
    /// </summary>
    private static readonly string[] InstallRootParents =
    [
        "ProgramFiles64Folder", "ProgramFilesFolder", "ProgramFiles6432Folder",
        "LocalAppDataFolder", "TARGETDIR",
    ];

    /// <summary>
    /// Windows Installer 的预定义文件夹属性是操作系统位置的别名，不是某个应用的安装目录。
    /// 即使某个包把它们放在候选位置（父目录为 TARGETDIR 等）也一律不采用：
    /// 把 ProgramFiles64Folder 之类的属性交给命令行，等于重定向整个系统的程序目录。
    /// </summary>
    private static readonly HashSet<string> ReservedKeys = new(StringComparer.Ordinal)
    {
        "TARGETDIR", "ROOTDRIVE", "SOURCEDIR", "SourceDir",
        "ProgramFilesFolder", "ProgramFiles64Folder", "ProgramFiles6432Folder", "ProgramFiles86Folder",
        "CommonFilesFolder", "CommonFiles64Folder", "CommonFiles6432Folder",
        "LocalAppDataFolder", "AppDataFolder", "CommonAppDataFolder", "UserProfileFolder", "PersonalFolder",
        "WindowsFolder", "SystemFolder", "System64Folder", "System16Folder", "System32Folder",
        "DesktopFolder", "CommonDesktopFolder", "ProgramMenuFolder", "CommonProgramsFolder",
        "StartMenuFolder", "CommonStartMenuFolder", "StartupFolder", "CommonStartupFolder",
        "SendToFolder", "FontsFolder", "TemplatesFolder", "CommonTemplatesFolder",
        "FavoritesFolder", "CommonFavoritesFolder", "NetHoodFolder", "PrintHoodFolder",
        "RecentFolder", "AdminToolsFolder", "CommonAdministrativeToolsFolder", "TempFolder", "MsiHiddenProperties",
    };

    /// <summary>
    /// 只读打开安装包并读出事实。文件被占用、损坏、COM 不可用、取消，全部返回 Failed，绝不抛出。
    /// </summary>
    /// <param name="msiPath">已下载到本地的安装包路径。</param>
    /// <param name="openDatabase">测试替换用的打开实现；为 null 时使用 WindowsInstaller COM。</param>
    public static MsiPackageInspection Inspect(string? msiPath,
        Func<string, IMsiDatabaseReader?>? openDatabase = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(msiPath)) return MsiPackageInspection.Failed("msi_path_invalid");
        // 先做 8 字节头检查：不是 OLE 复合文档就不是 MSI，省掉一次 COM 调用。
        if (!HasCompoundFileHeader(msiPath)) return MsiPackageInspection.Failed("msi_not_a_package");
        IMsiDatabaseReader? reader = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            reader = (openDatabase ?? OpenDatabase)(msiPath);
            if (reader is null) return MsiPackageInspection.Failed("msi_database_unavailable");
            ct.ThrowIfCancellationRequested();
            var directories = reader.ReadDirectories();
            ct.ThrowIfCancellationRequested();
            var rawProductCode = reader.ReadProperty("ProductCode");
            ct.ThrowIfCancellationRequested();
            return new MsiPackageInspection
            {
                Opened = true,
                // 只有形如 {8-4-4-4-12} 的产品码才会被记录；其余一律当作没有产品码。
                ProductCode = TryNormalizeProductCode(rawProductCode, out var productCode) ? productCode : null,
                InstallRootProperty = SelectInstallRootProperty(directories),
            };
        }
        catch (OperationCanceledException) { return MsiPackageInspection.Failed("msi_inspection_cancelled"); }
        catch (Exception) { return MsiPackageInspection.Failed("msi_inspection_failed"); }
        finally
        {
            try { reader?.Dispose(); }
            catch (Exception) { /* 释放失败不能改变检查结论。 */ }
        }
    }

    /// <summary>
    /// 从 Directory 表各行里选出安装根属性名；选不出来就返回 null（调用方据此退回默认行为）。
    /// 判据：父目录是已知的系统安装根之一，目录键是“公开”（全大写）属性，且不是系统属性别名。
    /// </summary>
    public static string? SelectInstallRootProperty(IEnumerable<MsiDirectoryRow>? directories)
    {
        if (directories is null) return null;
        (string Key, int Rank)? best = null;
        var seen = 0;
        foreach (var row in directories)
        {
            if (++seen > MaximumDirectoryRows) break;
            if (row?.Directory is not { } key || row.Parent is not { } parent) continue;
            if (!IsPublicPropertyName(key) || ReservedKeys.Contains(key)) continue;
            var rank = Array.FindIndex(InstallRootParents,
                candidate => string.Equals(candidate, parent, StringComparison.OrdinalIgnoreCase));
            if (rank < 0) continue;
            // 多个候选时先按父目录优先级、再按目录键排序：结果稳定可复现，不依赖读取顺序。
            if (best is null || rank < best.Value.Rank
                || rank == best.Value.Rank && string.CompareOrdinal(key, best.Value.Key) < 0)
                best = (key, rank);
        }
        return best?.Key;
    }

    /// <summary>
    /// 命令行只能设置“公开”属性：MSI 规定目录键只由大写字母、数字、下划线、点组成时才是公开属性；
    /// 含小写字母、以下划线或数字开头的键是私有属性，无法从命令行设置，因此一律不接受。
    /// </summary>
    public static bool IsPublicPropertyName(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 72) return false;
        if (value[0] == '_' || char.IsAsciiDigit(value[0])) return false;
        foreach (var character in value)
        {
            var allowed = character is >= 'A' and <= 'Z' || character is >= '0' and <= '9'
                || character == '_' || character == '.';
            if (!allowed) return false;
        }
        return true;
    }

    /// <summary>
    /// 产品码必须形如花括号包裹的 8-4-4-4-12 十六进制 GUID。
    /// 判定复用 Core 里那一把唯一的尺子，避免安装侧与删除侧出现两套略有差异的规则：
    /// 不合法就返回 false，宁可没有产品码，也不把可疑文本交给 msiexec。
    /// </summary>
    public static bool TryNormalizeProductCode(string? value, out string? productCode)
    {
        productCode = null;
        if (!ProductCodeFormat.IsValid(value)) return false;
        productCode = value;
        return true;
    }

    /// <summary>按属性名构造只读查询。属性名只来自本文件的常量，并再按公开属性规则校验，绝不把外部输入拼进 SQL。</summary>
    private static string PropertyQuery(string name) =>
        "SELECT \u0060Value\u0060 FROM \u0060Property\u0060 WHERE \u0060Property\u0060='" + name + "'";

    /// <summary>MSI 是 OLE 复合文档：D0 CF 11 E0 A1 B1 1A E1。只读 8 字节，读不到就当作不是安装包。</summary>
    private static bool HasCompoundFileHeader(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read | FileShare.Delete, 8, FileOptions.SequentialScan);
            Span<byte> header = stackalloc byte[8];
            return stream.Read(header) == 8
                && header[0] == 0xD0 && header[1] == 0xCF && header[2] == 0x11 && header[3] == 0xE0
                && header[4] == 0xA1 && header[5] == 0xB1 && header[6] == 0x1A && header[7] == 0xE1;
        }
        catch (Exception) { return false; }
    }

    private static IMsiDatabaseReader? OpenDatabase(string path) =>
        OperatingSystem.IsWindows() ? OpenWindowsInstallerDatabase(path) : null;

    [SupportedOSPlatform("windows")]
    private static IMsiDatabaseReader? OpenWindowsInstallerDatabase(string path)
    {
        if (Type.GetTypeFromProgID("WindowsInstaller.Installer") is not { } type) return null;
        if (Activator.CreateInstance(type) is not { } installer) return null;
        dynamic created = installer;
        // 0 = msiOpenDatabaseModeReadOnly：只读打开，绝不修改安装包。
        // 打开失败由异常向上冒泡，最终变成“没读到”，由调用方退回默认行为。
        object? database = created.OpenDatabase(path, 0);
        return database is null ? null : new WindowsInstallerDatabase(database);
    }

    /// <summary>
    /// COM 晚绑定包装：不引用任何 PIA，只暴露本文件需要的两个只读查询。
    /// RCW 的回收交给 GC（Dispose 只断开引用），因此不调用任何显式释放 API。
    /// </summary>
    [SupportedOSPlatform("windows")]
    private sealed class WindowsInstallerDatabase : IMsiDatabaseReader
    {
        private object? _database;

        public WindowsInstallerDatabase(object database) => _database = database;

        public IReadOnlyList<MsiDirectoryRow> ReadDirectories()
        {
            var rows = new List<MsiDirectoryRow>();
            if (_database is not { } database) return rows;
            dynamic view = ((dynamic)database).OpenView(DirectoryQuery);
            // Execute 的 Record 参数可省略；传 null 即“无需参数”。
            view.Execute(null);
            // Fetch 返回 null 表示没有下一行；循环本身另有硬上限，不依赖“总会返回 null”。
            for (var index = 0; index < MaximumDirectoryRows; index++)
            {
                object? record = view.Fetch();
                if (record is null) break;
                var key = ReadString(record, 1);
                if (key is not null) rows.Add(new MsiDirectoryRow(key, ReadString(record, 2)));
            }
            return rows;
        }

        public string? ReadProperty(string name)
        {
            if (name != "ProductCode" || _database is not { } database) return null;
            dynamic view = ((dynamic)database).OpenView(PropertyQuery(name));
            view.Execute(null);
            object? record = view.Fetch();
            return record is null ? null : ReadString(record, 1);
        }

        private static string? ReadString(object record, int index)
        {
            try
            {
                // NULL 字段在部分列上会抛异常；一律当作“读不到”，由调用方决定是否采用该行/该属性。
                var text = ((dynamic)record).StringData(index) as string;
                return text is { Length: > 0 } && text.Length <= MaximumValueLength ? text : null;
            }
            catch (Exception) { return null; }
        }

        public void Dispose() => _database = null;
    }
}
