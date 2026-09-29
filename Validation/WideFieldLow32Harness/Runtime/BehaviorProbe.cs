using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using WideFieldLow32Fixture;

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

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var freshSigned = new SignedState();
            var freshUnsigned = new UnsignedState();
            ConstructorRow(observations, "signed", Snapshot(freshSigned));
            ConstructorRow(observations, "unsigned", Snapshot(freshUnsigned));
            foreach (var high in HighWords)
            foreach (var low in LowWords)
            foreach (var neighbor in new[] { int.MinValue, 0, int.MaxValue })
            foreach (var hasReference in new[] { false, true })
            {
                var bits = ((ulong)high << 32) | low;
                var witness = hasReference ? new object() : null;
                var signed = new SignedState { Value = unchecked((long)bits), Neighbor = neighbor, Reference = witness };
                var unsigned = new UnsignedState { Value = bits, Neighbor = neighbor, Reference = witness };
                Record(observations, "signed", "signed", () => signed.ReadSigned(), () => Snapshot(signed));
                Record(observations, "signed", "unsigned", () => signed.ReadUnsigned(), () => Snapshot(signed));
                Record(observations, "unsigned", "signed", () => unsigned.ReadSigned(), () => Snapshot(unsigned));
                Record(observations, "unsigned", "unsigned", () => unsigned.ReadUnsigned(), () => Snapshot(unsigned));
            }
            SignedState missingSigned = null;
            UnsignedState missingUnsigned = null;
            RecordNull(observations, "signed", "signed", () => missingSigned.ReadSigned());
            RecordNull(observations, "signed", "unsigned", () => missingSigned.ReadUnsigned());
            RecordNull(observations, "unsigned", "signed", () => missingUnsigned.ReadSigned());
            RecordNull(observations, "unsigned", "unsigned", () => missingUnsigned.ReadUnsigned());
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "wide-field-low32" }, { "observations", observations }
            }));
        }

        private sealed class StateSnapshot
        {
            public ulong Bits;
            public int Neighbor;
            public object Reference;
        }

        private static StateSnapshot Snapshot(SignedState state)
        {
            return new StateSnapshot { Bits = unchecked((ulong)state.Value), Neighbor = state.Neighbor, Reference = state.Reference };
        }

        private static StateSnapshot Snapshot(UnsignedState state)
        {
            return new StateSnapshot { Bits = state.Value, Neighbor = state.Neighbor, Reference = state.Reference };
        }

        private static void ConstructorRow(List<object> observations, string owner, StateSnapshot state)
        {
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructor" }, { "owner", owner }, { "bits", state.Bits },
                { "neighbor", state.Neighbor }, { "referenceNull", state.Reference == null }
            });
        }

        private static void Record(List<object> observations, string owner, string route,
            Func<long> action, Func<StateSnapshot> snapshot)
        {
            var before = snapshot();
            long? result = null;
            var failure = "none";
            try { result = action(); }
            catch (Exception error) { failure = error.GetType().FullName; }
            var after = snapshot();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "read" }, { "owner", owner }, { "route", route },
                { "bitsBefore", before.Bits }, { "neighborBefore", before.Neighbor },
                { "referenceNullBefore", before.Reference == null }, { "result", result }, { "failure", failure },
                { "bitsAfter", after.Bits }, { "neighborAfter", after.Neighbor },
                { "referenceSame", ReferenceEquals(before.Reference, after.Reference) }
            });
        }

        private static void RecordNull(List<object> observations, string owner, string route, Func<long> action)
        {
            long? result = null;
            var failure = "none";
            try { result = action(); }
            catch (Exception error) { failure = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "null" }, { "owner", owner }, { "route", route },
                { "result", result }, { "failure", failure }
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
