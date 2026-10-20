using System;
using System.Collections;
using System.Collections.Generic;

/// <summary>Nested editor work shared by the UI and MCP. Disposes iterator scopes on failure.</summary>
public static class MCBWork
{
    public static IEnumerator Flatten(IEnumerator routine)
    {
        var stack = new Stack<IEnumerator>(); stack.Push(routine);
        try
        {
            while (stack.Count > 0)
            {
                var current = stack.Peek();
                if (!current.MoveNext()) { (stack.Pop() as IDisposable)?.Dispose(); continue; }
                if (current.Current is IEnumerator nested) stack.Push(nested);
                else yield return current.Current;
            }
        }
        finally { while (stack.Count > 0) (stack.Pop() as IDisposable)?.Dispose(); }
    }

    public static IEnumerator Guard(IEnumerator routine, Action<Exception> failed)
    {
        using var flat = new DisposableEnumerator(Flatten(routine));
        while (true)
        {
            bool more;
            try { more = flat.Value.MoveNext(); }
            catch (Exception ex) { failed(ex); yield break; }
            if (!more) yield break;
            yield return flat.Value.Current;
        }
    }

    // Synchronous callers (including deterministic health checks) consume the same implementation.
    public static void Drain(IEnumerator routine)
    {
        draining++;
        try
        {
            using var flat = new DisposableEnumerator(Flatten(routine));
            while (flat.Value.MoveNext()) { }
        }
        finally { draining--; }
    }

    [ThreadStatic] private static int draining;

    /// <summary>
    /// Whether Unity can finish compiling or importing while the work waits: never while it is drained, since the main
    /// thread is running it then. A script compile requested meanwhile (a file saved during a test run) waited for the
    /// work, and the work for the compile: the editor hung until it was killed.
    /// </summary>
    public static bool EditorBusy => draining == 0 && (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorApplication.isUpdating);
    private sealed class DisposableEnumerator : IDisposable
    {
        public readonly IEnumerator Value;
        public DisposableEnumerator(IEnumerator value) { Value = value; }
        public void Dispose() => (Value as IDisposable)?.Dispose();
    }
}
