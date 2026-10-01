using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeNestedScalarParameterStoreFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly string[] Operations = { "boolean", "int32", "uint32", "single" };
        private static readonly object[][] Values =
        {
            new object[] { false, true },
            new object[] { int.MinValue, int.MinValue + 1, -1, 0, 1, int.MaxValue - 1, int.MaxValue },
            new object[] { 0u, 1u, 255u, 65535u, 2147483647u, 2147483648u, uint.MaxValue },
            new object[] { 0u, 0x80000000u, 1u, 0x80000001u, 0x007fffffu, 0x00800000u,
                0x7f7fffffu, 0x7f800000u, 0xff800000u, 0x7fc00001u, 0xffc00123u, 0x7f800002u, 0x3fc00000u }
        };

        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
            var methods = 0;
            var fields = 0;
            var properties = 0;
            var signatures = new Dictionary<string, object>();
            var fieldTypes = new Dictionary<string, object>();
            var fieldAccess = new Dictionary<string, object>();
            var types = new[] { typeof(Cell), typeof(Holder) };
            foreach (var type in types)
            {
                methods += type.GetMethods(flags).Length + type.GetConstructors(flags).Length;
                fields += type.GetFields(flags).Length;
                properties += type.GetProperties(flags).Length;
                foreach (var method in type.GetMethods(flags))
                {
                    var signature = new List<string> { method.ReturnType.FullName };
                    foreach (var parameter in method.GetParameters()) signature.Add(parameter.ParameterType.FullName);
                    signatures.Add(type.Name + "." + method.Name, signature);
                }
                foreach (var field in type.GetFields(flags))
                {
                    fieldTypes.Add(type.Name + "." + field.Name, field.FieldType.FullName);
                    fieldAccess.Add(type.Name + "." + field.Name, (field.Attributes & FieldAttributes.FieldAccessMask).ToString());
                }
            }
            var rows = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" }, { "methods", methods }, { "fields", fields },
                    { "properties", properties }, { "types", types.Length }, { "signatures", signatures },
                    { "fieldTypes", fieldTypes }, { "fieldAccess", fieldAccess }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "targetNull", new Holder().Target == null }, { "cell", CellState(new Cell()) }
                }
            };
            for (var operation = 0; operation < Operations.Length; operation++)
            {
                for (var value = 0; value < Values[operation].Length; value++)
                {
                    foreach (var mode in new[] { "value", "null-target", "null-owner" })
                    {
                        Fresh(out var firstCell, out var secondCell, out var first, out var second);
                        if (mode == "null-target") first.Target = null;
                        Record(rows, Operations[operation] + "-" + mode + "-" + value, Operations[operation],
                            mode == "null-owner" ? null : first, Values[operation][value], first, second, firstCell, secondCell);
                    }
                }
            }
            Fresh(out var reusedFirstCell, out var reusedSecondCell, out var reusedFirst, out var reusedSecond);
            Record(rows, "reuse-signed-first", "int32", reusedFirst, -7, reusedFirst, reusedSecond, reusedFirstCell, reusedSecondCell);
            Record(rows, "reuse-unsigned-second", "uint32", reusedSecond, uint.MaxValue, reusedFirst, reusedSecond, reusedFirstCell, reusedSecondCell);
            Record(rows, "reuse-negative-zero", "single", reusedFirst, 0x80000000u, reusedFirst, reusedSecond, reusedFirstCell, reusedSecondCell);
            Record(rows, "reuse-boolean-second", "boolean", reusedSecond, false, reusedFirst, reusedSecond, reusedFirstCell, reusedSecondCell);
            reusedFirst.Target = reusedSecondCell;
            Record(rows, "retarget-first-to-second", "int32", reusedFirst, int.MaxValue, reusedFirst, reusedSecond, reusedFirstCell, reusedSecondCell);
            Record(rows, "shared-target-single", "single", reusedSecond, 0x7fc00001u, reusedFirst, reusedSecond, reusedFirstCell, reusedSecondCell);
            reusedFirst.Target = null;
            Record(rows, "reuse-null-target", "boolean", reusedFirst, true, reusedFirst, reusedSecond, reusedFirstCell, reusedSecondCell);
            reusedFirst.Target = reusedFirstCell;
            Record(rows, "retarget-first-back", "uint32", reusedFirst, 0u, reusedFirst, reusedSecond, reusedFirstCell, reusedSecondCell);
            Record(rows, "reuse-null-owner", "single", null, 0xff800000u, reusedFirst, reusedSecond, reusedFirstCell, reusedSecondCell);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-nested-scalar-parameter-store" }, { "observations", rows }
            }));
        }

        private static float FromBits(uint bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        private static uint Bits(float value) => unchecked((uint)BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        private static void Fresh(out Cell firstCell, out Cell secondCell, out Holder first, out Holder second)
        {
            firstCell = new Cell { Before = 11, Enabled = true, Signed = -13, Unsigned = 17, Amount = FromBits(0x40400000u), After = 29 };
            secondCell = new Cell { Before = 37, Enabled = false, Signed = 41, Unsigned = 43, Amount = FromBits(0x40800000u), After = 47 };
            first = new Holder { Target = firstCell };
            second = new Holder { Target = secondCell };
        }

        private static object CellState(Cell cell) => new Dictionary<string, object>
        {
            { "before", (int)cell.Before }, { "enabled", cell.Enabled }, { "signed", cell.Signed },
            { "unsigned", cell.Unsigned }, { "amountBits", Bits(cell.Amount) }, { "after", (int)cell.After }
        };

        private static string Identity(object value, object first, object second) =>
            value == null ? "null" : ReferenceEquals(value, first) ? "first" : ReferenceEquals(value, second) ? "second" : "other";

        private static object State(Holder first, Holder second, Cell firstCell, Cell secondCell) => new Dictionary<string, object>
        {
            { "firstTarget", Identity(first.Target, firstCell, secondCell) }, { "secondTarget", Identity(second.Target, firstCell, secondCell) },
            { "first", CellState(firstCell) }, { "second", CellState(secondCell) }
        };

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(Holder owner, string operation, object incoming)
        {
            switch (operation)
            {
                case "boolean": owner.StoreBoolean((bool)incoming); break;
                case "int32": owner.StoreInt32((int)incoming); break;
                case "uint32": owner.StoreUInt32((uint)incoming); break;
                case "single": owner.StoreSingle(FromBits((uint)incoming)); break;
                default: throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }

        private static void Record(List<object> rows, string kind, string operation, Holder owner, object incoming,
            Holder first, Holder second, Cell firstCell, Cell secondCell)
        {
            var before = State(first, second, firstCell, secondCell);
            var exception = "none";
            try { Invoke(owner, operation, incoming); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "operation", operation }, { "receiver", Identity(owner, first, second) }, { "incoming", incoming },
                { "transportedIncomingBits", operation == "single" ? (object)Bits(FromBits((uint)incoming)) : "not-used" },
                { "exception", exception }, { "before", before }, { "after", State(first, second, firstCell, secondCell) }
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
