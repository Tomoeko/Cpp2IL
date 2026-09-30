using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeDerivedReceiverInvocationFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance;
            var baseType = typeof(BaseNode);
            var derivedType = typeof(DerivedNode);
            var holderType = typeof(InvocationHolder);
            var rows = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" },
                    { "methods", baseType.GetMethods(flags).Length + baseType.GetConstructors(flags).Length +
                        derivedType.GetMethods(flags).Length + derivedType.GetConstructors(flags).Length +
                        holderType.GetMethods(flags).Length + holderType.GetConstructors(flags).Length },
                    { "fields", baseType.GetFields(flags).Length + derivedType.GetFields(flags).Length +
                        holderType.GetFields(flags).Length },
                    { "baseType", derivedType.BaseType.FullName },
                    { "targetType", holderType.GetField("Target").FieldType.FullName },
                    { "integerDeclaringType", derivedType.GetMethod("SetInt").DeclaringType.FullName },
                    { "booleanDeclaringType", derivedType.GetMethod("SetFlag").DeclaringType.FullName },
                    { "extraDeclaringType", derivedType.GetField("Extra").DeclaringType.FullName },
                    { "integerParameter", baseType.GetMethod("SetInt").GetParameters()[0].ParameterType.FullName },
                    { "booleanParameter", baseType.GetMethod("SetFlag").GetParameters()[0].ParameterType.FullName }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "holder", HolderState(new InvocationHolder(), null, null) },
                    { "base", BaseState(new BaseNode()) }, { "derived", State(new DerivedNode()) }
                }
            };
            for (var operation = 0; operation < 2; operation++)
            {
                Scenario(rows, operation, "success", false, false, false, false, 29, true);
                Scenario(rows, operation, "target-null", true, false, false, false, -41, false);
                Scenario(rows, operation, "holder-null", false, true, false, false, 47, true);
                Scenario(rows, operation, "distinct-target", false, false, false, true, int.MinValue, false);
                Scenario(rows, operation, "alias", false, false, true, false, int.MaxValue, true);
                var first = Node(17, false, 31);
                var second = Node(-19, true, -37);
                var owner = new InvocationHolder();
                Record(rows, operation, "reuse-failure", owner, first, second, int.MaxValue, false);
                owner.Target = first;
                Record(rows, operation, "reuse-success", owner, first, second, int.MinValue, true);
                Record(rows, operation, "repeat", owner, first, second, int.MaxValue, false);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-derived-receiver-invocation" }, { "observations", rows }
            }));
        }

        private static DerivedNode Node(int value, bool flag, int extra)
        {
            return new DerivedNode { Value = value, Flag = flag, Extra = extra };
        }

        private static Dictionary<string, object> BaseState(BaseNode node)
        {
            return new Dictionary<string, object> { { "calls", node.Calls }, { "value", node.Value }, { "flag", node.Flag } };
        }

        private static object State(DerivedNode node)
        {
            var state = BaseState(node);
            state.Add("extra", node.Extra);
            return state;
        }

        private static object HolderState(InvocationHolder owner, DerivedNode first, DerivedNode second)
        {
            if (owner == null) return null;
            return new Dictionary<string, object>
            {
                { "targetNull", owner.Target == null },
                { "targetFirst", owner.Target != null && ReferenceEquals(owner.Target, first) },
                { "targetSecond", owner.Target != null && ReferenceEquals(owner.Target, second) },
                { "targetRuntimeType", owner.Target == null ? null : owner.Target.GetType().FullName }
            };
        }

        private static void Scenario(List<object> rows, int operation, string kind, bool targetNull,
            bool holderNull, bool alias, bool useSecond, int integer, bool boolean)
        {
            var first = Node(17, false, 31);
            var second = alias ? first : Node(-19, true, -37);
            var owner = holderNull ? null : new InvocationHolder { Target = targetNull ? null : useSecond ? second : first };
            Record(rows, operation, kind, owner, first, second, integer, boolean);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(InvocationHolder owner, int operation, int integer, bool boolean)
        {
            switch (operation)
            {
                case 0: owner.ForwardInt(integer); return;
                case 1: owner.ForwardFlag(boolean); return;
                default: throw new ArgumentOutOfRangeException("operation");
            }
        }

        private static void Record(List<object> rows, int operation, string kind, InvocationHolder owner,
            DerivedNode first, DerivedNode second, int integer, bool boolean)
        {
            var exception = "none";
            try { Invoke(owner, operation, integer, boolean); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", operation }, { "integer", integer }, { "boolean", boolean },
                { "exception", exception }, { "holder", HolderState(owner, first, second) },
                { "alias", ReferenceEquals(first, second) }, { "first", State(first) }, { "second", State(second) }
            });
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report") continue;
                try { Write(arguments[index + 1], "player"); Application.Quit(0); }
                catch (Exception error) { Debug.LogException(error); Application.Quit(1); }
                return;
            }
        }
    }
}
