#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using NUnit.Framework;

public class MCBAdaptiveDeliveryTests
{
    static MCBTimingSample Point(long bytes, double ms) => new MCBTimingSample { bytes = bytes, milliseconds = ms };
    static MCBDeliveryVariant[] Variants() => new[] {
        new MCBDeliveryVariant { codec = "LZ4", packageBytes = 53_000_000, decodedBytes = 150_000_000 },
        new MCBDeliveryVariant { codec = "ZSTD", packageBytes = 26_000_000, decodedBytes = 150_000_000 }
    };
    static MCBPerformanceProfile Profile(double downloadMs) => new MCBPerformanceProfile {
        network = new List<MCBTimingSample> { Point(100_000_000, downloadMs) },
        codecs = new List<MCBCodecCalibration> {
            new MCBCodecCalibration { codec = "LZ4", decode = new List<MCBTimingSample> { Point(150_000_000, 25) } },
            new MCBCodecCalibration { codec = "ZSTD", decode = new List<MCBTimingSample> { Point(150_000_000, 114) } }
        }
    };

    [TestCase(100, "LZ4")]
    [TestCase(1000, "ZSTD")]
    public void ChoosesFastestCombinedDownloadAndDecode(double transferMs, string expected)
    {
        var decision = MCBPerformanceModel.Choose(Variants(), Profile(transferMs), _ => true);
        Assert.That(decision.codec, Is.EqualTo(expected));
        Assert.That(decision.calibrated, Is.True);
    }
    [Test] public void MissingCalibrationUsesSmallestSupportedDownload()
    {
        var decision = MCBPerformanceModel.Choose(Variants(), null, _ => true);
        Assert.That(decision.codec, Is.EqualTo("ZSTD")); Assert.That(decision.calibrated, Is.False);
        Assert.That(MCBPerformanceModel.Choose(Variants(), Profile(100), c => c == "ZSTD").codec, Is.EqualTo("ZSTD"));
        Assert.That(MCBPerformanceModel.Choose(Variants(), Profile(100), _ => false).codec, Is.Null);
    }
    [Test] public void CurveIncludesLatencyAndRejectsInvalidMeasurements()
    {
        Assert.That(MCBPerformanceModel.Predict(new[] { Point(25, 300), Point(100, 1050) }, 50), Is.EqualTo(550).Within(.001));
        Assert.That(MCBPerformanceModel.Predict(new[] { Point(0, 20), Point(10, double.NaN) }, 50), Is.NaN);
        Assert.That(MCBPerformanceModel.Predict(new[] { Point(25, 100), Point(100, 90) }, 50), Is.GreaterThan(0));
    }
    [Test] public void NativeHashMatchesManagedHashAcrossStreamingWrites()
    {
        byte[] data = MCBPerformance.CreateCalibrationPayload(1_000_013);
        using (var expected = new SHA256Managed())
        using (var native = MCBHashing.CreateSha256()) {
            var digest = expected.ComputeHash(data);
            native.TransformBlock(data, 0, 513, null, 0);
            native.TransformFinalBlock(data, 513, data.Length - 513);
            Assert.That(native.Hash, Is.EqualTo(digest));
            Assert.That(native.ComputeHash(data), Is.EqualTo(digest));
        }
    }
    [TestCase("LZ4")]
    [TestCase("ZSTD")]
    public void CodecRoundtripAcrossBlockBoundaryAndRejectsCorruption(string codec)
    {
        if (!MCBCompression.IsSupported(codec)) Assert.Ignore("Codec is not bundled for this platform.");
        byte[] input = MCBPerformance.CreateCalibrationPayload(MCBCompression.BlockBytes + 19);
        byte[] encoded = MCBCompression.Encode(input, codec);
        Assert.That(MCBCompression.Decode(encoded, codec), Is.EqualTo(input));
        Assert.Throws<InvalidDataException>(() => MCBCompression.Decode(encoded.Take(encoded.Length - 1).ToArray(), codec));
        Assert.Throws<InvalidDataException>(() => MCBCompression.Decode(encoded.Concat(new byte[] { 0 }).ToArray(), codec));
        byte[] oversized = (byte[])encoded.Clone();
        Array.Copy(BitConverter.GetBytes(int.MaxValue), 0, oversized, 4, 4);
        Assert.Throws<InvalidDataException>(() => MCBCompression.Decode(oversized, codec));
        using (var cancel = new CancellationTokenSource()) {
            cancel.Cancel();
            Assert.Throws<OperationCanceledException>(() => MCBCompression.Encode(input, codec, cancel.Token));
            Assert.Throws<OperationCanceledException>(() => MCBCompression.Decode(encoded, codec, cancel.Token));
        }
    }
    [Test] public void BothDeliveryCodecsPreserveMeshRigCacheAndReset() => MCBDeliveryHealthCheck.RunOrThrow();

    [TestCase(1999, 2)]
    [TestCase(2000, 2)]
    [TestCase(2001, 1)]
    public void LargeNetworkProbeOnlyRunsOnFastConnections(double firstMilliseconds, int expectedProbes)
    {
        var sizes = new List<int>(); var samples = new List<MCBTimingSample>();
        MCBPerformance.MeasureNetworkAsync((size, cancel) => {
            sizes.Add(size); return Task.FromResult(Point(size * 1000000L, firstMilliseconds));
        }, samples.Add, CancellationToken.None).GetAwaiter().GetResult();
        Assert.That(sizes, Is.EqualTo(expectedProbes == 2 ? new[] { 25, 100 } : new[] { 25 }));
        Assert.That(samples.Count, Is.EqualTo(expectedProbes));
    }
    [Test] public void ForegroundCancellationPreservesSmallProbeWithoutStartingLargeProbe()
    {
        using (var cancel = new CancellationTokenSource()) {
            int requests = 0, completed = 0;
            Assert.Throws<OperationCanceledException>(() => MCBPerformance.MeasureNetworkAsync((size, token) => {
                requests++; return Task.FromResult(Point(size * 1000000L, 500));
            }, sample => { completed++; cancel.Cancel(); }, cancel.Token).GetAwaiter().GetResult());
            Assert.That(requests, Is.EqualTo(1)); Assert.That(completed, Is.EqualTo(1));
        }
    }

    [TestCase(7, 1009)]
    [TestCase(513, 1048593)]
    [TestCase(65539, 17825809)]
    public void WordXorPreservesUnalignedKeyWrapsAndChunkBoundaries(int keyBytes, int payloadBytes)
    {
        byte[] key = MCBPerformance.CreateCalibrationPayload(keyBytes), input = MCBPerformance.CreateCalibrationPayload(payloadBytes);
        var expected = new byte[input.Length];
        for (int i = 0; i < input.Length; i++) expected[i] = (byte)(input[i] ^ key[i % key.Length]);
        float lastProgress = 0;
        var output = MCBXor.Transform(key, input, p => { Assert.That(p, Is.GreaterThanOrEqualTo(lastProgress)); lastProgress = p; });
        Assert.That(output, Is.EqualTo(expected)); Assert.That(lastProgress, Is.EqualTo(1));
        Assert.That(MCBXor.Transform(key, output), Is.EqualTo(input));
    }
}
#endif
