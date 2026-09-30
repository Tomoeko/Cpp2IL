using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BooleanArrayFillLoopFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private const BindingFlags DeclaredMembers = BindingFlags.DeclaredOnly |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static;

        public static void Write(string path, string stage)
        {
            var observations = new List<object> { DeclarationFacts() };
            var fresh = new ArrayHolder();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "created", fresh != null },
                { "valuesIsNull", fresh.Values == null },
                { "neighbor", fresh.Neighbor }, { "flag", fresh.Flag }
            });

            foreach (var length in new[] { 0, 1, 2, 3, 31, 32, 33, 127, 128, 129 })
            {
                var values = Seed(length);
                var holder = new ArrayHolder { Values = values, Neighbor = 17, Flag = true };
                var alias = new ArrayHolder { Values = values, Neighbor = 23, Flag = false };
                Record(observations, "length-" + length + "-true", holder, values, false, true, alias);
                Record(observations, "length-" + length + "-false", holder, values, false, false, alias);
                Record(observations, "length-" + length + "-true-again", holder, values, false, true, alias);
                Record(observations, "length-" + length + "-literal", holder, values, true, false, alias);
                Record(observations, "length-" + length + "-literal-repeat", holder, values, true, false, alias);
            }

            var sharedValues = Seed(7);
            var primary = new ArrayHolder { Values = sharedValues, Neighbor = 31, Flag = false };
            var shared = new ArrayHolder { Values = sharedValues, Neighbor = 37, Flag = true };
            Record(observations, "shared-true", primary, sharedValues, false, true, shared);
            Record(observations, "shared-literal", shared, sharedValues, true, false, primary);

            var witness = Seed(5);
            var missing = new ArrayHolder { Values = null, Neighbor = 41, Flag = true };
            Record(observations, "null-array-parameter", missing, witness, false, true);
            Record(observations, "null-array-literal", missing, witness, true, false);
            missing.Values = witness;
            Record(observations, "reused-array-true", missing, witness, false, true);
            Record(observations, "reused-array-false", missing, witness, false, false);
            Record(observations, "reused-array-literal", missing, witness, true, false);
            Record(observations, "null-holder-parameter", null, witness, false, true);
            Record(observations, "null-holder-literal", null, witness, true, false);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() }, { "stage", stage },
                { "profile", "boolean-array-fill-loop" }, { "observations", observations }
            }));
        }

        private static bool[] Seed(int length)
        {
            var values = new bool[length];
            for (var index = 0; index < length; index++)
                values[index] = index % 3 != 1;
            return values;
        }

        private static object DeclarationFacts()
        {
            var type = typeof(ArrayHolder);
            var values = type.GetField("Values", DeclaredMembers);
            var neighbor = type.GetField("Neighbor", DeclaredMembers);
            var flag = type.GetField("Flag", DeclaredMembers);
            var literal = type.GetMethod("FillFalse", DeclaredMembers);
            var parameter = type.GetMethod("Fill", DeclaredMembers);
            var constructors = type.GetConstructors(DeclaredMembers);
            return new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "sealedClass", type.IsClass && type.IsPublic && type.IsSealed &&
                    !type.IsAbstract && type.BaseType == typeof(object) },
                { "threeFields", type.GetFields(DeclaredMembers).Length == 3 },
                { "twoMethods", type.GetMethods(DeclaredMembers).Length == 2 },
                { "oneConstructor", constructors.Length == 1 && constructors[0].IsPublic &&
                    !constructors[0].IsStatic && constructors[0].GetParameters().Length == 0 },
                { "valuesField", values != null && values.IsPublic && !values.IsStatic &&
                    values.FieldType == typeof(bool[]) },
                { "neighborField", neighbor != null && neighbor.IsPublic && !neighbor.IsStatic &&
                    neighbor.FieldType == typeof(int) },
                { "flagField", flag != null && flag.IsPublic && !flag.IsStatic &&
                    flag.FieldType == typeof(bool) },
                { "literalSignature", HasSignature(literal, false) },
                { "parameterSignature", HasSignature(parameter, true) }
            };
        }

        private static bool HasSignature(MethodInfo method, bool parameter)
        {
            if (method == null || !method.IsPublic || method.IsStatic ||
                method.IsVirtual || method.IsGenericMethod || method.ReturnType != typeof(void))
                return false;
            var parameters = method.GetParameters();
            return parameter ? parameters.Length == 1 && parameters[0].ParameterType == typeof(bool)
                : parameters.Length == 0;
        }

        private static void Record(List<object> observations, string kind,
            ArrayHolder holder, bool[] witness, bool literal, bool value,
            ArrayHolder alias = null)
        {
            var beforeValues = holder == null ? null : holder.Values;
            var before = Snapshot(beforeValues);
            var witnessBefore = Snapshot(witness);
            var exception = "none";
            try
            {
                if (literal) holder.FillFalse();
                else holder.Fill(value);
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", literal ? "literal-false" : "parameter" },
                { "value", literal ? null : (object)value }, { "exception", exception },
                { "before", before }, { "after", holder == null ? null : Snapshot(holder.Values) },
                { "witnessBefore", witnessBefore }, { "witnessAfter", Snapshot(witness) },
                { "holderIsNull", holder == null },
                { "valuesSameBefore", holder == null ? null : (object)ReferenceEquals(holder.Values, beforeValues) },
                { "valuesSameWitness", holder == null ? null : (object)ReferenceEquals(holder.Values, witness) },
                { "neighborAfter", holder == null ? null : (object)holder.Neighbor },
                { "flagAfter", holder == null ? null : (object)holder.Flag },
                { "aliasSameWitness", alias == null ? null : (object)ReferenceEquals(alias.Values, witness) },
                { "aliasAfter", alias == null ? null : Snapshot(alias.Values) },
                { "aliasNeighborAfter", alias == null ? null : (object)alias.Neighbor },
                { "aliasFlagAfter", alias == null ? null : (object)alias.Flag }
            });
        }

        private static bool[] Snapshot(bool[] values)
        {
            return values == null ? null : (bool[])values.Clone();
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report") continue;
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
