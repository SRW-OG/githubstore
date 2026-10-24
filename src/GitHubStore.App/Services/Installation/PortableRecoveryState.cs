using System.Security.Cryptography;
using System.Text;

namespace GitHubStore.App.Services.Installation;

/// <summary>Conservative read-only check shared by launch and recovery paths.</summary>
internal static class PortableRecoveryState
{
    internal static string JournalPath(string dataDirectory, string installId) => Path.Combine(
        Path.GetFullPath(dataDirectory), "upgrade-journal",
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(installId))).ToLowerInvariant() + ".json");

    internal static bool RequiresRecovery(string dataDirectory, string installId)
    {
        try
        {
            // Even a completed or legacy journal remains blocked until the recovery
            // service verifies and removes it. An unreadable journal is not absence.
            _ = File.GetAttributes(JournalPath(dataDirectory, installId));
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }
}
