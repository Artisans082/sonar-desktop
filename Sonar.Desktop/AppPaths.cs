using System.IO;

namespace Sonar;

/// <summary>应用数据目录：与 Python 版保持一致，全部放在 ~/.sonar 下。</summary>
public static class AppPaths
{
    public static string HomeDir =>
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>配置与归档的根目录（~/.sonar）。</summary>
    public static string DataDir => Path.Combine(HomeDir, ".sonar");

    public static string ConfigFile => Path.Combine(DataDir, "config.json");

    /// <summary>历史归档目录（~/.sonar/history）。</summary>
    public static string HistoryDir => Path.Combine(DataDir, "history");

    /// <summary>旧版配置位置（程序目录），仅用于迁移。</summary>
    public static string LegacyConfigFile => Path.Combine(AppContext.BaseDirectory, "config.json");

    public static string ErrorLog => Path.Combine(DataDir, "error.log");

    public static void EnsureDataDir()
    {
        Directory.CreateDirectory(DataDir);
    }
}
