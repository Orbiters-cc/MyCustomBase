#if UNITY_EDITOR
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Orbiters.Toolkit;

/// <summary>
/// MCB's backend requests, sent by Orbiters Toolkit's <see cref="OrbitersHttp"/> (one client, its retry of requests that
/// change nothing, credentials never forwarded to another server, timeouts that cover the body) with the two things MCB
/// keeps on top of it:
/// <list type="bullet">
/// <item>its own, shorter time limits (<see cref="NetworkService.GetTimeoutSeconds"/>): the whole call, retries included,
/// ends there. A call MCB's limit ends throws an <see cref="OrbitersHttpException"/> with <c>TimedOut</c>, as one
/// OrbitersHttp's own limit ends would;</item>
/// <item>its offline policy (<see cref="MCBConnectivityMonitor"/>): each call reports how it ended under its
/// <see cref="MCBRequestPolicy"/>, exactly as the UnityWebRequest it replaces did. A call the caller cancelled reports
/// nothing.</item>
/// </list>
/// Awaited from the editor's thread, a call continues there.
/// </summary>
internal static class McbHttp
{
    /// <summary>Sends <paramref name="payload"/> as JSON (none when null) and reads the answer as <typeparamref name="T"/>.</summary>
    internal static Task<T> JsonAsync<T>(HttpMethod method, string url, string token, object payload, MCBRequestPolicy policy, int timeoutSeconds,
        CancellationToken cancellation = default) =>
        Run(url, policy, timeoutSeconds, cancellation, limit => OrbitersHttp.JsonAsync<T>(method, url, payload, token, limit));

    /// <summary>GET as text.</summary>
    internal static Task<string> TextAsync(string url, string token, MCBRequestPolicy policy, int timeoutSeconds, CancellationToken cancellation = default) =>
        Run(url, policy, timeoutSeconds, cancellation, limit => OrbitersHttp.TextAsync(url, token, limit));

    /// <summary>
    /// Runs <paramref name="call"/> with a token that ends at MCB's time limit (or when <paramref name="cancellation"/>
    /// does) and reports how it ended. Throws what the call threw: <see cref="OrbitersHttpException"/> for an error answer
    /// or none, <see cref="OperationCanceledException"/> when the caller cancelled, <see cref="JsonException"/> for an
    /// answer that is not what was asked for.
    /// </summary>
    internal static async Task<T> Run<T>(string url, MCBRequestPolicy policy, int timeoutSeconds, CancellationToken cancellation,
        Func<CancellationToken, Task<T>> call)
    {
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
        {
            if (timeoutSeconds > 0) limit.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
            try
            {
                T result = await call(limit.Token);
                MCBConnectivityMonitor.ReportAnswer(url, 200, null, policy);
                return result;
            }
            catch (OrbitersHttpException ex)
            {
                MCBConnectivityMonitor.ReportAnswer(url, ex.StatusCode, ex.Message, policy);
                throw;
            }
            // The server answered, with something else than asked for.
            catch (JsonException)
            {
                MCBConnectivityMonitor.ReportAnswer(url, 200, null, policy);
                throw;
            }
            catch (OperationCanceledException) when (!cancellation.IsCancellationRequested && limit.IsCancellationRequested)
            {
                var timedOut = new OrbitersHttpException($"Orbiters did not answer within {timeoutSeconds} seconds.", url, 0, timedOut: true);
                MCBConnectivityMonitor.ReportAnswer(url, 0, timedOut.Message, policy);
                throw timedOut;
            }
        }
    }

    /// <summary>"HTTP 404 Orbiters answered HTTP 404 (Not Found). | url=… | body=…", for logs and MCB's error texts.</summary>
    internal static string Context(OrbitersHttpException error, string url)
    {
        string body = NetworkService.CreateBodySnippet(error?.Body);
        return $"HTTP {error?.StatusCode ?? 0} {error?.Message} | url={NetworkService.SanitizeUrlForLogs(url)}" +
               (string.IsNullOrEmpty(body) ? string.Empty : $" | body={body}");
    }
}
#endif
