using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using ConditionalCallResultTailFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object> { Declarations(), Defaults() };
            for (var operation = 0; operation < 5; operation++)
            {
                Scenario(observations, operation, "ready", true, false, false, false, false);
                Scenario(observations, operation, "not-ready", false, false, false, false, false);
                Scenario(observations, operation, "first-null", true, true, false, false, false);
                Scenario(observations, operation, "second-null-ready", true, false, true, false, false);
                Scenario(observations, operation, "second-null-not-ready", false, false, true, false, false);
                Scenario(observations, operation, "alias-ready", true, false, false, true, false);
                Scenario(observations, operation, "alias-not-ready", false, false, false, true, false);
                Scenario(observations, operation, "null-owner", true, false, false, false, true);
                var first = Node(operation != 2 && operation != 3, false, -11, 17);
                var second = Node(false, true, 13, -19);
                var owner = new ConditionalOwner { First = first, Neighbor = 23 };
                Record(observations, operation, "reuse-failure", owner, first, second);
                owner.Second = second;
                owner.ProducerCount = 0;
                Record(observations, operation, "reuse-success", owner, first, second);
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "conditional-call-result-tail" }, { "observations", observations }
            }));
        }

        private static object Declarations()
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var node = typeof(TailNode);
            var producer = typeof(ProducerOwner);
            var caller = typeof(ConditionalOwner);
            return new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "methods", node.GetMethods(flags).Length + node.GetConstructors(flags).Length +
                    producer.GetMethods(flags).Length + producer.GetConstructors(flags).Length +
                    caller.GetMethods(flags).Length + caller.GetConstructors(flags).Length },
                { "fields", node.GetFields(flags).Length + producer.GetFields(flags).Length },
                { "producerProtected", producer.GetMethod("GetNode", flags).IsFamily },
                { "callerVirtual", caller.GetMethod("WhenReadyTrue", flags).IsVirtual },
                { "predicateReturn", node.GetMethod("ReadFlag", flags).ReturnType.FullName },
                { "booleanParameter", node.GetMethod("ApplyBoolean", flags).GetParameters()[0].ParameterType.FullName },
                { "integerParameter", node.GetMethod("ApplyInteger", flags).GetParameters()[0].ParameterType.FullName },
                { "baseIdentity", caller.BaseType == producer }
            };
        }

        private static object Defaults()
        {
            var owner = new ConditionalOwner();
            var node = new TailNode();
            return new Dictionary<string, object>
            {
                { "kind", "defaults" }, { "firstNull", owner.First == null }, { "secondNull", owner.Second == null },
                { "producerCount", owner.ProducerCount }, { "ownerNeighbor", owner.Neighbor },
                { "ready", node.Ready }, { "value", node.Value }, { "predicateCount", node.PredicateCount },
                { "applyCount", node.ApplyCount }, { "lastInteger", node.LastInteger }, { "nodeNeighbor", node.Neighbor }
            };
        }

        private static TailNode Node(bool ready, bool value, int integer, int neighbor)
        {
            return new TailNode { Ready = ready, Value = value, LastInteger = integer, Neighbor = neighbor };
        }

        private static void Scenario(List<object> observations, int operation, string kind, bool ready,
            bool firstNull, bool secondNull, bool alias, bool ownerNull)
        {
            var first = Node(ready, false, -11, 17);
            var second = alias ? first : Node(!ready, true, 13, -19);
            var owner = ownerNull ? null : new ConditionalOwner
            {
                First = firstNull ? null : first, Second = secondNull ? null : second, Neighbor = 23
            };
            Record(observations, operation, kind, owner, first, second);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(ConditionalOwner owner, int operation)
        {
            switch (operation)
            {
                case 0: owner.WhenReadyTrue(); break;
                case 1: owner.WhenReadyFalse(); break;
                case 2: owner.WhenNotReadyTrue(); break;
                case 3: owner.WhenNotReadyFalse(); break;
                case 4: owner.WhenReadyZero(); break;
                default: throw new ArgumentOutOfRangeException("operation");
            }
        }

        private static object State(TailNode node)
        {
            return new Dictionary<string, object>
            {
                { "ready", node.Ready }, { "value", node.Value }, { "predicateCount", node.PredicateCount },
                { "applyCount", node.ApplyCount }, { "lastInteger", node.LastInteger }, { "neighbor", node.Neighbor }
            };
        }

        private static void Record(List<object> observations, int operation, string kind,
            ConditionalOwner owner, TailNode first, TailNode second)
        {
            var exception = "none";
            try { Invoke(owner, operation); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", operation }, { "exception", exception },
                { "ownerNull", owner == null }, { "producerCount", owner == null ? null : (object)owner.ProducerCount },
                { "ownerNeighbor", owner == null ? null : (object)owner.Neighbor },
                { "firstNull", owner == null ? null : (object)(owner.First == null) },
                { "secondNull", owner == null ? null : (object)(owner.Second == null) },
                { "firstSameSecond", ReferenceEquals(first, second) },
                { "first", State(first) }, { "second", State(second) }
            });
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report")
                    continue;
                try { Write(arguments[index + 1], "player"); Application.Quit(0); }
                catch (Exception error) { Debug.LogException(error); Application.Quit(1); }
                return;
            }
        }
    }
}
