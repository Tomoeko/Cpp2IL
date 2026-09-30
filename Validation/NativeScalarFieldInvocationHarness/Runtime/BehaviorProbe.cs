using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeScalarFieldInvocationFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance;
            var nodeType = typeof(InvocationNode);
            var holderType = typeof(InvocationHolder);
            var observations = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" },
                    { "methods", nodeType.GetMethods(flags).Length + nodeType.GetConstructors(flags).Length +
                        holderType.GetMethods(flags).Length + holderType.GetConstructors(flags).Length },
                    { "fields", nodeType.GetFields(flags).Length + holderType.GetFields(flags).Length },
                    { "booleanField", holderType.GetField("Flag").FieldType.FullName },
                    { "integerField", holderType.GetField("Value").FieldType.FullName },
                    { "booleanParameter", nodeType.GetMethod("ApplyPair").GetParameters()[1].ParameterType.FullName },
                    { "integerParameter", nodeType.GetMethod("ApplyPair").GetParameters()[0].ParameterType.FullName }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "holder", HolderState(new InvocationHolder(), null, null) },
                    { "node", State(new InvocationNode()) }
                }
            };
            for (var operation = 0; operation < 7; operation++)
            {
                Scenario(observations, operation, "success", false, false, false, false, 29, true);
                Scenario(observations, operation, "target-null", true, false, false, false, -41, false);
                Scenario(observations, operation, "holder-null", false, true, false, false, 47, true);
                Scenario(observations, operation, "alias", false, false, true, false, int.MinValue, false);
                Scenario(observations, operation, "replacement-null", false, false, false, true, int.MaxValue, true);
                var first = Node(17, true);
                var second = Node(-19, false);
                var owner = new InvocationHolder { Replacement = second, Value = int.MaxValue, Flag = false, Neighbor = 43 };
                Record(observations, operation, "reuse-failure", owner, first, second, -53);
                owner.Target = first;
                Record(observations, operation, "reuse-success", owner, first, second, int.MinValue);
                owner.Target = first;
                Record(observations, operation, "repeat", owner, first, second, int.MaxValue);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-scalar-field-invocation" }, { "observations", observations }
            }));
        }

        private static InvocationNode Node(int value, bool flag)
        {
            return new InvocationNode { Value = value, Flag = flag };
        }

        private static object State(InvocationNode node)
        {
            return new Dictionary<string, object>
            {
                { "calls", node.Calls }, { "value", node.Value }, { "flag", node.Flag }
            };
        }

        private static object HolderState(InvocationHolder owner, InvocationNode first, InvocationNode second)
        {
            if (owner == null) return null;
            return new Dictionary<string, object>
            {
                { "value", owner.Value }, { "flag", owner.Flag }, { "neighbor", owner.Neighbor },
                { "beforeCount", owner.BeforeCount }, { "afterCount", owner.AfterCount },
                { "targetNull", owner.Target == null }, { "targetFirst", owner.Target != null && ReferenceEquals(owner.Target, first) },
                { "targetSecond", owner.Target != null && ReferenceEquals(owner.Target, second) },
                { "replacementNull", owner.Replacement == null }
            };
        }

        private static void Scenario(List<object> rows, int operation, string kind, bool targetNull,
            bool holderNull, bool alias, bool replacementNull, int value, bool flag)
        {
            var first = Node(17, true);
            var second = alias ? first : Node(-19, false);
            var owner = holderNull ? null : new InvocationHolder
            {
                Target = targetNull ? null : first, Replacement = replacementNull ? null : second,
                Value = value, Flag = flag, Neighbor = 43
            };
            Record(rows, operation, kind, owner, first, second, value);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static object Invoke(InvocationHolder owner, int operation, int integer)
        {
            switch (operation)
            {
                case 0: owner.SetIntegerField(); return null;
                case 1: owner.SetBooleanField(integer); return null;
                case 2: owner.SetFieldPair(); return null;
                case 3: owner.SetFieldReverse(); return null;
                case 4: owner.SetFieldPairReturning(); return null;
                case 5: owner.SetCapturedBeforeEffect(); return null;
                case 6: owner.SetCapturedAfterEffect(); return null;
                default: throw new ArgumentOutOfRangeException("operation");
            }
        }

        private static void Record(List<object> rows, int operation, string kind, InvocationHolder owner,
            InvocationNode first, InvocationNode second, int integer)
        {
            var exception = "none";
            object result = null;
            try { result = Invoke(owner, operation, integer); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", operation }, { "integer", integer },
                { "exception", exception }, { "result", result }, { "holder", HolderState(owner, first, second) },
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
