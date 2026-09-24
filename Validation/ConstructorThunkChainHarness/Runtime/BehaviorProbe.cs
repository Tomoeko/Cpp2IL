using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ConstructorThunkChainFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object> { DeclarationFacts() };
            var first = new FirstLeaf();
            var second = new FirstLeaf();
            var third = new SecondLeaf();
            var reflected = (SecondLeaf)typeof(SecondLeaf)
                .GetConstructor(Type.EmptyTypes).Invoke(new object[0]);

            RecordInitial(observations, "first", first, typeof(FirstLeaf));
            RecordInitial(observations, "second", second, typeof(FirstLeaf));
            RecordInitial(observations, "third", third, typeof(SecondLeaf));
            RecordInitial(observations, "reflection", reflected, typeof(SecondLeaf));

            PayloadBase firstAsBase = first;
            FirstRoot firstAsRoot = first;
            var firstNeighbor = new object();
            var thirdNeighbor = new object();
            firstAsBase.Neighbor = firstNeighbor;
            third.Neighbor = thirdNeighbor;
            observations.Add(Row("identity", "receiver-alias",
                ReferenceEquals(firstAsBase, firstAsRoot) && ReferenceEquals(firstAsRoot, first)));
            observations.Add(Row("identity", "instances-distinct",
                !ReferenceEquals(first, second) && !ReferenceEquals(first, third) &&
                !ReferenceEquals(third, reflected)));
            observations.Add(Row("identity", "neighbor-alias",
                ReferenceEquals(first.Neighbor, firstNeighbor) &&
                ReferenceEquals(third.Neighbor, thirdNeighbor)));
            observations.Add(Row("identity", "neighbors-independent",
                second.Neighbor == null && reflected.Neighbor == null &&
                !ReferenceEquals(first.Neighbor, third.Neighbor)));
            observations.Add(Row("identity", "state-preserved",
                first.State == 29 && second.State == 29 && third.State == 29 &&
                reflected.State == 29));
            observations.Add(Row("identity", "untouched-default",
                first.Untouched == null && second.Untouched == null &&
                third.Untouched == null && reflected.Untouched == null));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "constructor-thunk-chain" },
                { "observations", observations }
            }));
        }

        private static object DeclarationFacts()
        {
            return new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "chainsExact", HasChain(typeof(FirstLeaf), typeof(FirstMiddle), typeof(FirstRoot)) &&
                    HasChain(typeof(SecondLeaf), typeof(SecondMiddle), typeof(SecondRoot)) },
                { "payloadFieldsExact", HasPayloadFields() },
                { "leafTypesDistinct", typeof(FirstLeaf) != typeof(SecondLeaf) }
            };
        }

        private static bool HasChain(Type leaf, Type middle, Type root)
        {
            return leaf.IsSealed && leaf.BaseType == middle && middle.BaseType == root &&
                root.BaseType == typeof(PayloadBase) &&
                HasOnlyParameterlessConstructor(leaf) &&
                HasOnlyParameterlessConstructor(middle) &&
                HasOnlyParameterlessConstructor(root);
        }

        private static bool HasOnlyParameterlessConstructor(Type type)
        {
            var constructors = type.GetConstructors(BindingFlags.DeclaredOnly |
                BindingFlags.Public | BindingFlags.Instance);
            return constructors.Length == 1 && constructors[0].GetParameters().Length == 0;
        }

        private static bool HasPayloadFields()
        {
            var fields = typeof(PayloadBase).GetFields(BindingFlags.DeclaredOnly |
                BindingFlags.Public | BindingFlags.Instance);
            return typeof(PayloadBase).BaseType == typeof(object) &&
                HasOnlyParameterlessConstructor(typeof(PayloadBase)) &&
                fields.Length == 3 &&
                HasField("State", typeof(int)) &&
                HasField("Neighbor", typeof(object)) &&
                HasField("Untouched", typeof(object));
        }

        private static bool HasField(string name, Type fieldType)
        {
            var field = typeof(PayloadBase).GetField(name, BindingFlags.DeclaredOnly |
                BindingFlags.Public | BindingFlags.Instance);
            return field != null && field.FieldType == fieldType && !field.IsInitOnly;
        }

        private static void RecordInitial(List<object> observations, string name,
            PayloadBase instance, Type exactType)
        {
            observations.Add(new Dictionary<string, object>
            {
                { "kind", name },
                { "typeExact", instance.GetType() == exactType },
                { "state", instance.State },
                { "neighborInitiallyNull", instance.Neighbor == null },
                { "untouchedInitiallyNull", instance.Untouched == null }
            });
        }

        private static object Row(string subject, string check, bool result) =>
            new Dictionary<string, object>
            {
                { "subject", subject }, { "check", check }, { "result", result }
            };

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report")
                    continue;
                try
                {
                    Write(arguments[index + 1], "player");
                    Application.Quit(0);
                }
                catch (Exception error)
                {
                    Debug.LogException(error);
                    Application.Quit(1);
                }
                return;
            }
        }
    }
}
