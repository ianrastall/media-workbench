using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using MediaWorkbench.Core;

namespace MediaWorkbench.Infrastructure;

/// <summary>
/// Finds newer releases on GitHub and downloads a checksum-verified installer. It never installs on its own:
/// the caller starts the installer only after the user chooses to update.
/// </summary>
public sealed partial class UpdateService(HttpClient http, string currentVersion) : IUpdateService
{
    public const string ReleasesApi = "https://api.github.com/repos/ianrastall/media-workbench/releases?per_page=20";
    private const string DownloadPrefix = "https://github.com/ianrastall/media-workbench/releases/download/";

    public string CurrentVersion { get; } = currentVersion;

    public static string ApplicationVersion() =>
        (typeof(UpdateService).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0")
            .Split('+')[0]; // The SDK appends "+<commit>"; that is not part of the release version.

    public static HttpClient CreateClient(string version)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"MediaWorkbench/{version}");
        return client;
    }

    public async Task<UpdateInfo?> CheckAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await http.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var releases = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token), cancellationToken: timeout.Token);
        return SelectUpdate(releases.RootElement, CurrentVersion);
    }

    /// <summary>The newest published release above <paramref name="current"/> that carries an installer and its checksum.</summary>
    public static UpdateInfo? SelectUpdate(JsonElement releases, string current)
    {
        bool currentIsPrerelease = current.Contains('-');
        UpdateInfo? best = null;
        foreach (var release in releases.EnumerateArray())
        {
            if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
            // Stable installs are offered only stable releases; pre-release installs follow pre-releases too.
            if (!currentIsPrerelease && release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) continue;
            string tag = release.GetProperty("tag_name").GetString() ?? "";
            if (!tag.StartsWith('v') || !Semver().IsMatch(tag[1..])) continue;
            string version = tag[1..];
            if (CompareVersions(version, current) <= 0 || (best is not null && CompareVersions(version, best.Version) <= 0)) continue;
            string installer = $"MediaWorkbench-{version}-win-x64-setup.exe";
            string? installerUrl = null, checksumUrl = null;
            foreach (var asset in release.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? "";
                string url = asset.GetProperty("browser_download_url").GetString() ?? "";
                if (!url.StartsWith(DownloadPrefix + tag + "/", StringComparison.Ordinal)) continue;
                if (name == installer) installerUrl = url;
                else if (name == installer + ".sha256") checksumUrl = url;
            }
            if (installerUrl is null || checksumUrl is null) continue;
            best = new UpdateInfo(version, release.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                installerUrl, checksumUrl, release.GetProperty("html_url").GetString() ?? "");
        }
        return best;
    }

    public async Task<string> DownloadAsync(UpdateInfo update, IProgress<OperationProgress> progress, CancellationToken cancellationToken)
    {
        string name = $"MediaWorkbench-{update.Version}-win-x64-setup.exe";
        if (!Semver().IsMatch(update.Version) || !update.InstallerUrl.StartsWith(DownloadPrefix, StringComparison.Ordinal))
            throw new InvalidDataException("The update does not come from Media Workbench's release page.");
        string checksumText = await http.GetStringAsync(update.ChecksumUrl, cancellationToken);
        var match = Checksum().Match(checksumText);
        if (!match.Success || match.Groups[2].Value != name) throw new InvalidDataException("The update's checksum file is not in the expected format.");
        string expected = match.Groups[1].Value.ToLowerInvariant();

        string folder = Housekeeping.UpdateFolder; // Emptied by Housekeeping once the new version starts.
        Directory.CreateDirectory(folder);
        string target = Path.Combine(folder, name), partial = target + ".partial";
        try
        {
            using (var response = await http.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                long total = response.Content.Headers.ContentLength ?? 0, completed = 0;
                await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
                var buffer = new byte[1 << 16];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    completed += read;
                    progress.Report(new("Downloading Media Workbench " + update.Version, completed, total, "bytes"));
                }
            }
            string actual;
            await using (var written = File.OpenRead(partial)) actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(written, cancellationToken));
            if (actual != expected) throw new InvalidDataException("The downloaded update did not match its published checksum, so it was discarded.");
            File.Move(partial, target, overwrite: true);
            return target;
        }
        finally
        {
            File.Delete(partial);
        }
    }

    /// <summary>Semantic-version precedence: numeric parts, then pre-release identifiers (a release outranks its pre-releases).</summary>
    public static int CompareVersions(string left, string right)
    {
        static (int[] Core, string[] Pre) Parse(string value)
        {
            string[] parts = value.Split('+')[0].Split('-', 2);
            return (parts[0].Split('.').Select(int.Parse).ToArray(), parts.Length > 1 ? parts[1].Split('.') : []);
        }
        var (a, b) = (Parse(left), Parse(right));
        for (int i = 0; i < 3; i++)
            if (a.Core[i] != b.Core[i]) return a.Core[i].CompareTo(b.Core[i]);
        if (a.Pre.Length == 0 || b.Pre.Length == 0) return b.Pre.Length.CompareTo(a.Pre.Length);
        for (int i = 0; i < Math.Min(a.Pre.Length, b.Pre.Length); i++)
        {
            bool aNumber = int.TryParse(a.Pre[i], out int x), bNumber = int.TryParse(b.Pre[i], out int y);
            int result = aNumber && bNumber ? x.CompareTo(y) : aNumber ? -1 : bNumber ? 1 : string.CompareOrdinal(a.Pre[i], b.Pre[i]);
            if (result != 0) return result;
        }
        return a.Pre.Length.CompareTo(b.Pre.Length);
    }

    [GeneratedRegex(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$")]
    private static partial Regex Semver();
    [GeneratedRegex(@"^([0-9a-fA-F]{64})\s+\*?(\S+)\s*$")]
    private static partial Regex Checksum();
}
