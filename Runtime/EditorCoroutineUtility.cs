
#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.Collections;
using System.Collections.Generic;
using System;
using System.Reflection;

/// <summary>
/// A simple utility class to run Editor Coroutines.
/// Found originally here: https://gist.github.com/benblo/10732554
/// </summary>
public static class EditorCoroutineUtility
{
    private static readonly List<EditorCoroutineRunner> ActiveRunners = new List<EditorCoroutineRunner>();

    static EditorCoroutineUtility()
    {
        AssemblyReloadEvents.beforeAssemblyReload += StopAllCoroutines;
        EditorApplication.quitting += StopAllCoroutines;
    }

    private class EditorCoroutineRunner
    {
        private readonly Stack<IEnumerator> _coroutineStack;
        private readonly EditorApplication.CallbackFunction _updateDelegate;
        private bool _isRunning;

        public EditorCoroutineRunner(IEnumerator coroutine)
        {
            _coroutineStack = new Stack<IEnumerator>();
            _coroutineStack.Push(coroutine);
            _updateDelegate = Update;
        }

        public void Start()
        {
            if (_isRunning)
            {
                return;
            }

            _isRunning = true;
            ActiveRunners.Add(this);
            EditorApplication.update += _updateDelegate;
        }

        public void Stop()
        {
            if (!_isRunning)
            {
                return;
            }

            _isRunning = false;
            EditorApplication.update -= _updateDelegate;
            ActiveRunners.Remove(this);
        }

        private void Update()
        {
            if (_coroutineStack.Count == 0)
            {
                Stop();
                return;
            }

            IEnumerator currentCoroutine = _coroutineStack.Peek();

            try
            {
                if (!MoveNext(currentCoroutine))
                {
                    _coroutineStack.Pop();
                }
            }
            catch (Exception ex)
            {
                MCBLogger.LogError("Exception in editor coroutine: " + ex);
                // Optionally log the stack trace of the coroutine itself
                // LogCoroutineStackTrace(currentCoroutine);
                Stop(); // Stop processing on error to prevent spam
            }
        }

        private bool MoveNext(IEnumerator coroutine)
        {
            var yielded = coroutine.Current;

            // Handle WaitWhile - check if condition is still true
            if (yielded is WaitWhile waitWhile)
            {
                // Use reflection to get the predicate from WaitWhile
                var predicateField = typeof(WaitWhile).GetField("m_Predicate", BindingFlags.NonPublic | BindingFlags.Instance);
                if (predicateField != null)
                {
                    var predicate = predicateField.GetValue(waitWhile) as System.Func<bool>;
                    if (predicate != null && predicate())
                    {
                        // Condition is still true, keep waiting
                        return true;
                    }
                }
                // Condition is false or we couldn't get it, move to next
                return AdvanceCoroutine(coroutine);
            }

            // Handle WaitUntil - check if condition is now true
            if (yielded is WaitUntil waitUntil)
            {
                // Use reflection to get the predicate from WaitUntil
                var predicateField = typeof(WaitUntil).GetField("m_Predicate", BindingFlags.NonPublic | BindingFlags.Instance);
                if (predicateField != null)
                {
                    var predicate = predicateField.GetValue(waitUntil) as System.Func<bool>;
                    if (predicate != null && !predicate())
                    {
                        // Condition is still false, keep waiting
                        return true;
                    }
                }
                // Condition is true or we couldn't get it, move to next
                return AdvanceCoroutine(coroutine);
            }

            // Handle WaitForSeconds (though less useful in editor)
            if (yielded is WaitForSeconds waitForSeconds)
            {
                // For editor, we could implement a simple time-based wait
                // But it's generally better to use yield return null in editor
                MCBLogger.LogWarning("WaitForSeconds is not reliably supported in editor coroutines. Consider using yield return null instead.");
                return AdvanceCoroutine(coroutine);
            }

            if (yielded is AsyncOperation asyncOp)
            {
                if (!asyncOp.isDone)
                {
                    return true;
                }
            }

            if (yielded is Coroutine)
            {
                MCBLogger.LogWarning(
                    "EditorCoroutineUtility: Yielding on 'UnityEngine.Coroutine' is not supported in the editor. Use 'yield return null' or another IEnumerator.");
            }

            return AdvanceCoroutine(coroutine);
        }

        private bool AdvanceCoroutine(IEnumerator coroutine)
        {
            if (!coroutine.MoveNext())
            {
                return false;
            }

            if (coroutine.Current is IEnumerator nestedCoroutine)
            {
                _coroutineStack.Push(nestedCoroutine);
            }

            return true;
        }
    }

    public static void StartCoroutineOwnerless(IEnumerator coroutine)
    {
        if (coroutine == null)
        {
            MCBLogger.LogError("Coroutine cannot be null.");
            return;
        }

        EditorCoroutineRunner runner = new EditorCoroutineRunner(coroutine);
        runner.Start();
    }

    public static void StopAllCoroutines()
    {
        for (int i = ActiveRunners.Count - 1; i >= 0; i--)
        {
            ActiveRunners[i]?.Stop();
        }

        ActiveRunners.Clear();
    }

    // You could add StartCoroutine methods that take an owner object
    // to manage stopping coroutines if the owner is destroyed/disabled,
    // but for simple editor tasks, ownerless is often sufficient.
}
#endif
