using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace MemPulse.Core;

public sealed record AvailableUpdate(Version Version, string Tag, Uri InstallerUrl, byte[] Sha256, Uri ReleaseUrl);

public sealed class UpdateService
{
    // Constants
    private const string LatestApi = "https://api.github.com/repos/rkfsociety/MemPulse/releases/latest";
    private const string InstallerName = "MemPulse-Setup-x64.exe";
    private const int MaxRetryAttempts = 3;
    private const int BufferSize = 81920;
    private const long MaxInstallerBytes = 100L * 1024 * 1024; // 100 MiB
    private const int ProgressReportIntervalMs = 200;

    // HttpClient (singleton)
    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            // Increased timeout to accommodate large installer downloads (up to 100 MiB).
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MemPulse-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    // Helper – retry with exponential back‑off
    private static async Task<T> ExecuteWithRetryAsync<T>(Func<Task<T>> operation)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return await operation();
            }
            catch (HttpRequestException) when (attempt < MaxRetryAttempts)
            {
                attempt++;
                StartupLog.Warn($"Повторная попытка запроса обновления #{attempt}");
                var delay = TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay);
            }
        }
    }

    // Current version (from assembly)
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

    // Parse release tag
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

    // Verify asset URL
    public static bool IsExpectedAssetUrl(Uri uri, string tag) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Equals($"/rkfsociety/MemPulse/releases/download/{tag}/{InstallerName}", StringComparison.Ordinal);

    // Check for newer release
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken cancellationToken = default)
    {
        using var response = await ExecuteWithRetryAsync(() =>
            Client.GetAsync(LatestApi, HttpCompletionOption.ResponseHeadersRead, cancellationToken));
        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        if (root.GetProperty("draft").GetBoolean() ||
            root.GetProperty("prerelease").GetBoolean())
            return null;

        string? tag = root.GetProperty("tag_name").GetString();
        if (!TryParseReleaseTag(tag, out var version) || version <= CurrentVersion)
            return null;

        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var asset in assets.EnumerateArray())
        {
            if (!string.Equals(asset.GetProperty("name").GetString(), InstallerName, StringComparison.Ordinal))
                continue;

            string? url = asset.GetProperty("browser_download_url").GetString();
            string? digest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;

            if (!Uri.TryCreate(url, UriKind.Absolute, out var installerUrl) ||
                !IsExpectedAssetUrl(installerUrl, tag!) ||
                digest is null ||
                !Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
                throw new InvalidDataException("Некорректная ссылка на установщик или контрольная сумма SHA-256 релиза.");

            return new AvailableUpdate(
                version,
                tag!,
                installerUrl,
                Convert.FromHexString(digest[7..]),
                new Uri($"https://github.com/rkfsociety/MemPulse/releases/tag/{tag}"));
        }

        return null;
    }

    // Download installer with progress throttling and backup
    public async Task<string> DownloadAsync(AvailableUpdate update, IProgress<double>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!IsExpectedAssetUrl(update.InstallerUrl, update.Tag) || update.Sha256.Length != 32)
            throw new InvalidDataException("Некорректные сведения об обновлении.");

        string folder = Path.Combine(Path.GetTempPath(), "MemPulse", "Updates");
        Directory.CreateDirectory(folder);
        string destination = Path.Combine(folder, InstallerName);
        string staging = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".download");

        try
        {
            using var response = await ExecuteWithRetryAsync(() =>
                Client.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken));
            response.EnsureSuccessStatusCode();

            var declared = response.Content.Headers.ContentLength;
            if (declared > MaxInstallerBytes)
                throw new InvalidDataException("Размер обновления превышает допустимый предел.");

            await using var network = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(staging, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, BufferSize, FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
            long downloaded = 0;
            var lastReport = Stopwatch.GetTimestamp();

            try
            {
                int count;
                while ((count = await network.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    downloaded += count;
                    if (downloaded > MaxInstallerBytes)
                        throw new InvalidDataException("Размер обновления превышает допустимый предел.");

                    sha.AppendData(buffer, 0, count);
                    await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken);

                    if (declared > 0 && progress != null)
                    {
                        var now = Stopwatch.GetTimestamp();
                        var elapsedMs = (now - lastReport) * 1000 / Stopwatch.Frequency;
                        if (elapsedMs >= ProgressReportIntervalMs)
                        {
                            progress.Report(Math.Clamp((double)downloaded / declared.Value * 100, 0, 100));
                            lastReport = now;
                        }
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }

            await output.FlushAsync(cancellationToken);
            await output.DisposeAsync();
            if (!CryptographicOperations.FixedTimeEquals(sha.GetHashAndReset(), update.Sha256))
                throw new InvalidDataException("Контрольная сумма обновления не совпадает.");
            progress?.Report(100);

            // Backup old installer if it exists
            if (File.Exists(destination))
            {
                var backup = destination + ".old";
                File.Copy(destination, backup, true);
            }
            File.Move(staging, destination, true);
            return destination;
        }
        finally
        {
            if (File.Exists(staging))
                File.Delete(staging);
        }
    }
}
