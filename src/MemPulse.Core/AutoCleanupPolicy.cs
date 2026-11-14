namespace MemPulse.Core;

/// <summary>Shared threshold and cooldown policy for automatic memory cleanup.</summary>
public static class AutoCleanupPolicy
{
    public const int MinThreshold = 70;
    public const int MaxThreshold = 98;
    public const int MinCooldownMinutes = 5;
    public const int MaxCooldownMinutes = 120;

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
