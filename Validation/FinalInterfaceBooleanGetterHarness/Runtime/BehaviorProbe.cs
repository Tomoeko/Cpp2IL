using System;
using System.Collections.Generic;
using System.IO;
using FinalInterfaceBooleanGetterFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Neighbors = { int.MinValue, 0, int.MaxValue };

        public static void Write(string path, string stage)
        {
            var observations = new List<object> { ConstructorRow() };
            foreach (var flag in new[] { false, true })
            foreach (var neighbor in Neighbors)
            foreach (var hasReference in new[] { false, true })
            {
                var witness = hasReference ? new object() : null;
                var ordinary = new FlagState { Flag = flag, Neighbor = neighbor, Reference = witness };
                IFlag ordinaryInterface = ordinary;
                Record(observations, "ordinary", "interface-value", () => ordinaryInterface.Value,
                    () => Snapshot(ordinary));
                Record(observations, "ordinary", "interface-read", () => ordinaryInterface.Read(),
                    () => Snapshot(ordinary));
                Record(observations, "ordinary", "concrete-value", () => ordinary.Value,
                    () => Snapshot(ordinary));
                Record(observations, "ordinary", "concrete-read", () => ordinary.Read(),
                    () => Snapshot(ordinary));

                var explicitState = new ExplicitFlagState { Flag = flag, Neighbor = neighbor, Reference = witness };
                IFlag explicitInterface = explicitState;
                Record(observations, "explicit", "interface-value", () => explicitInterface.Value,
                    () => Snapshot(explicitState));
                Record(observations, "explicit", "interface-read", () => explicitInterface.Read(),
                    () => Snapshot(explicitState));
            }

            foreach (var flag in new[] { false, true })
            foreach (var shadowFlag in new[] { false, true })
            foreach (var neighbor in Neighbors)
            foreach (var hasReference in new[] { false, true })
            {
                var witness = hasReference ? new object() : null;
                var shadow = new ShadowFlagState
                {
                    Flag = flag, Neighbor = neighbor, Reference = witness,
                    ShadowFlag = shadowFlag, ShadowNeighbor = ~neighbor, ShadowReference = witness
                };
                IFlag shadowInterface = shadow;
                FlagState baseState = shadow;
                Record(observations, "shadow", "interface-value", () => shadowInterface.Value,
                    () => Snapshot(shadow));
                Record(observations, "shadow", "interface-read", () => shadowInterface.Read(),
                    () => Snapshot(shadow));
                Record(observations, "shadow", "base-value", () => baseState.Value,
                    () => Snapshot(shadow));
                Record(observations, "shadow", "base-read", () => baseState.Read(),
                    () => Snapshot(shadow));
                Record(observations, "shadow", "shadow-value", () => shadow.Value,
                    () => Snapshot(shadow));
                Record(observations, "shadow", "shadow-read", () => shadow.Read(),
                    () => Snapshot(shadow));
            }

            FlagState missingOrdinary = null;
            ExplicitFlagState missingExplicit = null;
            ShadowFlagState missingShadow = null;
            RecordNull(observations, "ordinary", "interface-value", () => ((IFlag)missingOrdinary).Value);
            RecordNull(observations, "ordinary", "interface-read", () => ((IFlag)missingOrdinary).Read());
            RecordNull(observations, "ordinary", "concrete-value", () => missingOrdinary.Value);
            RecordNull(observations, "ordinary", "concrete-read", () => missingOrdinary.Read());
            RecordNull(observations, "explicit", "interface-value", () => ((IFlag)missingExplicit).Value);
            RecordNull(observations, "explicit", "interface-read", () => ((IFlag)missingExplicit).Read());
            RecordNull(observations, "shadow", "interface-value", () => ((IFlag)missingShadow).Value);
            RecordNull(observations, "shadow", "interface-read", () => ((IFlag)missingShadow).Read());
            RecordNull(observations, "shadow", "base-value", () => ((FlagState)missingShadow).Value);
            RecordNull(observations, "shadow", "base-read", () => ((FlagState)missingShadow).Read());
            RecordNull(observations, "shadow", "shadow-value", () => missingShadow.Value);
            RecordNull(observations, "shadow", "shadow-read", () => missingShadow.Read());

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "final-interface-boolean-getter" },
                { "observations", observations }
            }));
        }

        private static object ConstructorRow()
        {
            var ordinary = new FlagState();
            var explicitState = new ExplicitFlagState();
            var shadow = new ShadowFlagState();
            return new Dictionary<string, object>
            {
                { "kind", "constructors" },
                { "ordinaryFlag", ordinary.Flag }, { "ordinaryNeighbor", ordinary.Neighbor },
                { "ordinaryReferenceNull", ordinary.Reference == null },
                { "explicitFlag", explicitState.Flag }, { "explicitNeighbor", explicitState.Neighbor },
                { "explicitReferenceNull", explicitState.Reference == null },
                { "baseFlag", shadow.Flag }, { "baseNeighbor", shadow.Neighbor },
                { "baseReferenceNull", shadow.Reference == null },
                { "shadowFlag", shadow.ShadowFlag }, { "shadowNeighbor", shadow.ShadowNeighbor },
                { "shadowReferenceNull", shadow.ShadowReference == null }
            };
        }

        private sealed class StateSnapshot
        {
            public bool Flag;
            public int Neighbor;
            public object Reference;
            public bool? ShadowFlag;
            public int? ShadowNeighbor;
            public object ShadowReference;
        }

        private static StateSnapshot Snapshot(FlagState state)
        {
            return new StateSnapshot { Flag = state.Flag, Neighbor = state.Neighbor, Reference = state.Reference };
        }

        private static StateSnapshot Snapshot(ExplicitFlagState state)
        {
            return new StateSnapshot { Flag = state.Flag, Neighbor = state.Neighbor, Reference = state.Reference };
        }

        private static StateSnapshot Snapshot(ShadowFlagState state)
        {
            return new StateSnapshot
            {
                Flag = state.Flag, Neighbor = state.Neighbor, Reference = state.Reference,
                ShadowFlag = state.ShadowFlag, ShadowNeighbor = state.ShadowNeighbor,
                ShadowReference = state.ShadowReference
            };
        }

        private static void Record(List<object> observations, string owner, string route,
            Func<bool> action, Func<StateSnapshot> snapshot)
        {
            var before = snapshot();
            bool? result = null;
            var failure = "none";
            try { result = action(); }
            catch (Exception error) { failure = error.GetType().FullName; }
            var after = snapshot();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "read" }, { "owner", owner }, { "route", route },
                { "flagBefore", before.Flag }, { "neighborBefore", before.Neighbor },
                { "referenceNullBefore", before.Reference == null },
                { "shadowFlagBefore", before.ShadowFlag }, { "shadowNeighborBefore", before.ShadowNeighbor },
                { "shadowReferenceNullBefore", before.ShadowFlag.HasValue ? (bool?)(before.ShadowReference == null) : null },
                { "result", result }, { "failure", failure },
                { "flagAfter", after.Flag }, { "neighborAfter", after.Neighbor },
                { "referenceSame", ReferenceEquals(before.Reference, after.Reference) },
                { "shadowFlagAfter", after.ShadowFlag }, { "shadowNeighborAfter", after.ShadowNeighbor },
                { "shadowReferenceSame", before.ShadowFlag.HasValue ? (bool?)ReferenceEquals(before.ShadowReference, after.ShadowReference) : null }
            });
        }

        private static void RecordNull(List<object> observations, string owner, string route, Func<bool> action)
        {
            bool? result = null;
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
