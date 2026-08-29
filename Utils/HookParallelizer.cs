using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.RuntimeDetour.HookGen;

namespace RainMeadow;

public class HookParallelizer : IDisposable
{
    // need to keep a list of hooks even if it isnt used to prevent them from getting garbage collected and undone
    private static readonly List<IDetour> hooks = [ ];

    private readonly List<(MethodBase method, Delegate callback, bool modify)> _pendingHooks = [ ];

    public HookParallelizer()
    {
        // this will prevent the hooks from getting added and just add them to pendingHooks instead
        HookEndpointManager.OnAdd += On_HookEndpointManager_OnAdd;
        HookEndpointManager.OnModify += On_HookEndpointManager_OnModify;
    }

    private bool On_HookEndpointManager_OnAdd(MethodBase method, Delegate callback)
    {
        _pendingHooks.Add((method, callback, false));
        return false;
    }

    private bool On_HookEndpointManager_OnModify(MethodBase method, Delegate callback)
    {
        _pendingHooks.Add((method, callback, true));
        return false;
    }

    public void Dispose()
    {
        RainMeadow.Info($"Applying {_pendingHooks.Count} collected hooks in parallel");

        ConcurrentBag<IDetour> hooksAdded = [ ];
        Parallel.ForEach(_pendingHooks, hook =>
        {
            (MethodBase method, Delegate callback, bool modify) = hook;
            // unfortunately, HookEndpointManager.Add/Modify is not thread safe,
            // so have to create hooks manually instead
            hooksAdded.Add(
                modify
                    ? new ILHook(method, (ILContext.Manipulator)callback)
                    : new Hook(method, callback)
            );
        });
        hooks.AddRange(hooksAdded);
        _pendingHooks.Clear();

        HookEndpointManager.OnAdd -= On_HookEndpointManager_OnAdd;
        HookEndpointManager.OnModify -= On_HookEndpointManager_OnModify;
    }
}
