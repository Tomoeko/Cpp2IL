using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using CallResultBooleanTailFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object> { Declarations() };
            var witness = new TailNode { Value = true };
            var owner = new TailOwner { Node = witness };
            var alias = new TailOwner { Node = witness };
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "defaults" }, { "ownerNodeNull", new TailOwner().Node == null },
                { "ownerCount", new TailOwner().ProducerCount },
                { "nodeValue", new TailNode().Value }, { "nodeCount", new TailNode().ApplyCount }
            });
            for (var operation = 0; operation < 6; operation++)
            {
                Record(observations, "success-" + operation, owner, witness, alias, operation, true);
                Record(observations, "repeat-" + operation, owner, witness, alias, operation, false);
            }
            Record(observations, "alias-true", alias, witness, owner, 5, true);
            owner.Node = null;
            for (var operation = 0; operation < 6; operation++)
                Record(observations, "null-result-" + operation, owner, witness, alias, operation, true);
            for (var operation = 0; operation < 6; operation++)
                Record(observations, "null-owner-" + operation, null, witness, alias, operation, true);
            owner.Node = witness;
            Record(observations, "reuse-false", owner, witness, alias, 2, false);
            Record(observations, "reuse-true", owner, witness, alias, 5, true);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "call-result-boolean-tail" }, { "observations", observations }
            }));
        }

        private static object Declarations()
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public |
                BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var owner = typeof(TailOwner);
            var node = typeof(TailNode);
            var calls = typeof(StaticCalls);
            return new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "methods", owner.GetMethods(flags).Length + owner.GetConstructors(flags).Length +
                    node.GetMethods(flags).Length + node.GetConstructors(flags).Length + calls.GetMethods(flags).Length },
                { "fields", owner.GetFields(flags).Length + node.GetFields(flags).Length },
                { "producerReturn", owner.GetMethod("GetNode").ReturnType.FullName },
                { "instanceParameter", owner.GetMethod("ForwardParameter").GetParameters()[0].ParameterType.FullName },
                { "staticReceiver", calls.GetMethod("ForwardParameter").GetParameters()[0].ParameterType.FullName },
                { "staticParameter", calls.GetMethod("ForwardParameter").GetParameters()[1].ParameterType.FullName }
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(TailOwner owner, int operation, bool value)
        {
            switch (operation)
            {
                case 0: owner.ForwardFalse(); break;
                case 1: owner.ForwardTrue(); break;
                case 2: owner.ForwardParameter(value); break;
                case 3: StaticCalls.ForwardFalse(owner); break;
                case 4: StaticCalls.ForwardTrue(owner); break;
                case 5: StaticCalls.ForwardParameter(owner, value); break;
                default: throw new ArgumentOutOfRangeException("operation");
            }
        }

        private static void Record(List<object> observations, string kind, TailOwner owner,
            TailNode witness, TailOwner alias, int operation, bool value)
        {
            var exception = "none";
            try { Invoke(owner, operation, value); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", operation }, { "argument", value },
                { "exception", exception }, { "ownerNull", owner == null },
                { "producerCount", owner == null ? null : (object)owner.ProducerCount },
                { "nodeNull", owner == null ? null : (object)(owner.Node == null) },
                { "nodeSameWitness", owner == null ? null : (object)ReferenceEquals(owner.Node, witness) },
                { "value", witness.Value }, { "applyCount", witness.ApplyCount },
                { "aliasSameWitness", ReferenceEquals(alias.Node, witness) }, { "aliasCount", alias.ProducerCount }
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
