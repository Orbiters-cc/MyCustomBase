#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;

[Serializable] public sealed class MCBTimingSample
{
    public long bytes;
    public double milliseconds;
}
[Serializable] public sealed class MCBCodecCalibration
{
    public string codec;
    public List<MCBTimingSample> encode = new List<MCBTimingSample>();
    public List<MCBTimingSample> decode = new List<MCBTimingSample>();
}
[Serializable] public sealed class MCBPerformanceProfile
{
    public const int Schema = 1;
    public int schema = Schema;
    public string environment;
    public string cpuMeasuredUtc;
    public string networkMeasuredUtc;
    public List<MCBCodecCalibration> codecs = new List<MCBCodecCalibration>();
    public List<MCBTimingSample> network = new List<MCBTimingSample>();
}
public sealed class MCBDeliveryDecision
{
    public string codec;
    public bool calibrated;
    public List<MCBDeliveryEstimate> candidates = new List<MCBDeliveryEstimate>();
}
public sealed class MCBDeliveryEstimate
{
    public string codec;
    public long packageBytes;
    public long decodedBytes;
    public double predictedMilliseconds;
}

public static class MCBPerformanceModel
{
    public static double Predict(IEnumerable<MCBTimingSample> samples, long bytes)
    {
        var points = (samples ?? Enumerable.Empty<MCBTimingSample>()).Where(p => p != null
            && p.bytes > 0 && p.milliseconds > 0 && !double.IsNaN(p.milliseconds) && !double.IsInfinity(p.milliseconds)).ToArray();
        if (points.Length == 0) return double.NaN;
        double meanX = points.Average(p => (double)p.bytes), meanY = points.Average(p => p.milliseconds);
        double denominator = points.Sum(p => Math.Pow(p.bytes - meanX, 2));
        double slope = denominator > 0 ? points.Sum(p => (p.bytes - meanX) * (p.milliseconds - meanY)) / denominator : meanY / meanX;
        if (slope <= 0) slope = meanY / meanX;
        double intercept = Math.Max(0, meanY - slope * meanX);
        return intercept + Math.Max(0, bytes) * slope;
    }

    public static MCBDeliveryDecision Choose(MCBDeliveryVariant[] variants, MCBPerformanceProfile profile, Func<string, bool> supported)
    {
        var available = (variants ?? Array.Empty<MCBDeliveryVariant>()).Where(v => v != null
            && v.packageBytes > 0 && v.decodedBytes > 0 && supported(v.codec)).ToArray();
        var decision = new MCBDeliveryDecision();
        if (available.Length == 0) return decision;
        bool calibrated = profile != null && profile.network?.Count > 0;
        foreach (var variant in available) {
            var calibration = profile?.codecs?.FirstOrDefault(c => c.codec == variant.codec);
            double decode = Predict(calibration?.decode, variant.decodedBytes);
            double transfer = Predict(profile?.network, variant.packageBytes);
            if (double.IsNaN(decode) || double.IsNaN(transfer)) calibrated = false;
            decision.candidates.Add(new MCBDeliveryEstimate { codec = variant.codec, packageBytes = variant.packageBytes,
                decodedBytes = variant.decodedBytes, predictedMilliseconds = double.IsNaN(decode + transfer) ? 0 : decode + transfer });
        }
        decision.calibrated = calibrated;
        decision.codec = calibrated
            ? decision.candidates.OrderBy(c => c.predictedMilliseconds).ThenBy(c => c.packageBytes).First().codec
            : available.OrderBy(v => v.packageBytes).First().codec;
        return decision;
    }
}
#endif
