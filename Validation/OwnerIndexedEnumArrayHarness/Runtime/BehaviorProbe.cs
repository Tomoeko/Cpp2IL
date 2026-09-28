using System;
using System.Collections.Generic;
using System.IO;
using OwnerIndexedEnumArrayFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private const int HighBit = unchecked((int)0x80000011);

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var length = 0; length <= 3; length++)
            {
                for (var index = -1; index <= 3; index++)
                {
                    observations.Add(ObserveA("length-" + length, index,
                        new ReaderA { Before = -53, Values = NewValues(length), Slot = index,
                            After = 59 }));
                    observations.Add(ObserveB("length-" + length, index,
                        new ReaderB { Before = -53, Spacer = -61,
                            Values = NewValues(length), Slot = index, After = 59 }));
                }
            }
            foreach (var index in new[] { -1, 0, 3 })
            {
                observations.Add(ObserveA("array-null", index,
                    new ReaderA { Before = -53, Values = null, Slot = index,
                        After = 59 }));
                observations.Add(ObserveB("array-null", index,
                    new ReaderB { Before = -53, Spacer = -61, Values = null,
                        Slot = index, After = 59 }));
                observations.Add(ObserveA("owner-null", index, null));
                observations.Add(ObserveB("owner-null", index, null));
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "owner-indexed-enum-array" },
                { "observations", observations }
            }));
        }

        private static Tone[] NewValues(int length)
        {
            var all = new[] { Tone.Negative, Tone.HighBit, Tone.Positive };
            var values = new Tone[length];
            Array.Copy(all, values, length);
            return values;
        }

        private static bool ValuesUnchanged(Tone[] values)
        {
            var all = new[] { Tone.Negative, Tone.HighBit, Tone.Positive };
            for (var index = 0; index < values.Length; index++)
                if (values[index] != all[index])
                    return false;
            return true;
        }

        private static object ObserveA(string kind, int index, ReaderA owner)
        {
            var values = owner == null ? null : owner.Values;
            return Observe(kind, "A", index, () => owner.Current, values,
                () => owner.Values, () => owner.Slot, () => owner.Before,
                () => null, () => owner.After, owner != null);
        }

        private static object ObserveB(string kind, int index, ReaderB owner)
        {
            var values = owner == null ? null : owner.Values;
            return Observe(kind, "B", index, () => owner.Current, values,
                () => owner.Values, () => owner.Slot, () => owner.Before,
                () => owner.Spacer, () => owner.After, owner != null);
        }

        private static object Observe(string kind, string ownerName, int index,
            Func<Tone> read, Tone[] values, Func<Tone[]> currentValues,
            Func<int> currentSlot, Func<long> currentBefore,
            Func<long?> currentSpacer, Func<long> currentAfter, bool hasOwner)
        {
            int? result = null;
            var exception = "none";
            try { result = (int)read(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "owner", ownerName }, { "index", index },
                { "result", result }, { "exception", exception },
                { "sameArray", hasOwner ?
                    (object)ReferenceEquals(values, currentValues()) : null },
                { "slotUnchanged", hasOwner ?
                    (object)(currentSlot() == index) : null },
                { "before", hasOwner ? (object)currentBefore() : null },
                { "spacer", hasOwner ? (object)currentSpacer() : null },
                { "after", hasOwner ? (object)currentAfter() : null },
                { "valuesUnchanged", values == null ? null :
                    (object)ValuesUnchanged(values) }
            };
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
