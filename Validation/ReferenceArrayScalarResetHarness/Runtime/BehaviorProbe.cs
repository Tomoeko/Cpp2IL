using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using ReferenceArrayScalarResetFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly string[] BooleanCases =
        {
            "holder-null", "null-array", "empty", "one", "three", "null-first", "null-middle", "null-last", "alias"
        };
        private static readonly string[] SingleCases =
        {
            "holder-null", "null-array", "empty", "one", "short", "exact", "long", "null-first",
            "null-middle", "null-last", "null-after-limit", "alias", "tail-alias"
        };
        private static readonly uint[] SingleBits =
        {
            0x80000000, 0x7FC01234, 0x00000001, 0x80000001, 0x7F800000, 0xFF800000, 0x3F800000, 0xFFC05678
        };

        public static void Write(string path, string stage)
        {
            var identities = new Dictionary<object, int>();
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance;
            var methodCount = 0;
            var fieldCount = 0;
            var signatures = new Dictionary<string, object>();
            var fieldTypes = new Dictionary<string, object>();
            foreach (var type in new[] { typeof(BooleanEntry), typeof(SingleEntry), typeof(ResetHolder) })
            {
                methodCount += type.GetMethods(flags).Length + type.GetConstructors(flags).Length;
                fieldCount += type.GetFields(flags).Length;
                foreach (var field in type.GetFields(flags)) fieldTypes.Add(type.Name + "." + field.Name, field.FieldType.FullName);
                foreach (var method in type.GetMethods(flags))
                {
                    var signature = new List<string> { method.ReturnType.FullName };
                    foreach (var parameter in method.GetParameters()) signature.Add(parameter.ParameterType.FullName);
                    signatures.Add(type.Name + "." + method.Name, signature);
                }
            }
            var rows = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" }, { "methods", methodCount }, { "fields", fieldCount },
                    { "signatures", signatures }, { "fieldTypes", fieldTypes }
                },
                new Dictionary<string, object>
                {
                    { "kind", "defaults" }, { "markerBits", Bits(new ResetHolder().Marker) },
                    { "flagsNull", new ResetHolder().Flags == null }, { "valuesNull", new ResetHolder().Values == null },
                    { "active", new BooleanEntry().Active }, { "booleanNeighbor", new BooleanEntry().Neighbor },
                    { "valueBits", Bits(new SingleEntry().Value) }, { "singleNeighbor", new SingleEntry().Neighbor }
                }
            };
            foreach (var kind in BooleanCases) Record(rows, "active", kind, Scenario("active", kind, identities), identities);
            foreach (var kind in SingleCases) Record(rows, "values", kind, Scenario("values", kind, identities), identities);

            var holder = Owner(identities);
            holder.Flags = null;
            Record(rows, "active", "reuse-null-array", holder, identities);
            holder.Flags = MakeFlags(3, identities);
            var savedFlag = holder.Flags[1];
            holder.Flags[1] = null;
            Record(rows, "active", "reuse-null-middle", holder, identities);
            holder.Flags[1] = savedFlag;
            Record(rows, "active", "reuse-repaired", holder, identities);
            Record(rows, "active", "reuse-repeat", holder, identities);
            holder = Owner(identities);
            holder.Values = MakeValues(6, identities);
            Record(rows, "values", "reuse-short", holder, identities);
            holder.Values = MakeValues(7, identities);
            var savedValue = holder.Values[6];
            holder.Values[6] = null;
            Record(rows, "values", "reuse-null-last", holder, identities);
            holder.Values[6] = savedValue;
            Record(rows, "values", "reuse-repaired", holder, identities);
            Record(rows, "values", "reuse-repeat", holder, identities);
            var first = Owner(identities);
            var second = Owner(identities);
            second.Flags = first.Flags;
            second.Values = first.Values;
            Record(rows, "active", "shared-first", first, identities);
            first.Flags[1].Active = true;
            Record(rows, "active", "shared-second", second, identities);
            Record(rows, "values", "shared-first", first, identities);
            first.Values[1].Value = Single(0x80000001);
            Record(rows, "values", "shared-second", second, identities);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "reference-array-scalar-reset" }, { "observations", rows }
            }));
        }

        private static float Single(uint bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);
        private static int Bits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);

        private static BooleanEntry[] MakeFlags(int length, Dictionary<object, int> identities)
        {
            var result = new BooleanEntry[length];
            for (var index = 0; index < length; index++)
            {
                result[index] = new BooleanEntry { Active = true, Neighbor = 501 + index };
                identities.Add(result[index], index);
            }
            return result;
        }

        private static SingleEntry[] MakeValues(int length, Dictionary<object, int> identities)
        {
            var result = new SingleEntry[length];
            for (var index = 0; index < length; index++)
            {
                result[index] = new SingleEntry { Value = Single(SingleBits[index % SingleBits.Length]), Neighbor = -601 - index };
                identities.Add(result[index], index);
            }
            return result;
        }

        private static ResetHolder Owner(Dictionary<object, int> identities) => new ResetHolder
        {
            Marker = Single(0xFFC02468), Flags = MakeFlags(3, identities), Values = MakeValues(8, identities)
        };

        private static ResetHolder Scenario(string method, string kind, Dictionary<object, int> identities)
        {
            if (kind == "holder-null") return null;
            var holder = Owner(identities);
            var length = kind == "empty" ? 0 : kind == "one" ? 1 : kind == "three" ? 3 :
                         kind == "short" ? 6 : kind == "exact" ? 7 : method == "active" ? 3 : 8;
            if (method == "active")
            {
                holder.Flags = MakeFlags(length, identities);
                if (kind == "null-array") holder.Flags = null;
                if (kind == "null-first") holder.Flags[0] = null;
                if (kind == "null-middle") holder.Flags[1] = null;
                if (kind == "null-last") holder.Flags[2] = null;
                if (kind == "alias") holder.Flags[1] = holder.Flags[0];
            }
            else
            {
                holder.Values = MakeValues(length, identities);
                if (kind == "null-array") holder.Values = null;
                if (kind == "null-first") holder.Values[0] = null;
                if (kind == "null-middle") holder.Values[3] = null;
                if (kind == "null-last") holder.Values[6] = null;
                if (kind == "null-after-limit") holder.Values[7] = null;
                if (kind == "alias") holder.Values[1] = holder.Values[0];
                if (kind == "tail-alias") holder.Values[7] = holder.Values[0];
            }
            return holder;
        }

        private static object Snapshot(ResetHolder holder, Dictionary<object, int> identities)
        {
            if (holder == null) return null;
            List<object> flags = null;
            List<object> values = null;
            if (holder.Flags != null)
            {
                flags = new List<object>();
                foreach (var entry in holder.Flags) flags.Add(entry == null ? null : new Dictionary<string, object>
                {
                    { "identity", identities[entry] }, { "active", entry.Active }, { "neighbor", entry.Neighbor }
                });
            }
            if (holder.Values != null)
            {
                values = new List<object>();
                foreach (var entry in holder.Values) values.Add(entry == null ? null : new Dictionary<string, object>
                {
                    { "identity", identities[entry] }, { "valueBits", Bits(entry.Value) }, { "neighbor", entry.Neighbor }
                });
            }
            return new Dictionary<string, object> { { "markerBits", Bits(holder.Marker) }, { "flags", flags }, { "values", values } };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void Invoke(ResetHolder holder, string method)
        {
            if (method == "active") holder.ResetActive();
            else holder.ResetValues();
        }

        private static void Record(List<object> rows, string method, string kind, ResetHolder holder, Dictionary<object, int> identities)
        {
            var before = Snapshot(holder, identities);
            var flags = holder == null ? null : holder.Flags;
            var values = holder == null ? null : holder.Values;
            var exception = "none";
            try { Invoke(holder, method); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "method", method }, { "exception", exception }, { "before", before },
                { "after", Snapshot(holder, identities) }, { "flagsSame", holder != null && ReferenceEquals(flags, holder.Flags) },
                { "valuesSame", holder != null && ReferenceEquals(values, holder.Values) }
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
