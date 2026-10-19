using System.Text;

namespace MediaWorkbench.Infrastructure;

/// <summary>Two bounded current logs; the two bounded legacy logs migrated by SettingsStore are retained separately.</summary>
internal sealed class StartupLog
{
    internal const int MaximumBytes = 1 << 20;
    private byte[]? _pending;

    internal void Record(Exception exception)
    {
        byte[] entry = Encoding.UTF8.GetBytes($"{DateTimeOffset.UtcNow:O}\n{exception}\n\n");
        _pending = entry.Length <= 16384 ? entry : Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(entry.AsSpan(0, 16380)) + "\n");
    }

    internal void Flush(string? root)
    {
        if (_pending is null || root is null) return;
        try
        {
            SettingsStore.RequireExistingRoot(root);
            string folder = SettingsStore.StartupLogs(root);
            SettingsStore.RefuseLinks(folder);
            Directory.CreateDirectory(folder);
            string file = Path.Combine(folder, "startup.log"), previous = Path.Combine(folder, "startup.old.log");
            SettingsStore.RefuseLinks(file); SettingsStore.RefuseLinks(previous);
            if (File.Exists(file) && new FileInfo(file).Length + _pending.Length > MaximumBytes)
            {
                SettingsStore.AtomicWrite(previous, ReadTail(file)!);
                File.Delete(file);
            }
            using (var stream = new FileStream(file, FileMode.Append, FileAccess.Write, FileShare.Read)) stream.Write(_pending);
            _pending = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { }
    }

    internal static byte[]? ReadTail(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            stream.Position = Math.Max(0, stream.Length - MaximumBytes);
            byte[] bytes = new byte[stream.Length - stream.Position];
            stream.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
}
