using System;
using System.Collections.Generic;
using System.IO;
using ByRefIntegerHalvesFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly uint[] LowWords =
        {
            0, 1, 2, 0x7f, 0x80, 0x7fff, 0x8000, 0x7ffffffe, 0x7fffffff,
            0x80000000, 0xfffffffe, 0xffffffff
        };
        private static readonly uint[] HighWords =
        {
            0, 1, 0x7fffffff, 0x80000000, 0xffffffff, 0x12345678, 0x80000001, 0xffff0000
        };
        private static readonly string[] Routes =
        {
            "static-signed", "instance-signed", "static-unsigned", "instance-unsigned",
            "static-unsigned-to-signed", "instance-unsigned-to-signed"
        };

        public static void Write(string path, string stage)
        {
            var fresh = new SplitState();
            var observations = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "constructor" }, { "neighbor", fresh.Neighbor },
                    { "referenceNull", fresh.Reference == null }
                }
            };
            foreach (var high in HighWords)
            foreach (var low in LowWords)
            for (var initial = 0; initial < 3; initial++)
            foreach (var alias in new[] { false, true })
            foreach (var route in Routes)
            {
                var bits = ((ulong)high << 32) | low;
                var state = route.StartsWith("instance-", StringComparison.Ordinal)
                    ? new SplitState { Neighbor = new[] { -137, 0, 911 }[initial],
                        Reference = initial == 0 ? null : new object() }
                    : null;
                var highIndex = alias ? 0 : 1;
                if (route == "static-unsigned" || route == "instance-unsigned")
                {
                    var value = new uint[] { 0, 0x80000000, uint.MaxValue }[initial];
                    var storage = new[] { value, ~value, 0x01234567u, 0xfedcba98u };
                    Action action = route == "static-unsigned"
                        ? (Action)(() => Splitters.SplitUnsigned(bits, ref storage[0], ref storage[highIndex]))
                        : () => state.SplitUnsigned(bits, ref storage[0], ref storage[highIndex]);
                    Record(observations, route, bits, initial, alias, action, () => Snapshot(storage, state));
                }
                else
                {
                    var value = new[] { int.MinValue, 0, int.MaxValue }[initial];
                    var storage = new[] { value, ~value, int.MinValue + 73, int.MaxValue - 79 };
                    Action action;
                    if (route == "static-signed")
                        action = () => Splitters.SplitSigned(unchecked((long)bits), ref storage[0], ref storage[highIndex]);
                    else if (route == "instance-signed")
                        action = () => state.SplitSigned(unchecked((long)bits), ref storage[0], ref storage[highIndex]);
                    else if (route == "static-unsigned-to-signed")
                        action = () => Splitters.SplitUnsignedToSigned(bits, out storage[0], out storage[highIndex]);
                    else
                        action = () => state.SplitUnsignedToSigned(bits, out storage[0], out storage[highIndex]);
                    Record(observations, route, bits, initial, alias, action, () => Snapshot(storage, state));
                }
            }
            SplitState missing = null;
            foreach (var route in new[] { "instance-signed", "instance-unsigned", "instance-unsigned-to-signed" })
            {
                if (route == "instance-unsigned")
                {
                    var storage = new uint[] { 17, 29, 0x01234567, 0xfedcba98 };
                    RecordNull(observations, route, () => missing.SplitUnsigned(0x1234567887654321, ref storage[0], ref storage[1]),
                        () => Snapshot(storage, null));
                }
                else
                {
                    var storage = new[] { -17, 29, int.MinValue + 73, int.MaxValue - 79 };
                    Action action = route == "instance-signed"
                        ? (Action)(() => missing.SplitSigned(0x1234567887654321, ref storage[0], ref storage[1]))
                        : () => missing.SplitUnsignedToSigned(0x1234567887654321, out storage[0], out storage[1]);
                    RecordNull(observations, route, action, () => Snapshot(storage, null));
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "byref-integer-halves" }, { "observations", observations }
            }));
        }

        private sealed class StateSnapshot
        {
            public long Slot0;
            public long Slot1;
            public long Guard0;
            public long Guard1;
            public object Storage;
            public bool HasReceiver;
            public int? Neighbor;
            public object Reference;
        }

        private static StateSnapshot Snapshot(int[] storage, SplitState state)
        {
            return Snapshot(storage[0], storage[1], storage[2], storage[3], storage, state);
        }

        private static StateSnapshot Snapshot(uint[] storage, SplitState state)
        {
            return Snapshot(storage[0], storage[1], storage[2], storage[3], storage, state);
        }

        private static StateSnapshot Snapshot(long slot0, long slot1, long guard0, long guard1, object storage, SplitState state)
        {
            return new StateSnapshot { Slot0 = slot0, Slot1 = slot1, Guard0 = guard0, Guard1 = guard1, Storage = storage,
                HasReceiver = state != null, Neighbor = state == null ? (int?)null : state.Neighbor,
                Reference = state == null ? null : state.Reference };
        }

        private static Dictionary<string, object> StateRow(string kind, string route, Action action, Func<StateSnapshot> snapshot)
        {
            var before = snapshot();
            var failure = "none";
            try { action(); }
            catch (Exception error) { failure = error.GetType().FullName; }
            var after = snapshot();
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "route", route }, { "failure", failure },
                { "slot0Before", before.Slot0 }, { "slot1Before", before.Slot1 },
                { "slot0After", after.Slot0 }, { "slot1After", after.Slot1 },
                { "guard0Before", before.Guard0 }, { "guard1Before", before.Guard1 },
                { "guard0After", after.Guard0 }, { "guard1After", after.Guard1 },
                { "storageSame", ReferenceEquals(before.Storage, after.Storage) },
                { "receiverNeighborBefore", before.Neighbor }, { "receiverNeighborAfter", after.Neighbor },
                { "receiverReferenceNullBefore", before.HasReceiver ? (bool?)(before.Reference == null) : null },
                { "receiverReferenceSame", before.HasReceiver ? (bool?)ReferenceEquals(before.Reference, after.Reference) : null }
            };
        }

        private static void Record(List<object> observations, string route, ulong bits, int initial, bool alias,
            Action action, Func<StateSnapshot> snapshot)
        {
            var row = StateRow("split", route, action, snapshot);
            row.Add("bits", bits);
            row.Add("initial", initial);
            row.Add("alias", alias);
            observations.Add(row);
        }

        private static void RecordNull(List<object> observations, string route, Action action, Func<StateSnapshot> snapshot)
        {
            observations.Add(StateRow("null", route, action, snapshot));
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
