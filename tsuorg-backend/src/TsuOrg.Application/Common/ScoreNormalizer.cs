namespace TsuOrg.Application.Common;

/// <summary>
/// CALSV returns Layer 3 confidence on 0–1 and OCR token scores on 0–100.
/// Persist and display as integer percent (0–100).
/// </summary>
public static class ScoreNormalizer
{
    public static int ToPercent(decimal? value)
    {
        if (value is null) return 0;
        var v = value.Value;
        if (v < 0) return 0;
        var pct = v > 1m ? v : v * 100m;
        if (pct > 100m) pct = 100m;
        return (int)Math.Round(pct);
    }

    public static int? ToPercentOrNull(decimal? value) =>
        value is null ? null : ToPercent(value);

    public static decimal ToUnit(decimal value)
    {
        if (value < 0) return 0m;
        var unit = value > 1m ? value / 100m : value;
        return unit > 1m ? 1m : unit;
    }
}
