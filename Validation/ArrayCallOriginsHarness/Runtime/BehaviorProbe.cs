using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using ArrayCallOriginsFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private const BindingFlags Declared = BindingFlags.Public | BindingFlags.NonPublic |
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;

        public static void Write(string path, string stage)
        {
            var rows = new List<object> { Declarations() };
            var fresh = new ArrayCallOperations();
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "valuesNull", fresh.Values == null },
                { "otherNull", fresh.Other == null }, { "producerCalls", fresh.ProducerCalls },
                { "effectCalls", fresh.EffectCalls }, { "neighbor", fresh.Neighbor }
            });
            var seed = new[] { 4, -7, int.MaxValue };
            foreach (var index in new[] { int.MinValue, -1, 0, 1, 2, 3, int.MaxValue })
                Observe(rows, "produced-" + index, "produced", seed, new[] { 6, 8, 1 }, index, 0, false);
            Observe(rows, "produced-null", "produced", null, seed, 0, 0, false);
            Observe(rows, "produced-empty", "produced", new int[0], seed, 0, 0, false);
            Observe(rows, "produced-null-owner", "produced", seed, seed, 0, 0, true);
            foreach (var operation in new[] { "capture", "after", "around" })
            {
                Observe(rows, operation + "-first", operation, seed, new[] { 6, 8, 1 }, 0, 99, false);
                Observe(rows, operation + "-overflow", operation, seed, new[] { 6, 8, 1 }, 2, int.MinValue, false);
                Observe(rows, operation + "-negative", operation, seed, seed, -1, 99, false);
                Observe(rows, operation + "-bounds", operation, seed, seed, 3, 99, false);
                Observe(rows, operation + "-null", operation, null, seed, 0, 99, false);
                Observe(rows, operation + "-empty", operation, new int[0], seed, 0, 99, false);
                Observe(rows, operation + "-null-owner", operation, seed, seed, 0, 99, true);
            }
            foreach (var index in new[] { -1, 0, 1, 2, 3 })
                Observe(rows, "pair-" + index, "pair", seed, new[] { 6, 8, 1 }, index, 0, false);
            Observe(rows, "pair-null-left", "pair", null, seed, 0, 0, false);
            Observe(rows, "pair-null-right", "pair", seed, null, 0, 0, false);
            Observe(rows, "pair-left-bounds-before-null-right", "pair", seed, null, 3, 0, false);
            Observe(rows, "pair-empty-right", "pair", seed, new int[0], 0, 0, false);
            Observe(rows, "pair-alias", "pair", seed, seed, 1, 0, false);
            Observe(rows, "pair-null-owner", "pair", seed, seed, 0, 0, true);
            Producers(rows);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "array-call-origins" }, { "observations", rows }
            }));
        }

        private static object Declarations()
        {
            var owner = typeof(ArrayCallOperations);
            var ordinary = owner.IsPublic && owner.IsSealed && owner.BaseType == typeof(object) &&
                owner.GetFields(Declared).Length == 5 && owner.GetMethods(Declared).Length == 8;
            var constructors = owner.GetConstructors(Declared);
            ordinary &= constructors.Length == 1 && constructors[0].IsPublic &&
                constructors[0].GetParameters().Length == 0;
            foreach (var name in new[] { "Values", "Other", "ProducerCalls", "EffectCalls", "Neighbor" })
            {
                var field = owner.GetField(name, Declared);
                ordinary &= field != null && field.IsPublic && !field.IsStatic &&
                    field.FieldType == (name == "Values" || name == "Other" ? typeof(int[]) : typeof(int));
            }
            return new Dictionary<string, object>
            {
                { "kind", "declarations" }, { "owner", ordinary },
                { "signatures", Signature(owner, "GetValues", false, typeof(int[])) &&
                    Signature(owner, "GetOther", false, typeof(int[])) &&
                    Signature(owner, "ChangeValue", false, typeof(void), typeof(int), typeof(int)) &&
                    Signature(owner, "ReadProduced", true, typeof(int), owner, typeof(int)) &&
                    Signature(owner, "CaptureBeforeEffect", true, typeof(int), owner, typeof(int), typeof(int)) &&
                    Signature(owner, "ReadAfterEffect", true, typeof(int), owner, typeof(int), typeof(int)) &&
                    Signature(owner, "ReadAroundEffect", true, typeof(int), owner, typeof(int), typeof(int)) &&
                    Signature(owner, "ReadPairCaptured", true, typeof(int), owner, typeof(int)) }
            };
        }

        private static bool Signature(Type type, string name, bool isStatic, Type result, params Type[] arguments)
        {
            var method = type.GetMethod(name, Declared);
            if (method == null || !method.IsPublic || method.IsStatic != isStatic || method.IsVirtual ||
                method.IsGenericMethod || method.ReturnType != result) return false;
            var parameters = method.GetParameters();
            if (parameters.Length != arguments.Length) return false;
            for (var index = 0; index < parameters.Length; index++)
                if (parameters[index].ParameterType != arguments[index]) return false;
            return true;
        }

        private static void Observe(List<object> rows, string kind, string operation, int[] leftSeed,
            int[] rightSeed, int index, int value, bool missing)
        {
            var left = Snapshot(leftSeed);
            var right = ReferenceEquals(leftSeed, rightSeed) ? left : Snapshot(rightSeed);
            var owner = missing ? null : new ArrayCallOperations
                { Values = left, Other = right, ProducerCalls = 5, EffectCalls = 11, Neighbor = 37 };
            var beforeLeft = Snapshot(left);
            var beforeRight = Snapshot(right);
            object result = null;
            var exception = "none";
            try
            {
                if (operation == "produced") result = ArrayCallOperations.ReadProduced(owner, index);
                else if (operation == "capture") result = ArrayCallOperations.CaptureBeforeEffect(owner, index, value);
                else if (operation == "after") result = ArrayCallOperations.ReadAfterEffect(owner, index, value);
                else if (operation == "around") result = ArrayCallOperations.ReadAroundEffect(owner, index, value);
                else result = ArrayCallOperations.ReadPairCaptured(owner, index);
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", operation }, { "index", index }, { "value", value },
                { "result", result }, { "exception", exception },
                { "leftBefore", beforeLeft }, { "leftAfter", Snapshot(left) },
                { "rightBefore", beforeRight }, { "rightAfter", Snapshot(right) },
                { "producerCalls", owner == null ? null : (object)owner.ProducerCalls },
                { "effectCalls", owner == null ? null : (object)owner.EffectCalls },
                { "neighbor", owner == null ? null : (object)owner.Neighbor },
                { "alias", ReferenceEquals(left, right) },
                { "ownerLeftSame", owner == null ? null : (object)ReferenceEquals(owner.Values, left) },
                { "ownerRightSame", owner == null ? null : (object)ReferenceEquals(owner.Other, right) }
            });
        }

        private static void Producers(List<object> rows)
        {
            var left = new[] { 4 };
            var right = new[] { 6 };
            var owner = new ArrayCallOperations { Values = left, Other = right, Neighbor = 37 };
            var first = owner.GetValues();
            var second = owner.GetValues();
            var other = owner.GetOther();
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "producer-aliases" }, { "firstSame", ReferenceEquals(first, left) },
                { "secondSame", ReferenceEquals(second, left) }, { "otherSame", ReferenceEquals(other, right) },
                { "producerCalls", owner.ProducerCalls }, { "effectCalls", owner.EffectCalls },
                { "neighbor", owner.Neighbor }, { "left", Snapshot(left) }, { "right", Snapshot(right) }
            });
        }

        private static int[] Snapshot(int[] values) { return values == null ? null : (int[])values.Clone(); }

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
