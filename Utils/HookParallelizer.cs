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

    private interface IPendingDetour
    {
        public IDetour Apply();
    }

    private class PendingHook(MethodBase from, MethodInfo to, object? target, HookConfig? config) : IPendingDetour
    {
        public IDetour Apply() => config.HasValue
            ? new Hook(from, to, target, config.Value)
            : new Hook(from, to, target);
    }

    private class PendingILHook(MethodBase from, ILContext.Manipulator manip, ILHookConfig? config) : IPendingDetour
    {
        public IDetour Apply() => config.HasValue
            ? new ILHook(from, manip, config.Value)
            : new ILHook(from, manip);
    }

    private readonly List<IPendingDetour> _pendingHooks = [ ];

    private readonly Hook _ilHookHook;
    private readonly Hook _hookHook;

    public HookParallelizer()
    {
        // this will prevent the hooks from getting added and just add them to pendingHooks instead

        HookEndpointManager.OnModify += On_HookEndpointManager_OnModify;
        HookEndpointManager.OnAdd += On_HookEndpointManager_OnAdd;

        // the next two should go IN THIS ORDER SPECIFICALLY. do NOT change the order.
        // also be careful with them, since they dont call orig, callers should not try to access anything in them,
        // nor should that instance be saved anywhere

        _ilHookHook = new Hook(
            typeof(ILHook).GetConstructor(
                [ typeof(MethodBase), typeof(ILContext.Manipulator), typeof(ILHookConfig).MakeByRefType() ]
            ),
            On_ILHook_ctor
        );

        _hookHook = new Hook(
            typeof(Hook).GetConstructor(
                [ typeof(MethodBase), typeof(MethodInfo), typeof(object), typeof(HookConfig).MakeByRefType() ]
            ),
            On_Hook_ctor
        );
    }

    private bool On_HookEndpointManager_OnModify(MethodBase method, Delegate callback)
    {
        _pendingHooks.Add(new PendingILHook(method, (ILContext.Manipulator)callback, null));
        return false;
    }

    private bool On_HookEndpointManager_OnAdd(MethodBase method, Delegate callback)
    {
        _pendingHooks.Add(new PendingHook(method, callback.Method, callback.Target, null));
        return false;
    }

    private void On_ILHook_ctor(
        ILHook self, MethodBase from, ILContext.Manipulator manipulator, ref ILHookConfig config
    ) {
        _pendingHooks.Add(new PendingILHook(from, manipulator, config));
    }

    private void On_Hook_ctor(Hook self, MethodBase from, MethodInfo to, object target, ref HookConfig config) {
        _pendingHooks.Add(new PendingHook(from, to, target, config));
    }

    public void Dispose()
    {
        _hookHook.Dispose();
        _ilHookHook.Dispose();
        HookEndpointManager.OnAdd -= On_HookEndpointManager_OnAdd;
        HookEndpointManager.OnModify -= On_HookEndpointManager_OnModify;

        ApplyHooks();
    }

    public void ApplyHooks()
    {
        RainMeadow.Info($"Applying {_pendingHooks.Count} collected hooks in parallel");
        ConcurrentBag<IDetour> hooksAdded = [ ];
        Parallel.ForEach(_pendingHooks, hook => hooksAdded.Add(hook.Apply()));
        _pendingHooks.Clear();
        hooks.AddRange(hooksAdded);
    }
}
