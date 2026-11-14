namespace MemPulse.Core;

/// <summary>Shared threshold and cooldown policy for automatic memory cleanup.</summary>
public static class AutoCleanupPolicy
{
    public const int MinThreshold = 70;
    public const int MaxThreshold = 98;
    public const int MinCooldownMinutes = 5;
    public const int MaxCooldownMinutes = 120;
    public const long LowYieldThresholdBytes = 32L * 1024 * 1024;

    public static bool ShouldCleanSmart(
        double usedPercent,
        int threshold,
        int cooldownMinutes,
        DateTime utcNow,
        DateTime lastAttemptUtc,
        bool isFullscreenActive,
        bool skipFullscreen,
        long? lastFreedBytes)
    {
        if (skipFullscreen && isFullscreenActive)
            return false;

        if (!double.IsFinite(usedPercent) ||
            threshold is < MinThreshold or > MaxThreshold ||
            cooldownMinutes is < MinCooldownMinutes or > MaxCooldownMinutes ||
            usedPercent < threshold)
        {
            return false;
        }

        if (lastAttemptUtc == DateTime.MinValue)
            return true;

        int effectiveCooldown = lastFreedBytes is >= 0 and < LowYieldThresholdBytes
            ? Math.Min(MaxCooldownMinutes * 2, cooldownMinutes * 2)
            : cooldownMinutes;

        return utcNow - lastAttemptUtc >= TimeSpan.FromMinutes(effectiveCooldown);
    }

    public static bool ShouldClean(
        double usedPercent,
        int threshold,
        DateTimeOffset now,
        DateTimeOffset nextAttempt) =>
        double.IsFinite(usedPercent) &&
        threshold is >= MinThreshold and <= MaxThreshold &&
        usedPercent >= threshold &&
        now >= nextAttempt;

    public static bool ShouldClean(
        double usedPercent,
        int threshold,
        int cooldownMinutes,
        DateTime utcNow,
        DateTime lastAttemptUtc)
    {
        if (!double.IsFinite(usedPercent) ||
            threshold is < MinThreshold or > MaxThreshold ||
            cooldownMinutes is < MinCooldownMinutes or > MaxCooldownMinutes ||
            usedPercent < threshold)
        {
            return false;
        }

        if (lastAttemptUtc == DateTime.MinValue)
            return true;

        return utcNow - lastAttemptUtc >= TimeSpan.FromMinutes(cooldownMinutes);
    }
}
