using System;
using System.Collections.Generic;
using System.IO;
using FieldBooleanArrayReadFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] BoundaryIndices =
            { int.MinValue, -1, 0, 1, 2, int.MaxValue };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var owner = NewOwner();
            foreach (var slot in new[] { 0, 1, 2 })
                foreach (var index in BoundaryIndices)
                    Record(observations, "initial", slot, index, owner);

            owner.First[0] = true;
            owner.Second[0] = false;
            owner.Late[0] = true;
            foreach (var slot in new[] { 0, 1, 2 })
                Record(observations, "mutated", slot, 0, owner);

            var shared = new[] { true, false };
            owner.First = shared;
            owner.Second = shared;
            owner.Late = shared;
            foreach (var slot in new[] { 0, 1, 2 })
                Record(observations, "aliased", slot, 1, owner);
            shared[1] = true;
            foreach (var slot in new[] { 0, 1, 2 })
                Record(observations, "alias-mutated", slot, 1, owner);

            owner.First = Array.Empty<bool>();
            owner.Second = Array.Empty<bool>();
            owner.Late = Array.Empty<bool>();
            foreach (var slot in new[] { 0, 1, 2 })
                Record(observations, "empty", slot, 0, owner);

            owner.First = null;
            owner.Second = null;
            owner.Late = null;
            foreach (var slot in new[] { 0, 1, 2 })
            {
                Record(observations, "array-null", slot, 0, owner);
                Record(observations, "owner-null", slot, 0, null);
            }

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "field-boolean-array-read" },
                { "observations", observations }
            }));
        }

        private static BooleanArrayOwner NewOwner()
        {
            return new BooleanArrayOwner
            {
                Before = -13, First = new[] { false, true },
                Between = 29, Second = new[] { true, false },
                Pad0 = 31, Pad1 = 32, Pad2 = 33, Pad3 = 34,
                Pad4 = 35, Pad5 = 36, Pad6 = 37,
                Late = new[] { false, true }
            };
        }

        private static void Record(List<object> observations, string kind, int slot,
            int index, BooleanArrayOwner owner)
        {
            bool? value = null;
            string exception = "none";
            try
            {
                switch (slot)
                {
                    case 0: value = owner.ReadFirst(index); break;
                    case 1: value = owner.ReadSecond(index); break;
                    default: value = owner.ReadLate(index); break;
                }
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "slot", slot }, { "index", index },
                { "value", value }, { "exception", exception },
                { "before", owner == null ? (int?)null : owner.Before },
                { "between", owner == null ? (int?)null : owner.Between },
                { "pad6", owner == null ? (long?)null : owner.Pad6 }
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
