using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using RimGovernor.Host.Core;
using UnityEngine;
using Verse;

namespace RimGovernor.Host;

internal static class RimBridgePatches
{
    private const string HarmonyId = "pardeike.RimGovernor.Host.runtime";
    private static readonly object Sync = new();
    private static bool _applied;

    public static void Apply()
    {
        lock (Sync)
        {
            if (_applied)
                return;

            try
            {
                new Harmony(HarmonyId).Patch(
                    original: AccessTools.Method(typeof(Root), nameof(Root.Update)),
                    postfix: new HarmonyMethod(typeof(Root_Update_Patch), nameof(Root_Update_Patch.Postfix)));
                Log.Message("[RimBridge] Applied essential Harmony patches.");
            }
            catch (Exception ex)
            {
                Log.Error($"[RimBridge] STARTUP_ESSENTIAL_PATCH_FAILURE: {ex}");
                throw;
            }

            _applied = true;
        }
    }

    public static object DescribeStatus()
    {
        lock (Sync)
        {
            return new { applied = _applied };
        }
    }
}

[HarmonyPatch(typeof(Root), nameof(Root.Update))]
internal static class Root_Update_Patch
{
    public static void Postfix()
    {
        RimBridgeMainThread.Pump();
        RimBridgeAsyncScheduler.Pump();
        RimWorldTickStepper.AdvanceFromRootUpdate(Time.frameCount);
        RimBridgeAsyncScheduler.Pump();
        if (PlayDataLoader.Loaded && !LongEventHandler.AnyEventNowOrWaiting && Find.UIRoot != null)
        {
            RimBridgeStartup.OnRuntimeReady();
        }
    }
}

internal static class RimBridgeMainThread
{
    private interface IMainThreadWorkItem
    {
        void ExecuteIfPending();

        bool CancelIfPending();
    }

    private sealed class MainThreadWorkItem<T> : IMainThreadWorkItem
    {
        private readonly Func<T> _func;
        private readonly TaskCompletionSource<T> _completion = new();
        private readonly OperationContextSnapshot _context = OperationContext.Capture();
        private int _state;

        public MainThreadWorkItem(Func<T> func)
        {
            _func = func ?? throw new ArgumentNullException(nameof(func));
        }

        public Task<T> Completion => _completion.Task;

        public void ExecuteIfPending()
        {
            if (Interlocked.CompareExchange(ref _state, 1, 0) != 0)
                return;

            try
            {
                using var scope = OperationContext.Restore(_context);
                var result = _func();
                _completion.TrySetResult(result);
            }
            catch (Exception ex)
            {
                _completion.TrySetException(ex);
            }
            finally
            {
                Interlocked.Exchange(ref _state, 2);
            }
        }

        public bool CancelIfPending()
        {
            if (Interlocked.CompareExchange(ref _state, 3, 0) != 0)
                return false;

            _completion.TrySetCanceled();
            return true;
        }
    }

    private static readonly Queue<IMainThreadWorkItem> Pending = [];
    private static readonly object Sync = new();
    private static int _mainThreadId;

    public static void Initialize()
    {
        if (_mainThreadId == 0)
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
    }

    public static bool IsMainThread => Thread.CurrentThread.ManagedThreadId == _mainThreadId;

    public static void Pump()
    {
        while (true)
        {
            IMainThreadWorkItem workItem;
            lock (Sync)
            {
                if (Pending.Count == 0)
                    break;

                workItem = Pending.Dequeue();
            }

            try
            {
                workItem.ExecuteIfPending();
            }
            catch (Exception ex)
            {
                Log.Error($"[RimBridge] Main-thread work item failed: {ex}");
            }
        }
    }

    public static T Invoke<T>(Func<T> func, int timeoutMs = 5000)
    {
        if (IsMainThread)
            return func();

        var workItem = new MainThreadWorkItem<T>(func);

        lock (Sync)
        {
            Pending.Enqueue(workItem);
        }

        if (timeoutMs <= 0)
            return workItem.Completion.GetAwaiter().GetResult();

        if (workItem.Completion.Wait(timeoutMs))
            return workItem.Completion.GetAwaiter().GetResult();

        if (workItem.Completion.IsCompleted)
            return workItem.Completion.GetAwaiter().GetResult();

        if (workItem.CancelIfPending())
            throw new TimeoutException($"Timed out waiting for main-thread work after {timeoutMs}ms.");

        if (workItem.Completion.IsCompleted)
            return workItem.Completion.GetAwaiter().GetResult();

        throw new TimeoutException($"Timed out waiting for main-thread work after {timeoutMs}ms.");
    }

    public static void Invoke(Action action, int timeoutMs = 5000)
    {
        Invoke(() =>
        {
            action();
            return true;
        }, timeoutMs);
    }

    public static async Task<T> InvokeAsync<T>(Func<T> func, CancellationToken cancellationToken = default)
    {
        if (IsMainThread)
            return func();

        var workItem = new MainThreadWorkItem<T>(func);

        lock (Sync)
        {
            Pending.Enqueue(workItem);
        }

        CancellationTokenRegistration registration = default;
        if (cancellationToken.CanBeCanceled)
            registration = cancellationToken.Register(() => workItem.CancelIfPending());

        try
        {
            return await workItem.Completion.ConfigureAwait(false);
        }
        finally
        {
            registration.Dispose();
        }
    }

    public static Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
    {
        return InvokeAsync(() =>
        {
            action();
            return true;
        }, cancellationToken);
    }
}
