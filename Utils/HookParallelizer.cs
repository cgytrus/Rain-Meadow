using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Mono.Cecil.Cil;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;

namespace RainMeadow;

/// <summary>
/// Allows applying multiple hooks at once in parallel.
/// Create an instance to start collecting hooks made with <see cref="Hook"/>, <see cref="ILHook"/>,
/// they will not be applied immediately.
/// Dispose of or call <see cref="ApplyHooks"/> to apply the collected hooks in parallel.
/// Be aware that referencing anything in <see cref="Hook"/> and <see cref="ILHook"/>
/// and that using <see cref="DetourContext"/> without disposing of it
/// while this is active is unsupported.
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
    private readonly Stack<IDetour> _tempHooks = [ ];

    public HookParallelizer()
    {
        // every single hook still creates a stack trace because monomod wants to support
        // creating DetourContexts without disposing of them
        // and actually, this is currently the only way it even works!
        // because monomod developers accidentally inverted the IsDisposed check in their Dispose implementation!

        // anyway, so we first remove all invalid contexts off the top of the stack by getting DetourContext.Current
        object current = AccessTools.PropertyGetter(typeof(DetourContext), "Current").Invoke(null, [ ]);
        AccessTools.Field(typeof(DetourContext), "Last").SetValue(null, current); // it doesnt set Last if its null

        // then hook IsValid to skip the stack trace check
        // this also means that users of this class would be *required* to dispose of their created DetourContexts
        _tempHooks.Push(new ILHook(
            AccessTools.PropertyGetter(typeof(DetourContext), "IsValid"),
            IL_DetourContext_get_IsValid
        ));

        // then fix DetourContext.Dispose
        _tempHooks.Push(new ILHook(
            AccessTools.Method(typeof(DetourContext), nameof(DetourContext.Dispose)),
            IL_DetourContext_Dispose
        ));

        // this will prevent the hooks from getting added and just add them to _pendingHooks instead
        // these should go IN THIS ORDER SPECIFICALLY. do NOT change the order.
        // also be careful with them, since they dont call orig, callers should not try to access anything in them
        // until HookParallelizer is disposed of
        _tempHooks.Push(new Hook(PendingILHook.ctorInfo, On_ILHook_ctor));
        _tempHooks.Push(new Hook(PendingHook.ctorInfo, On_Hook_ctor));
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

    private void IL_DetourContext_get_IsValid(ILContext il)
    {
        ILCursor cursor = new(il);

        // if (this.IsDisposed)
        cursor.GotoNext(MoveType.Before, i => i.MatchBrfalse(out _));

        // return !this.IsDisposed;
        cursor.Emit(OpCodes.Ldc_I4_0);
        cursor.Emit(OpCodes.Ceq);
        cursor.Emit(OpCodes.Ret);

        // match the old brfalse
        cursor.Emit(OpCodes.Ldc_I4_0);
    }

    private void IL_DetourContext_Dispose(ILContext il)
    {
        ILCursor cursor = new(il);

        // if (!this.IsDisposed)
        cursor.GotoNext(MoveType.Before, i => i.MatchBrtrue(out _));

        // if (!!this.IsDisposed), lol
        cursor.Emit(OpCodes.Ldc_I4_0);
        cursor.Emit(OpCodes.Ceq);
    }

    public void Dispose()
    {
        while (_tempHooks.Count > 0)
            _tempHooks.Pop().Dispose();
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

        private static readonly Ctor _ctor = ctorInfo.CreateDelegate<Ctor>();

        public void InvokeCtor() => _ctor(hook, from, to, target, ref config);
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

        private static readonly Ctor _ctor = ctorInfo.CreateDelegate<Ctor>();

        public void InvokeCtor() => _ctor(hook, from, manipulator, ref config);
    }
}
