#if UNITY_EDITOR
using System;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using UnityEngine.Networking;

public static partial class MCBPerformance
{
    sealed class CountingDownload : DownloadHandlerScript
    {
        public long Bytes { get; private set; }
        readonly long maximum;
        public CountingDownload(long maximum) : base(new byte[64 * 1024]) { this.maximum = maximum; }
        protected override bool ReceiveData(byte[] data, int length)
        {
            Bytes += length;
            return Bytes <= maximum;
        }
    }

    static async Task PrepareProbesAsync(CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        string url = MCBUtils.ResolveApiUrl("mcb/performance/bootstrap");
        using (var request = UnityWebRequest.Get(url)) {
            request.SetRequestHeader("Authorization", "Bearer " + token);
            request.timeout = 60;
            using (cancel.Register(request.Abort))
                await MCBManagedRequest.SendUnityWebRequestAsync(request, url, MCBRequestPolicy.Diagnostics("MCB download calibration"));
            cancel.ThrowIfCancellationRequested();
            if (request.result != UnityWebRequest.Result.Success) throw new InvalidOperationException("Calibration endpoint is unavailable.");
        }
    }

    static async Task<MCBTimingSample> MeasureProbeAsync(int megabytes, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        long expected = megabytes * 1_000_000L;
        string url = MCBUtils.ResolveApiUrl("mcb/performance/probe/" + megabytes)
            + "?t=" + Uri.EscapeDataString(token) + "&nonce=" + Guid.NewGuid().ToString("N");
        using (var request = new UnityWebRequest(url, "GET")) {
            var counter = new CountingDownload(expected);
            request.downloadHandler = counter;
            request.timeout = 30;
            request.SetRequestHeader("Cache-Control", "no-cache, no-store");
            var watch = Stopwatch.StartNew();
            using (cancel.Register(request.Abort))
                await MCBManagedRequest.SendUnityWebRequestAsync(request, url, MCBRequestPolicy.Diagnostics("MCB bandwidth probe"));
            watch.Stop();
            cancel.ThrowIfCancellationRequested();
            string encoding = request.GetResponseHeader("Content-Encoding");
            if (request.result != UnityWebRequest.Result.Success || counter.Bytes != expected
                || (!string.IsNullOrEmpty(encoding) && encoding != "identity")) throw new InvalidOperationException("Incomplete bandwidth probe.");
            return new MCBTimingSample { bytes = counter.Bytes, milliseconds = watch.Elapsed.TotalMilliseconds };
        }
    }

    static async Task SendReportAsync(object report)
    {
        if (!ShareMeasurements || string.IsNullOrWhiteSpace(token)) return;
        string url = MCBUtils.ResolveApiUrl("mcb/performance/reports");
        try {
            using (var request = new UnityWebRequest(url, "POST")) {
                request.SetRequestHeader("Authorization", "Bearer " + token);
                request.SetRequestHeader("Content-Type", "application/json");
                request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(report)));
                request.downloadHandler = new DownloadHandlerBuffer();
                request.timeout = 5;
                await MCBManagedRequest.SendUnityWebRequestAsync(request, url, MCBRequestPolicy.Diagnostics("MCB performance measurements"));
            }
        } catch { /* Optional measurements must not affect the user operation. */ }
    }
}
#endif
