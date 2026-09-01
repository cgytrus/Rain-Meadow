using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using HarmonyLib;
using Mono.Cecil.Cil;
using Mono.Collections.Generic;
using MonoMod.Cil;
using MonoMod.RuntimeDetour;
using MonoMod.Utils;
using ExceptionHandler = Mono.Cecil.Cil.ExceptionHandler;
using OpCodes = Mono.Cecil.Cil.OpCodes;

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
#if DEBUG
        if (IsHookPriorityProblematic(from, to, config, out string problem))
            RainMeadow.Error(problem);
#endif
    }

#if DEBUG
    private readonly HashSet<MethodBase> _hookedMethods = [ ];
    private bool IsHookPriorityProblematic(MethodBase from, MethodInfo to, HookConfig config, out string problem)
    {
        problem = null!;

        if (config.Priority != 0)
            return false;

        string logPrefix = $"{from.GetID(simple: true)} hook {to.GetID(simple: true)} with default priority";

        if (!_hookedMethods.Add(from))
            logPrefix += " and another hook";

        Type? origType = to.GetParameters().FirstOrDefault()?.ParameterType;

        if (origType is null || !typeof(Delegate).IsAssignableFrom(origType))
        {
            problem = $"{logPrefix} does not have orig param";
            return true;
        }

        using DynamicMethodDefinition dmd = new(to);
        using ILContext il = new(dmd.Definition);
        il.ReferenceBag = RuntimeILReferenceBag.Instance; // i dont think this is needed but keep it just in case

        List<(Instruction from, Instruction to)> branches = il.Instrs
            .Where(x => x.Operand is Instruction)
            .Select(x => (x, (Instruction)x.Operand))
            .Concat(
                il.Instrs
                    .Where(x => x.Operand is Instruction[])
                    .SelectMany(x => ((Instruction[])x.Operand).Select(y => (x, y)))
            )
            .ToList();

        Collection<ExceptionHandler> exceptionHandles = il.Body.ExceptionHandlers;

        // cant pass origType into MatchCallvirt directly
        // because FullName in reflection and Fullname in Mono.Cecil are formatted differently for generics
        string origTypeFullName = il.Body.Method.Module.ImportReference(origType).FullName;

        ILCursor cursor = new(il);

        bool everCallsOrig = false;

        // first ldarg.1, then callvirt with our orig param type
        while (cursor.TryGotoNext(x => x.MatchLdarg(to.IsStatic ? 0 : 1))
            && cursor.TryGotoNext(x => x.MatchCallvirt(origTypeFullName, "Invoke")))
        {
            everCallsOrig = true;

            // any branches that might skip this instruction
            if (branches.Any(x => x.from.Offset < cursor.Next.Offset && cursor.Next.Offset < x.to.Offset))
            {
                problem = $"{logPrefix} might not call orig (branch)";
                return true;
            }

            if (cursor.Clone().TryGotoPrev(x => x.MatchRet()))
            {
                problem = $"{logPrefix} might not call orig (early return)";
                return true;
            }

            if (cursor.Clone().TryGotoPrev(x => x.MatchJmp(out _)))
            {
                problem = $"{logPrefix} might not call orig (jump)";
                return true;
            }

            if (cursor.Clone().TryGotoPrev(x => x.MatchTail()))
            {
                problem = $"{logPrefix} might not call orig (tail call)";
                return true;
            }

            // ReSharper disable once InvertIf
            // throws before the current instruction that are within the same try
            if (cursor.Clone().TryGotoPrev(x => x.MatchThrow()
                    && exceptionHandles.Any(y =>
                        y.TryStart.Offset <= cursor.Next.Offset
                        && cursor.Next.Offset < y.TryEnd.Offset
                        && y.TryStart.Offset <= x.Offset
                        && x.Offset < y.TryEnd.Offset
                    )
                ))
            {
                problem = $"{logPrefix} might not call orig (throw)";
                return true;
            }
        }

        // ReSharper disable once ConvertIfStatementToReturnStatement
        if (!everCallsOrig)
        {
            problem = $"{logPrefix} never calls orig";
            return true;
        }

        return false;
    }
#endif

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
        IEnumerable<IPendingDetour> pendingDetours = _pendingDetours;
#if DEBUG
        // randomize order in debug to catch priority bugs more easily
        pendingDetours = pendingDetours.OrderBy(_ => RXRandom._randomSource.Next());
#endif
        Parallel.ForEach(pendingDetours, hook => hook.Apply());
        _pendingDetours.Clear();
    }

    private interface IPendingDetour
    {
        public void Apply();
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

        private static readonly Dictionary<MethodBase, List<Detour>> _detourMap =
            (Dictionary<MethodBase, List<Detour>>)AccessTools.Field(typeof(Detour), "_DetourMap").GetValue(null);

        public void Apply()
        {
            ApplyMonoModPriorityWorkaround();
            _ctor(hook, from, to, target, ref config);
        }

        private static readonly List<Hook> _workaroundHooks = [ ];
        private void ApplyMonoModPriorityWorkaround()
        {
            // hook priorities break with exactly two detours, so add one that does nothing to work around the bug.
            if (_detourMap.TryGetValue(from, out List<Detour> detours) && detours.Count > 2)
                return;

            Type[] origParamTypes = from.IsStatic
                ? from.GetParameters().Select(p => p.ParameterType).ToArray()
                : [ from.GetThisParamType(), ..from.GetParameters().Select(p => p.ParameterType) ];

            // first try getting the parameter type from the hook
            Type? origDelegateType = to.GetParameters().FirstOrDefault()?.ParameterType;

            // then try Action/Func
            if (origDelegateType is null || !typeof(Delegate).IsAssignableFrom(origDelegateType))
            {
                try
                {
                    origDelegateType = to.ReturnType == typeof(void)
                        ? AccessTools
                            .TypeByName($"System.Action`{origParamTypes.Length}")?
                            .MakeGenericType(origParamTypes)
                        : AccessTools
                            .TypeByName($"System.Func`{origParamTypes.Length + 1}")?
                            .MakeGenericType([ ..origParamTypes, to.ReturnType ]);
                }
                catch (Exception ex)
                {
                    RainMeadow.Warn(ex);
                    origDelegateType = null;
                }
            }

            if (origDelegateType is null)
            {
                RainMeadow.Warn("Failed to apply MonoMod.RuntimeDetour hook priority workaround");
                return;
            }

            using DynamicMethodDefinition dmd = new(
                $"MonoModPriorityWorkaround<{from.GetID(simple: true)}>",
                to.ReturnType, // cant get return type of from but to should be the same
                [ origDelegateType, ..origParamTypes ]
            );
            ILProcessor il = dmd.GetILProcessor();
            il.Emit(OpCodes.Ldarg, 0);
            for (int i = 0; i < origParamTypes.Length; i++)
                il.Emit(OpCodes.Ldarg, i + 1);
            il.Emit(OpCodes.Callvirt, origDelegateType.GetMethod("Invoke"));
            il.Emit(OpCodes.Ret);
            _workaroundHooks.Add(new Hook(from, dmd.Generate()));
        }
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

        private static readonly IDictionary _map =
            (IDictionary)AccessTools.Field(typeof(ILHook), "_Map").GetValue(null);

        private static readonly FieldInfo _contextChainInfo =
            AccessTools.Field(_map.GetType().GetGenericArguments()[1], "Chain");

        public void Apply()
        {
            ApplyMonoModPriorityWorkaround();
            _ctor(hook, from, manipulator, ref config);
        }

        private static readonly List<ILHook> _workaroundHooks = [ ];
        private void ApplyMonoModPriorityWorkaround()
        {
            // hook priorities break with exactly two detours, so add one that does nothing to work around the bug.
            if (_map.Contains(from) && ((List<ILHook>)_contextChainInfo.GetValue(_map[from])).Count > 2)
                return;
            _workaroundHooks.Add(new ILHook(from, _ => { }, new ILHookConfig { ManualApply = true }));
        }
    }
}
