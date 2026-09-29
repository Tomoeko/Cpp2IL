using System;
using System.Collections.Generic;
using System.IO;
using FinalOverrideBooleanGetterFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var freshState = new FlagState();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "default-state" },
                { "flag", freshState.Flag },
                { "baseValue", ((FlagBase)freshState).Value },
                { "concreteValue", freshState.Value },
                { "baseReferenceNull", freshState.BaseReference == null },
                { "baseNeighbor", freshState.BaseNeighbor },
                { "referenceNull", freshState.Reference == null },
                { "neighbor", freshState.Neighbor }
            });

            var freshShadow = new ShadowState();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "default-shadow" },
                { "flag", freshShadow.Flag },
                { "shadowFlag", freshShadow.ShadowFlag },
                { "baseValue", ((FlagBase)freshShadow).Value },
                { "middleValue", ((FlagState)freshShadow).Value },
                { "shadowValue", freshShadow.Value },
                { "baseReferenceNull", freshShadow.BaseReference == null },
                { "baseNeighbor", freshShadow.BaseNeighbor },
                { "referenceNull", freshShadow.Reference == null },
                { "neighbor", freshShadow.Neighbor },
                { "shadowNeighbor", freshShadow.ShadowNeighbor }
            });

            FlagBase missingBase = null;
            var baseFailure = "none";
            try { _ = missingBase.Value; }
            catch (Exception error) { baseFailure = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "null-base" }, { "failure", baseFailure }
            });

            ShadowState missingShadow = null;
            var shadowFailure = "none";
            try { _ = missingShadow.Value; }
            catch (Exception error) { shadowFailure = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "null-shadow" }, { "failure", shadowFailure }
            });

            RecordState(observations, false);
            RecordState(observations, true);
            foreach (var flag in new[] { false, true })
                foreach (var shadowFlag in new[] { false, true })
                    RecordShadow(observations, flag, shadowFlag);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "final-override-boolean-getter" },
                { "observations", observations }
            }));
        }

        private static void RecordState(List<object> observations, bool flag)
        {
            var baseReference = new object();
            var reference = new object();
            var state = new FlagState
            {
                Flag = flag, BaseReference = baseReference, BaseNeighbor = -31,
                Reference = reference, Neighbor = 47
            };
            FlagBase baseView = state;
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "state" }, { "inputFlag", flag },
                { "baseValue", baseView.Value },
                { "concreteValue", state.Value },
                { "repeatValue", baseView.Value },
                { "aliasSame", ReferenceEquals(baseView, state) },
                { "flagAfter", state.Flag },
                { "baseReferenceSame", ReferenceEquals(baseReference, state.BaseReference) },
                { "baseNeighbor", state.BaseNeighbor },
                { "referenceSame", ReferenceEquals(reference, state.Reference) },
                { "neighbor", state.Neighbor }
            });
        }

        private static void RecordShadow(List<object> observations, bool flag,
            bool shadowFlag)
        {
            var baseReference = new object();
            var reference = new object();
            var shadow = new ShadowState
            {
                Flag = flag, ShadowFlag = shadowFlag,
                BaseReference = baseReference, BaseNeighbor = -31,
                Reference = reference, Neighbor = 47, ShadowNeighbor = -59
            };
            FlagBase baseView = shadow;
            FlagState middleView = shadow;
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "shadow" }, { "inputFlag", flag },
                { "inputShadowFlag", shadowFlag },
                { "baseValue", baseView.Value },
                { "middleValue", middleView.Value },
                { "shadowValue", shadow.Value },
                { "repeatBaseValue", baseView.Value },
                { "repeatShadowValue", shadow.Value },
                { "baseAliasSame", ReferenceEquals(baseView, shadow) },
                { "middleAliasSame", ReferenceEquals(middleView, shadow) },
                { "flagAfter", shadow.Flag },
                { "shadowFlagAfter", shadow.ShadowFlag },
                { "baseReferenceSame", ReferenceEquals(baseReference, shadow.BaseReference) },
                { "baseNeighbor", shadow.BaseNeighbor },
                { "referenceSame", ReferenceEquals(reference, shadow.Reference) },
                { "neighbor", shadow.Neighbor },
                { "shadowNeighbor", shadow.ShadowNeighbor }
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
