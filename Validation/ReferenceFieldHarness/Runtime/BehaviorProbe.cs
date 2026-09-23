using System;
using System.Collections.Generic;
using System.IO;
using ReferenceFieldFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var boxPrefix = new[] { int.MinValue, 0 };
            var boxSuffix = new[] { int.MaxValue, -17 };
            var outerPrefix = new[] { 11, 13 };
            var outerSuffix = new[] { -19, -23 };
            var box = new ReferenceBox { Prefix = boxPrefix, Suffix = boxSuffix };
            var outer = new ReferenceOuter
            {
                Prefix = outerPrefix, Inner = box, Suffix = outerSuffix
            };
            var derivedBox = new DerivedBox { Marker = int.MinValue };
            var derivedOuter = new DerivedOuter { Inner = derivedBox, Marker = long.MaxValue };
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructors" }, { "boxCreated", box != null }, { "outerCreated", outer != null },
                { "derivedBoxCreated", derivedBox != null }, { "derivedOuterCreated", derivedOuter != null }
            });
            var values = new[] { new string('q', 3), string.Empty, null };
            var labels = new[] { "value", "empty", "null-value" };
            for (var index = 0; index < values.Length; index++)
            {
                box.Text = values[index];
                Record(observations, "direct-" + labels[index], box, values[index], false);
                Record(observations, "nested-" + labels[index], outer, values[index], true);
            }
            Record(observations, "direct-null-owner", null, null, false);
            outer.Inner = null;
            Record(observations, "nested-null-inner", outer, null, true);
            Record(observations, "nested-null-outer", null, null, true);
            derivedBox.Text = new string('d', 4);
            Record(observations, "direct-derived-value", derivedBox, derivedBox.Text, false);
            Record(observations, "nested-derived-value", derivedOuter, derivedBox.Text, true);
            derivedBox.Text = null;
            Record(observations, "direct-derived-null-value", derivedBox, null, false);
            Record(observations, "nested-derived-null-value", derivedOuter, null, true);
            derivedOuter.Inner = null;
            Record(observations, "nested-derived-null-inner", derivedOuter, null, true);
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "derived-layout" }, { "boxMarker", derivedBox.Marker },
                { "outerMarker", derivedOuter.Marker }
            });
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "neighbor-arrays" },
                { "boxPrefix", box.Prefix }, { "boxSuffix", box.Suffix },
                { "outerPrefix", outer.Prefix }, { "outerSuffix", outer.Suffix },
                { "sameBoxPrefix", ReferenceEquals(box.Prefix, boxPrefix) },
                { "sameBoxSuffix", ReferenceEquals(box.Suffix, boxSuffix) },
                { "sameOuterPrefix", ReferenceEquals(outer.Prefix, outerPrefix) },
                { "sameOuterSuffix", ReferenceEquals(outer.Suffix, outerSuffix) }
            });
            outer.Inner = box;
            box.Payload = (object)int.MinValue;
            RecordObject(observations, "object-direct-boxed", box, box.Payload, false);
            RecordObject(observations, "object-nested-boxed", outer, box.Payload, true);
            box.Payload = new string('o', 3);
            RecordObject(observations, "object-direct-string", box, box.Payload, false);
            RecordObject(observations, "object-nested-string", outer, box.Payload, true);
            box.Payload = null;
            RecordObject(observations, "object-direct-null-value", box, null, false);
            RecordObject(observations, "object-nested-null-value", outer, null, true);
            RecordObject(observations, "object-direct-null-owner", null, null, false);
            outer.Inner = null;
            RecordObject(observations, "object-nested-null-inner", outer, null, true);
            RecordObject(observations, "object-nested-null-outer", null, null, true);
            outer.Inner = box;
            box.Numbers = new[] { int.MinValue, 0, int.MaxValue };
            RecordArray(observations, "array-direct-values", box, box.Numbers, false);
            RecordArray(observations, "array-nested-values", outer, box.Numbers, true);
            box.Numbers = Array.Empty<int>();
            RecordArray(observations, "array-direct-empty", box, box.Numbers, false);
            RecordArray(observations, "array-nested-empty", outer, box.Numbers, true);
            box.Numbers = null;
            RecordArray(observations, "array-direct-null-value", box, null, false);
            RecordArray(observations, "array-nested-null-value", outer, null, true);
            RecordArray(observations, "array-direct-null-owner", null, null, false);
            outer.Inner = null;
            RecordArray(observations, "array-nested-null-inner", outer, null, true);
            RecordArray(observations, "array-nested-null-outer", null, null, true);
            box.Text = new string('t', 4);
            box.Payload = (object)int.MaxValue;
            box.Numbers = new[] { int.MinValue, int.MaxValue };
            RecordCall(observations, "self-text-value", box.Text, () => box.ReadTextSelf());
            RecordCall(observations, "self-object-boxed", box.Payload, () => box.ReadObjectSelf());
            RecordCall(observations, "self-array-values", box.Numbers, () => box.ReadArraySelf());
            box.Text = null;
            box.Payload = null;
            box.Numbers = null;
            RecordCall(observations, "self-text-null-value", null, () => box.ReadTextSelf());
            RecordCall(observations, "self-object-null-value", null, () => box.ReadObjectSelf());
            RecordCall(observations, "self-array-null-value", null, () => box.ReadArraySelf());
            ReferenceBox nullBox = null;
            RecordCall(observations, "self-text-null-owner", null, () => nullBox.ReadTextSelf());
            RecordCall(observations, "self-object-null-owner", null, () => nullBox.ReadObjectSelf());
            RecordCall(observations, "self-array-null-owner", null, () => nullBox.ReadArraySelf());
            outer.Inner = box;
            RecordBox(observations, "class-self-value", box, () => outer.ReadBoxSelf());
            RecordBox(observations, "class-param-value", box, () => ReferenceReads.ReadBox(outer));
            outer.Inner = null;
            RecordBox(observations, "class-self-null-value", null, () => outer.ReadBoxSelf());
            RecordBox(observations, "class-param-null-value", null, () => ReferenceReads.ReadBox(outer));
            ReferenceOuter nullOuter = null;
            RecordBox(observations, "class-self-null-owner", null, () => nullOuter.ReadBoxSelf());
            RecordBox(observations, "class-param-null-owner", null, () => ReferenceReads.ReadBox(null));
            derivedOuter.Inner = derivedBox;
            RecordBox(observations, "class-self-derived", derivedBox, () => derivedOuter.ReadBoxSelf());
            RecordBox(observations, "class-param-derived", derivedBox, () => ReferenceReads.ReadBox(derivedOuter));
            var sharedBoxPrefix = new[] { -31 };
            var sharedBoxSuffix = new[] { 37 };
            var sharedOuterPrefix = new[] { -41 };
            var sharedOuterSuffix = new[] { 43 };
            var sharedBox = new SharedBox
            {
                Prefix = sharedBoxPrefix, Suffix = sharedBoxSuffix,
                Text = new string('s', 4)
            };
            var sharedOuter = new SharedOuter
            {
                Prefix = sharedOuterPrefix, Inner = sharedBox, Suffix = sharedOuterSuffix
            };
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "shared-constructors" }, { "boxCreated", sharedBox != null },
                { "outerCreated", sharedOuter != null }
            });
            RecordShared(observations, "shared-direct-value", sharedBox, sharedBox.Text, false);
            RecordShared(observations, "shared-nested-value", sharedOuter, sharedBox.Text, true);
            sharedBox.Text = null;
            RecordShared(observations, "shared-direct-null-value", sharedBox, null, false);
            RecordShared(observations, "shared-nested-null-value", sharedOuter, null, true);
            RecordShared(observations, "shared-direct-null-owner", null, null, false);
            sharedOuter.Inner = null;
            RecordShared(observations, "shared-nested-null-inner", sharedOuter, null, true);
            RecordShared(observations, "shared-nested-null-outer", null, null, true);
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "shared-neighbors" },
                { "boxPrefix", sharedBox.Prefix }, { "boxSuffix", sharedBox.Suffix },
                { "outerPrefix", sharedOuter.Prefix }, { "outerSuffix", sharedOuter.Suffix },
                { "sameBoxPrefix", ReferenceEquals(sharedBox.Prefix, sharedBoxPrefix) },
                { "sameBoxSuffix", ReferenceEquals(sharedBox.Suffix, sharedBoxSuffix) },
                { "sameOuterPrefix", ReferenceEquals(sharedOuter.Prefix, sharedOuterPrefix) },
                { "sameOuterSuffix", ReferenceEquals(sharedOuter.Suffix, sharedOuterSuffix) }
            });
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "reference-field" }, { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, object owner, string expected, bool nested)
            => RecordCall(observations, kind, expected,
                () => nested ? ((ReferenceOuter)owner).ReadInner() : ReferenceReads.Read((ReferenceBox)owner));

        private static void RecordShared(List<object> observations, string kind, object owner, string expected, bool nested)
            => RecordCall(observations, kind, expected,
                () => nested ? ((SharedOuter)owner).ReadInner() : ReferenceReads.ReadShared((SharedBox)owner));

        private static void RecordObject(List<object> observations, string kind, object owner, object expected, bool nested)
            => RecordCall(observations, kind, expected,
                () => nested ? ((ReferenceOuter)owner).ReadObjectInner() : ReferenceReads.ReadObject((ReferenceBox)owner));

        private static void RecordArray(List<object> observations, string kind, object owner, int[] expected, bool nested)
            => RecordCall(observations, kind, expected,
                () => nested ? ((ReferenceOuter)owner).ReadArrayInner() : ReferenceReads.ReadArray((ReferenceBox)owner));

        private static void RecordCall(List<object> observations, string kind, object expected, Func<object> read)
        {
            object result = null;
            var sameReference = false;
            var exception = "none";
            try
            {
                result = read();
                sameReference = ReferenceEquals(result, expected);
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "result", result },
                { "sameReference", sameReference }, { "exception", exception }
            });
        }

        private static void RecordBox(List<object> observations, string kind, ReferenceBox expected,
            Func<ReferenceBox> read)
        {
            ReferenceBox result = null;
            var sameReference = false;
            var exception = "none";
            try
            {
                result = read();
                sameReference = ReferenceEquals(result, expected);
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "resultIsNull", result == null },
                { "resultIsDerived", result is DerivedBox },
                { "sameReference", sameReference }, { "exception", exception }
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
