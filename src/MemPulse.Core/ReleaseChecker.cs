using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MemPulse.Core;

public sealed record ReleaseInfo(Version Version, string Tag, Uri ReleaseUrl, Uri? AssetUrl, string? AssetName);

/// <summary>Cross-platform GitHub Releases API checker for MemPulse.</summary>
public static class ReleaseChecker
{
    public const string LatestApi = "https://api.github.com/repos/rkfsociety/MemPulse/releases/latest";
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MemPulse-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static Version CurrentVersion
    {
        get
        {
            string? version = Assembly.GetEntryAssembly()
                ?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion.Split('+')[0];
            return Version.TryParse(version, out var parsed) ? parsed : new Version(0, 0, 0);
        }
    }

    public static bool TryParseReleaseTag(string? tag, out Version version)
    {
        version = new Version(0, 0, 0);
        if (tag is null ||
            !Regex.IsMatch(tag, @"^v(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$", RegexOptions.CultureInvariant))
            return false;
        if (!Version.TryParse(tag.AsSpan(1), out var parsed) || parsed is null)
            return false;
        version = parsed;
        return true;
    }

    public static bool IsExpectedAssetUrl(Uri uri, string tag, string assetName) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Equals($"/rkfsociety/MemPulse/releases/download/{tag}/{assetName}", StringComparison.Ordinal);

    public static string PreferredLinuxAssetName()
    {
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "arm64",
            _ => "x64"
        };
        return $"MemPulse-linux-{arch}.tar.gz";
    }

    public static ReleaseInfo? ParseLatestLinuxRelease(string json, Version currentVersion, string? preferredAssetName = null)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (!root.TryGetProperty("tag_name", out var tagEl) ||
            !TryParseReleaseTag(tagEl.GetString(), out var version) ||
            version <= currentVersion)
            return null;

        string tag = tagEl.GetString()!;
        var releaseUrl = root.TryGetProperty("html_url", out var htmlEl) &&
                         Uri.TryCreate(htmlEl.GetString(), UriKind.Absolute, out var parsedHtml) &&
                         parsedHtml.Scheme == Uri.UriSchemeHttps
            ? parsedHtml
            : new Uri($"https://github.com/rkfsociety/MemPulse/releases/tag/{tag}");

        Uri? assetUrl = null;
        string? matchedName = null;
        string preferred = preferredAssetName ?? PreferredLinuxAssetName();

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string? name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                string? urlStr = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(urlStr))
                    continue;
                if (!Uri.TryCreate(urlStr, UriKind.Absolute, out var candidateUri) ||
                    !IsExpectedAssetUrl(candidateUri, tag, name))
                    continue;

                if (string.Equals(name, preferred, StringComparison.OrdinalIgnoreCase))
                {
                    assetUrl = candidateUri;
                    matchedName = name;
                    break;
                }

                if (assetUrl is null &&
                    name.StartsWith("MemPulse-linux-", StringComparison.OrdinalIgnoreCase) &&
                    name.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
                {
                    assetUrl = candidateUri;
                    matchedName = name;
                }
            }
        }

        return new ReleaseInfo(version, tag, releaseUrl, assetUrl, matchedName);
    }

    public static async Task<ReleaseInfo?> CheckLatestLinuxAsync(CancellationToken cancellationToken = default)
    {
        using var response = await Client.GetAsync(LatestApi, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();
        string json = await response.Content.ReadAsStringAsync(cancellationToken);
        return ParseLatestLinuxRelease(json, CurrentVersion);
    }
}
