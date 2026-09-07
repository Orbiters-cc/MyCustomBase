#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

public static partial class MCBPerformance
{
    static async Task<MCBPerformanceProfile> CalibrateAsync(MCBPerformanceProfile result, CancellationToken cancel)
    {
        string startedKey = preferenceKey;
        try {
            if (Expired(result.cpuMeasuredUtc, 14)) {
                Status = "Measuring compression in the background";
                string[] codecs = new[] { MCBCompression.Lz4, MCBCompression.Zstd }.Where(MCBCompression.IsSupported).ToArray();
                result.codecs = await Task.Factory.StartNew(() => MeasureCodecs(codecs, cancel), cancel,
                    TaskCreationOptions.LongRunning, TaskScheduler.Default);
                result.cpuMeasuredUtc = DateTime.UtcNow.ToString("O");
            }
            cancel.ThrowIfCancellationRequested();
            if (Expired(result.networkMeasuredUtc, 1)) {
                Status = "Measuring download speed in the background";
                await PrepareProbesAsync(cancel);
                var network = new List<MCBTimingSample>();
                await MeasureNetworkAsync(MeasureProbeAsync, sample => {
                    network.Add(sample); result.network = network;
                    result.networkMeasuredUtc = DateTime.UtcNow.ToString("O");
                }, cancel);
            }
        } catch (OperationCanceledException) {
            // Retain a completed CPU measurement when real work interrupts the network probe.
        } catch (Exception) {
            // Optional measurement failures never prevent opening MCB or applying a version.
        }
        if (startedKey == preferenceKey && ShareMeasurements && result.codecs.Count > 0 && !Expired(result.cpuMeasuredUtc, 14) && !Expired(result.networkMeasuredUtc, 1))
            reports.Enqueue(new { kind = "calibration", schema = 1, clientId, unity = unityVersion,
                platform, cores, memoryGB, codecs = result.codecs, network = result.network });
        return result;
    }

    public static async Task MeasureNetworkAsync(Func<int, CancellationToken, Task<MCBTimingSample>> measure,
        Action<MCBTimingSample> measured, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        var first = await measure(25, cancel);
        measured(first);
        if (first.milliseconds <= 2000) {
            cancel.ThrowIfCancellationRequested();
            measured(await measure(100, cancel));
        }
    }

    public static List<MCBCodecCalibration> MeasureCodecs(string[] codecs, CancellationToken cancel)
    {
        var thread = Thread.CurrentThread;
        var oldPriority = thread.Priority;
        try {
            thread.Priority = ThreadPriority.BelowNormal;
            var result = codecs.Select(c => new MCBCodecCalibration { codec = c }).ToList();
            // Exclude native library initialization and JIT from the measured samples.
            var warmup = CreateCalibrationPayload(1_000_000, cancel);
            foreach (var codec in result) MCBCompression.Decode(MCBCompression.Encode(warmup, codec.codec, cancel), codec.codec, cancel);
            foreach (int size in new[] { 50_000_000, 150_000_000 }) {
                byte[] dummy = CreateCalibrationPayload(size, cancel);
                string expected;
                using (var sha = MCBHashing.CreateSha256()) expected = Convert.ToBase64String(sha.ComputeHash(dummy));
                foreach (var codec in result) {
                    cancel.ThrowIfCancellationRequested();
                    var watch = Stopwatch.StartNew();
                    byte[] compressed = MCBCompression.Encode(dummy, codec.codec, cancel);
                    watch.Stop(); codec.encode.Add(new MCBTimingSample { bytes = size, milliseconds = watch.Elapsed.TotalMilliseconds });
                    watch.Restart();
                    byte[] decoded = MCBCompression.Decode(compressed, codec.codec, cancel);
                    watch.Stop(); codec.decode.Add(new MCBTimingSample { bytes = size, milliseconds = watch.Elapsed.TotalMilliseconds });
                    using (var sha = MCBHashing.CreateSha256())
                        if (Convert.ToBase64String(sha.ComputeHash(decoded)) != expected) throw new InvalidOperationException("Codec calibration roundtrip failed.");
                }
            }
            return result;
        } finally { thread.Priority = oldPriority; }
    }

    public static byte[] CreateCalibrationPayload(int length, CancellationToken cancel = default)
    {
        // Repeatable mix of sparse float-sized values, indices and noisy bytes; no avatar data.
        var bytes = new byte[length];
        uint state = 0x9e3779b9;
        for (int i = 0; i < length; i += 4) {
            if ((i & 0xfffff) == 0) cancel.ThrowIfCancellationRequested();
            state ^= state << 13; state ^= state >> 17; state ^= state << 5;
            uint value = i % 64 < 24 ? 0u : (i % 64 < 40 ? (uint)(i / 12) : (state & 0x807fffff) | 0x3f000000);
            for (int b = 0; b < 4 && i + b < length; b++) bytes[i + b] = (byte)(value >> (8 * b));
        }
        return bytes;
    }
}
#endif
