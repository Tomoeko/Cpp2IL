using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using FixedBooleanConjunctionFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var type = typeof(BooleanPair);
            var baseType = type.BaseType;
            var baseDefinition = baseType.GetGenericTypeDefinition();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly;
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "methods", type.GetMethods(flags).Length + type.GetConstructors(flags).Length + baseDefinition.GetConstructors(flags).Length },
                { "fields", type.GetFields(flags).Length },
                { "baseType", baseDefinition.FullName },
                { "baseArgument", baseType.GetGenericArguments()[0].FullName },
                { "baseGenericParameters", baseDefinition.GetGenericArguments().Length },
                { "baseFields", baseDefinition.GetFields(flags).Length },
                { "baseConstructorParameters", baseDefinition.GetConstructors(flags)[0].GetParameters().Length },
                { "firstType", type.GetField("First").FieldType.FullName },
                { "secondType", type.GetField("Second").FieldType.FullName },
                { "sentinelType", type.GetField("Sentinel").FieldType.FullName },
                { "atZeroParameters", type.GetMethod("BothFalseAtZero").GetParameters().Length },
                { "atOneParameters", type.GetMethod("BothFalseAtOne").GetParameters().Length },
                { "atZeroReturn", type.GetMethod("BothFalseAtZero").ReturnType.FullName },
                { "atOneReturn", type.GetMethod("BothFalseAtOne").ReturnType.FullName }
            });
            RecordCases(observations, 0);
            RecordCases(observations, 1);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "fixed-boolean-conjunction" },
                { "observations", observations }
            }));
        }

        private static bool[] Values(int index, bool value)
        {
            return index == 0 ? new[] { value } : new[] { !value, value };
        }

        private static void RecordCases(List<object> observations, int index)
        {
            Record(observations, "both-false", NewPair(Values(index, false), Values(index, false)), index);
            Record(observations, "second-true", NewPair(Values(index, false), Values(index, true)), index);
            Record(observations, "first-true", NewPair(Values(index, true), Values(index, false)), index);
            Record(observations, "both-true", NewPair(Values(index, true), Values(index, true)), index);
            Record(observations, "skip-second-null", NewPair(Values(index, true), null), index);
            Record(observations, "skip-second-empty", NewPair(Values(index, true), Array.Empty<bool>()), index);
            Record(observations, "first-null", NewPair(null, Values(index, false)), index);
            Record(observations, "first-null-second-empty", NewPair(null, Array.Empty<bool>()), index);
            Record(observations, "first-empty", NewPair(Array.Empty<bool>(), Values(index, false)), index);
            Record(observations, "first-empty-second-null", NewPair(Array.Empty<bool>(), null), index);
            Record(observations, "second-null", NewPair(Values(index, false), null), index);
            Record(observations, "second-empty", NewPair(Values(index, false), Array.Empty<bool>()), index);
            var shared = Values(index, false);
            var aliased = NewPair(shared, shared);
            Record(observations, "alias-false", aliased, index);
            shared[index] = true;
            if (index == 1)
                shared[0] = false;
            Record(observations, "alias-true", aliased, index);
            Record(observations, "owner-null", null, index);
            if (index == 1)
            {
                Record(observations, "first-short", NewPair(new[] { true }, null), index);
                Record(observations, "second-short", NewPair(Values(index, false), new[] { false }), index);
                Record(observations, "skip-second-short", NewPair(Values(index, true), new[] { false }), index);
                var shortShared = new[] { false };
                Record(observations, "alias-short", NewPair(shortShared, shortShared), index);
            }
        }

        private static BooleanPair NewPair(bool[] first, bool[] second)
        {
            return new BooleanPair { First = first, Second = second, Sentinel = 73 };
        }

        private static bool[] Snapshot(bool[] values)
        {
            return values == null ? null : (bool[])values.Clone();
        }

        private static void Record(List<object> observations, string kind, BooleanPair owner, int index)
        {
            var first = owner == null ? null : owner.First;
            var second = owner == null ? null : owner.Second;
            var firstBefore = Snapshot(first);
            var secondBefore = Snapshot(second);
            int? sentinelBefore = owner == null ? (int?)null : owner.Sentinel;
            bool? result = null;
            var exception = "none";
            try { result = index == 0 ? owner.BothFalseAtZero() : owner.BothFalseAtOne(); }
            catch (Exception error) { exception = error.GetType().FullName; }

            observations.Add(new Dictionary<string, object>
            {
                { "method", index == 0 ? "BothFalseAtZero" : "BothFalseAtOne" },
                { "kind", kind },
                { "result", result },
                { "exception", exception },
                { "firstBefore", firstBefore },
                { "secondBefore", secondBefore },
                { "firstAfter", Snapshot(owner == null ? null : owner.First) },
                { "secondAfter", Snapshot(owner == null ? null : owner.Second) },
                { "firstSecondSame", owner == null ? (bool?)null : ReferenceEquals(first, second) },
                { "firstFieldSame", owner == null ? (bool?)null : ReferenceEquals(first, owner.First) },
                { "secondFieldSame", owner == null ? (bool?)null : ReferenceEquals(second, owner.Second) },
                { "sentinelBefore", sentinelBefore },
                { "sentinelAfter", owner == null ? (int?)null : owner.Sentinel }
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
