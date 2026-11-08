using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using SkiaSharp;

namespace DynamicIsland.Features;

/// <summary>
/// Полный XML уведомления из базы центра уведомлений Windows. Слушатель отдаёт только тексты,
/// а аватар отправителя и аргументы кнопок лежат в этой базе.
/// </summary>
internal static partial class ToastPayload
{
    private static string DbDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications");

    /// <summary>XML уведомления по его номеру; null — не нашли.</summary>
    private static bool _initialized;

    public static string? Read(uint id)
    {
        if (!_initialized)
        {
            SQLitePCL.Batteries_V2.Init();
            _initialized = true;
        }

        var temp = Path.Combine(Path.GetTempPath(), "dynamicisland_wpn_" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(temp);
            foreach (var name in new[] { "wpndatabase.db", "wpndatabase.db-wal", "wpndatabase.db-shm" })
            {
                var from = Path.Combine(DbDirectory, name);
                var to = Path.Combine(temp, name);
                if (!File.Exists(from)) { if (File.Exists(to)) File.Delete(to); continue; }

                // База открыта системой: читаем копию, не мешая службе уведомлений.
                using var input = new FileStream(from, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var output = File.Create(to);
                input.CopyTo(output);
            }

            using var connection = new SqliteConnection($"Data Source={Path.Combine(temp, "wpndatabase.db")};Mode=ReadOnly;Pooling=False");
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Payload FROM Notification WHERE Id = $id ORDER BY ArrivalTime DESC LIMIT 1";
            command.Parameters.AddWithValue("$id", (long)id);
            if (command.ExecuteScalar() is not byte[] bytes) return null;
            return System.Text.Encoding.UTF8.GetString(bytes).TrimStart('\uFEFF');
        }
        catch (Exception ex)
        {
            Log.Write("База уведомлений: " + ex.Message);
            return null;
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { /* временная копия */ }
        }
    }

    [GeneratedRegex("appLogoOverride[^>]*?src=\"([^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex Logo();

    /// <summary>Аватар отправителя, если приложение его приложило.</summary>
    public static SKImage? Avatar(string xml)
    {
        try
        {
            var m = Logo().Match(xml);
            if (!m.Success) return null;

            var src = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
            var path = src.StartsWith("file:///", StringComparison.OrdinalIgnoreCase)
                ? Uri.UnescapeDataString(src[8..]).Replace('/', '\\')
                : src;
            if (!File.Exists(path)) return null;

            using var data = SKData.Create(path);
            using var bitmap = SKBitmap.Decode(data);
            return bitmap is null ? null : SKImage.FromBitmap(bitmap);
        }
        catch { return null; }
    }
}
