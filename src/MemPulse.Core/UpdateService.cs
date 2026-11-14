using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MemPulse.Core;

public sealed record AvailableUpdate(Version Version, string Tag, Uri InstallerUrl, byte[] Sha256, Uri ReleaseUrl);

public sealed class UpdateService
{
    private const string LatestApi = "https://api.github.com/repos/rkfsociety/MemPulse/releases/latest";
    private const string InstallerName = "MemPulse-Setup-x64.exe";
    private const string InstallerSha256Name = InstallerName + ".sha256";
    private const string Sha256SumsName = "SHA256SUMS.txt";
    private const int MaxChecksumFileBytes = 64 * 1024;
    private const int MaxRetryAttempts = 3;
    private const int BufferSize = 81920;
    private const long MaxInstallerBytes = 100L * 1024 * 1024;
    private const int ProgressReportIntervalMs = 200;

    private static readonly HttpClient Client = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = true })
        {
            Timeout = TimeSpan.FromMinutes(5)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MemPulse-Updater/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

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

    public static bool IsExpectedAssetUrl(Uri uri, string tag) =>
        IsExpectedAssetUrl(uri, tag, InstallerName);

    public static bool IsExpectedAssetUrl(Uri uri, string tag, string assetName) =>
        uri.Scheme == Uri.UriSchemeHttps &&
        uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) &&
        uri.AbsolutePath.Equals($"/rkfsociety/MemPulse/releases/download/{tag}/{assetName}", StringComparison.Ordinal);

    public static bool TryParseSha256Digest(string? digest, out byte[] sha256)
    {
        sha256 = [];
        if (string.IsNullOrWhiteSpace(digest) ||
            !Regex.IsMatch(digest, @"^sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
            return false;
        sha256 = Convert.FromHexString(digest[7..]);
        return true;
    }

    public static bool TryParseSha256Sums(string? content, string assetName, out byte[] sha256)
    {
        sha256 = [];
        if (string.IsNullOrWhiteSpace(content))
            return false;

        foreach (string rawLine in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (Regex.IsMatch(line, @"^[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
            {
                sha256 = Convert.FromHexString(line);
                return true;
            }

            var match = Regex.Match(line, @"^([a-fA-F0-9]{64})\s+\*?(.+)$", RegexOptions.CultureInvariant);
            if (!match.Success)
                continue;

            string fileName = Path.GetFileName(match.Groups[2].Value.Trim());
            if (string.Equals(fileName, assetName, StringComparison.Ordinal))
            {
                sha256 = Convert.FromHexString(match.Groups[1].Value);
                return true;
            }
        }
        return false;
    }

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

        Uri? installerUrl = null;
        string? installerDigest = null;
        Uri? installerSha256Url = null;
        Uri? sha256SumsUrl = null;

        foreach (var asset in assets.EnumerateArray())
        {
            string? name = asset.GetProperty("name").GetString();
            string? url = asset.GetProperty("browser_download_url").GetString();

            if (string.Equals(name, InstallerName, StringComparison.Ordinal))
            {
                if (!Uri.TryCreate(url, UriKind.Absolute, out var parsedUrl) ||
                    !IsExpectedAssetUrl(parsedUrl, tag!, InstallerName))
                    throw new InvalidDataException("Некорректная ссылка на установщик релиза.");
                installerUrl = parsedUrl;
                installerDigest = asset.TryGetProperty("digest", out var d) ? d.GetString() : null;
            }
            else if (string.Equals(name, InstallerSha256Name, StringComparison.Ordinal) &&
                     Uri.TryCreate(url, UriKind.Absolute, out var shaUrl) &&
                     IsExpectedAssetUrl(shaUrl, tag!, InstallerSha256Name))
            {
                installerSha256Url = shaUrl;
            }
            else if (string.Equals(name, Sha256SumsName, StringComparison.Ordinal) &&
                     Uri.TryCreate(url, UriKind.Absolute, out var sumsUrl) &&
                     IsExpectedAssetUrl(sumsUrl, tag!, Sha256SumsName))
            {
                sha256SumsUrl = sumsUrl;
            }
        }

        if (installerUrl is null)
            return null;

        byte[] sha256;
        if (!string.IsNullOrWhiteSpace(installerDigest))
        {
            if (!TryParseSha256Digest(installerDigest, out sha256))
                throw new InvalidDataException("Некорректный формат контрольной суммы SHA-256 в поле digest релиза.");
        }
        else
        {
            sha256 = await ResolveFallbackSha256Async(installerSha256Url, sha256SumsUrl, cancellationToken);
        }

        return new AvailableUpdate(
            version,
            tag!,
            installerUrl,
            sha256,
            new Uri($"https://github.com/rkfsociety/MemPulse/releases/tag/{tag}"));
    }

    private static async Task<byte[]> ResolveFallbackSha256Async(
        Uri? installerSha256Url,
        Uri? sha256SumsUrl,
        CancellationToken cancellationToken)
    {
        foreach (var candidate in new[] { installerSha256Url, sha256SumsUrl })
        {
            if (candidate is null) continue;
            string text = await DownloadSmallTextAsync(candidate, cancellationToken);
            if (TryParseSha256Sums(text, InstallerName, out var sha256))
                return sha256;
        }

        throw new InvalidDataException(
            "В релизе отсутствует контрольная сумма SHA-256 (поле digest и файл SHA256SUMS.txt).");
    }

    private static async Task<string> DownloadSmallTextAsync(Uri url, CancellationToken cancellationToken)
    {
        using var response = await ExecuteWithRetryAsync(() =>
            Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken));
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxChecksumFileBytes)
            throw new InvalidDataException("Файл контрольной суммы слишком велик.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        char[] buffer = new char[MaxChecksumFileBytes + 1];
        int read = await reader.ReadBlockAsync(buffer.AsMemory(), cancellationToken);
        if (read > MaxChecksumFileBytes)
            throw new InvalidDataException("Файл контрольной суммы слишком велик.");
        return new string(buffer, 0, read);
    }

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
