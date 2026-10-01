using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using NativeNestedOwnerScalarParameterStoreFixture;
using UnityEngine;
using Holder = NativeNestedOwnerScalarParameterStoreFixture.Container.Holder;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly uint[] Values =
        {
            0, 0x80000000, 1, 0x80000001, 0x007fffff, 0x00800000, 0x7f7fffff,
            0x7f800000, 0xff800000, 0x7fc00001, 0xffc00123, 0x7f800002, 0x3fc00000
        };

        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
            var methods = 0;
            var fields = 0;
            var properties = 0;
            var signatures = new Dictionary<string, object>();
            var fieldTypes = new Dictionary<string, object>();
            var fieldAccess = new Dictionary<string, object>();
            var enclosing = new Dictionary<string, object>();
            var visibility = new Dictionary<string, object>();
            var types = new[] { typeof(Cell), typeof(Container), typeof(Holder) };
            foreach (var type in types)
            {
                methods += type.GetMethods(flags).Length + type.GetConstructors(flags).Length;
                fields += type.GetFields(flags).Length;
                properties += type.GetProperties(flags).Length;
                enclosing.Add(type.Name, type.DeclaringType == null ? "none" : type.DeclaringType.FullName);
                visibility.Add(type.Name, (int)(type.Attributes & TypeAttributes.VisibilityMask));
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
                    { "kind", "declarations" }, { "methods", methods }, { "fields", fields }, { "properties", properties },
                    { "types", types.Length }, { "signatures", signatures }, { "fieldTypes", fieldTypes },
                    { "fieldAccess", fieldAccess }, { "enclosing", enclosing }, { "visibility", visibility }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "targetNull", new Holder().Target == null }, { "cell", CellState(new Cell()) }
                }
            };
            for (var index = 0; index < Values.Length; index++)
            {
                foreach (var mode in new[] { "value", "null-target", "null-owner" })
                {
                    Fresh(out var firstCell, out var secondCell, out var first, out var second);
                    if (mode == "null-target") first.Target = null;
                    Record(rows, mode + "-" + index, mode == "null-owner" ? null : first, Values[index], first, second, firstCell, secondCell);
                }
            }
            Fresh(out var reusedFirstCell, out var reusedSecondCell, out var reusedFirst, out var reusedSecond);
            Record(rows, "reuse-first", reusedFirst, 0x80000000, reusedFirst, reusedSecond, reusedFirstCell, reusedSecondCell);
            Record(rows, "reuse-second", reusedSecond, 1, reusedFirst, reusedSecond, reusedFirstCell, reusedSecondCell);
            reusedFirst.Target = reusedSecondCell;
            Record(rows, "retarget-first", reusedFirst, 0x7fc00001, reusedFirst, reusedSecond, reusedFirstCell, reusedSecondCell);
            Record(rows, "shared-second", reusedSecond, 0xff800000, reusedFirst, reusedSecond, reusedFirstCell, reusedSecondCell);
            reusedFirst.Target = null;
            Record(rows, "reuse-null-target", reusedFirst, 0x3fc00000, reusedFirst, reusedSecond, reusedFirstCell, reusedSecondCell);
            reusedFirst.Target = reusedFirstCell;
            Record(rows, "restore-first", reusedFirst, 0x7f800002, reusedFirst, reusedSecond, reusedFirstCell, reusedSecondCell);
            Record(rows, "reuse-null-owner", null, 0xffc00123, reusedFirst, reusedSecond, reusedFirstCell, reusedSecondCell);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-nested-owner-scalar-parameter-store" }, { "observations", rows }
            }));
        }

        private static float FromBits(uint bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        private static uint Bits(float value) => unchecked((uint)BitConverter.ToInt32(BitConverter.GetBytes(value), 0));

        private static void Fresh(out Cell firstCell, out Cell secondCell, out Holder first, out Holder second)
        {
            firstCell = new Cell { Before = 11, Amount = FromBits(0x40400000), After = 29 };
            secondCell = new Cell { Before = 37, Amount = FromBits(0x40800000), After = 47 };
            first = new Holder { Target = firstCell };
            second = new Holder { Target = secondCell };
        }

        private static object CellState(Cell cell) => new Dictionary<string, object>
        {
            { "before", (int)cell.Before }, { "amountBits", Bits(cell.Amount) }, { "after", (int)cell.After }
        };

        private static string Identity(object value, object first, object second) =>
            value == null ? "null" : ReferenceEquals(value, first) ? "first" : ReferenceEquals(value, second) ? "second" : "other";

        private static object State(Holder first, Holder second, Cell firstCell, Cell secondCell) => new Dictionary<string, object>
        {
            { "firstTarget", Identity(first.Target, firstCell, secondCell) }, { "secondTarget", Identity(second.Target, firstCell, secondCell) },
            { "first", CellState(firstCell) }, { "second", CellState(secondCell) }
        };

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(Holder owner, uint incoming) => owner.StoreSingle(FromBits(incoming));

        private static void Record(List<object> rows, string kind, Holder owner, uint incoming,
            Holder first, Holder second, Cell firstCell, Cell secondCell)
        {
            var before = State(first, second, firstCell, secondCell);
            var exception = "none";
            try { Invoke(owner, incoming); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "receiver", Identity(owner, first, second) }, { "incoming", incoming },
                { "transportedIncomingBits", Bits(FromBits(incoming)) },
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
