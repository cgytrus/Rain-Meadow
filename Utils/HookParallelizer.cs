using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;

namespace RainMeadow;

/// <summary>
/// Allows applying multiple hooks at once in parallel.
/// Create an instance to start collecting hooks made with <see cref="Hook"/>, <see cref="ILHook"/>,
/// they will not be applied immediately.
/// Dispose of or call <see cref="ApplyHooks"/> to apply the collected hooks in parallel.
/// Be aware that referencing anything in <see cref="Hook"/> and <see cref="ILHook"/> while this is active
/// is unsupported.
/// </summary>
/// <example>
/// <code>
/// using (new HookParallelizer())
/// {
///     On.Class.Method1 += On_Class_Method1;
///     On.Class.Method2 += On_Class_Method2;
///     On.Class.Method3 += On_Class_Method3;
///     IL.Class.Method4 += IL_Class_Method4;
/// }
/// </code>
/// </example>
public class HookParallelizer : IDisposable
{
    private readonly List<IPendingDetour> _pendingDetours = [ ];

    private readonly Hook _ilHookHook;
    private readonly Hook _hookHook;

    public HookParallelizer()
    {
        // this will prevent the hooks from getting added and just add them to _pendingHooks instead
        // these should go IN THIS ORDER SPECIFICALLY. do NOT change the order.
        // also be careful with them, since they dont call orig, callers should not try to access anything in them
        // until HookParallelizer is disposed of
        _ilHookHook = new Hook(PendingILHook.ctorInfo, On_ILHook_ctor);
        _hookHook = new Hook(PendingHook.ctorInfo, On_Hook_ctor);
    }

    private void On_ILHook_ctor(
        ILHook self,
        MethodBase from,
        ILContext.Manipulator manipulator,
        ref ILHookConfig config)
    {
        _pendingDetours.Add(new PendingILHook(self, from, manipulator, config));
    }

    private void On_Hook_ctor(Hook self, MethodBase from, MethodInfo to, object target, ref HookConfig config)
    {
        _pendingDetours.Add(new PendingHook(self, from, to, target, config));
    }

    public void Dispose()
    {
        _hookHook.Dispose();
        _ilHookHook.Dispose();

        ApplyHooks();
    }

    public void ApplyHooks()
    {
        RainMeadow.Info($"Applying {_pendingDetours.Count} hooks in parallel");
        Parallel.ForEach(_pendingDetours, hook => hook.InvokeCtor());
        _pendingDetours.Clear();
    }

    private interface IPendingDetour
    {
        public void InvokeCtor();
    }

    private class PendingHook(Hook hook, MethodBase from, MethodInfo to, object? target, HookConfig config)
        : IPendingDetour
    {
        public static readonly ConstructorInfo ctorInfo =
            typeof(Hook).GetConstructor(
                [ typeof(MethodBase), typeof(MethodInfo), typeof(object), typeof(HookConfig).MakeByRefType() ]
            )!;

        private delegate void Ctor(Hook self, MethodBase from, MethodInfo to, object? target, ref HookConfig config);

        private static readonly Ctor ctor = ctorInfo.CreateDelegate<Ctor>();

        public void InvokeCtor() => ctor(hook, from, to, target, ref config);
    }

    private class PendingILHook(ILHook hook, MethodBase from, ILContext.Manipulator manipulator, ILHookConfig config)
        : IPendingDetour
    {
        public static readonly ConstructorInfo ctorInfo =
            typeof(ILHook).GetConstructor(
                [ typeof(MethodBase), typeof(ILContext.Manipulator), typeof(ILHookConfig).MakeByRefType() ]
            )!;

        private delegate void Ctor(
            ILHook self, MethodBase from, ILContext.Manipulator manipulator, ref ILHookConfig config
        );

        private static readonly Ctor ctor = ctorInfo.CreateDelegate<Ctor>();

        public void InvokeCtor() => ctor(hook, from, manipulator, ref config);
    }
}
