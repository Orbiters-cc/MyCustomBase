#if UNITY_EDITOR
using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;

public static partial class NativeMeshPipelineBenchmark
{
    public static string StartAdaptiveDelivery(string asset, string bin, string key, int repetitions = 3)
    {
        string result = Start(asset, bin, key, repetitions);
        routine = RunAdaptiveDelivery(Math.Max(1, Math.Min(5, repetitions)));
        return result;
    }

    static IEnumerator RunAdaptiveDelivery(int repetitions)
    {
        source = UnityEditor.AssetDatabase.LoadAssetAtPath<NativeMeshPayloadAsset>(report.sourceAsset);
        if (source == null || source.payloadCompression != NativeMeshPayloadService.PayloadCompressionNone)
            throw new InvalidDataException("This comparison requires an existing uncompressed native payload fixture.");
        report.rendererCount = source.renderers.Count;
        activeWorker = Task.Run(() => {
            byte[] key = File.ReadAllBytes(report.sourceKey), bin = File.ReadAllBytes(report.sourceBin);
            byte[] plain = MCBXor.Transform(key, bin);
            report.payloadBytes = plain.Length; report.keyBytes = key.Length;
            report.payloadSha256 = FastHash(plain);
            for (int iteration = 0; iteration <= repetitions; iteration++) {
                Status = "Production XOR and codec comparison " + iteration;
                var xor = Measure("production_xor_words_4_workers", iteration, () => MCBXor.Transform(key, bin), value => value.Length);
                if (FastHash(xor) != report.payloadSha256) throw new InvalidDataException("XOR changed payload bytes."); VerifiedLast();
                foreach (string codec in iteration % 2 == 0 ? new[] { "LZ4", "ZSTD" } : new[] { "ZSTD", "LZ4" }) {
                    if (!MCBCompression.IsSupported(codec)) continue;
                    byte[] encoded = Measure("production_" + codec + "_encode", iteration, () => MCBCompression.Encode(plain, codec), value => value.Length);
                    byte[] decoded = Measure("production_" + codec + "_decode", iteration, () => MCBCompression.Decode(encoded, codec), value => value.Length);
                    if (FastHash(decoded) != report.payloadSha256) throw new InvalidDataException("Codec changed mesh bytes.");
                    VerifiedLast(); report.measurements[report.measurements.Count - 2].verified = true;
                }
            }
            Status = "Background CPU calibration on 50 and 150 MB";
            var calibration = MCBPerformance.MeasureCodecs(new[] { "LZ4", "ZSTD" }.Where(MCBCompression.IsSupported).ToArray(), CancellationToken.None);
            foreach (var codec in calibration) {
                foreach (var sample in codec.encode) report.measurements.Add(new Measurement { stage = "calibration_" + codec.codec + "_encode", iteration = 1, bytes = sample.bytes, milliseconds = sample.milliseconds, verified = true });
                foreach (var sample in codec.decode) report.measurements.Add(new Measurement { stage = "calibration_" + codec.codec + "_decode", iteration = 1, bytes = sample.bytes, milliseconds = sample.milliseconds, verified = true });
            }
            Checkpoint();
        });
        while (!activeWorker.IsCompleted) yield return null;
        activeWorker.GetAwaiter().GetResult();
    }

    static string FastHash(byte[] bytes)
    {
        using (var sha = MCBHashing.CreateSha256()) return Hex(sha.ComputeHash(bytes));
    }
}
#endif
