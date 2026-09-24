using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using UnityEngine;
using VirtualStringCallFixture;

namespace RecoveryValidation
{
    public sealed class FurtherNode : DerivedNode
    {
        public int Extra;
    }

    public sealed class OverriddenLabelOwner : VirtualLabelOwner
    {
        public int OverrideCalls;

        public override string Label
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get
            {
                OverrideCalls++;
                return "override";
            }
        }
    }

    public static class BehaviorProbe
    {
        private static readonly FieldInfo CurrentField = typeof(NodeOwner).GetField(
            "_current", BindingFlags.Instance | BindingFlags.NonPublic);

        public static void Write(string path, string stage)
        {
            if (CurrentField == null)
                throw new InvalidOperationException("The private source field was not retained.");

            var observations = new List<object>();
            RecordConstructorOrder(observations);
            RecordInitialization(observations);

            var node = new FurtherNode
            {
                Text = "pre:", Marker = 11, Detail = 13, Extra = 17
            };
            var owner = new VirtualLabelOwner { Neighbor = 19 };
            SetCurrent(owner, node);
            Record(observations, "normal-first", owner, node);
            Record(observations, "normal-second", owner, node);

            var missingNode = new VirtualLabelOwner { Neighbor = 23 };
            Record(observations, "null-node", missingNode, null);

            var wrongType = new ChainNode { Text = "ignored:", Marker = 29 };
            var wrongOwner = new VirtualLabelOwner { Neighbor = 31 };
            SetCurrent(wrongOwner, wrongType);
            Record(observations, "wrong-type", wrongOwner, wrongType);

            var missingText = new DerivedNode { Marker = 37, Detail = 41 };
            var missingTextOwner = new VirtualLabelOwner { Neighbor = 43 };
            SetCurrent(missingTextOwner, missingText);
            Record(observations, "null-text", missingTextOwner, missingText);

            Record(observations, "null-owner", null, null);

            var overrideNode = new DerivedNode { Text = "unused:", Marker = 47, Detail = 53 };
            var overridden = new OverriddenLabelOwner { Neighbor = 59 };
            SetCurrent(overridden, overrideNode);
            VirtualLabelOwner baseReference = overridden;
            Record(observations, "override-through-base", baseReference, overrideNode);
            Record(observations, "override-direct", overridden, overrideNode);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "virtual-string-call" },
                { "observations", observations }
            }));
        }

        private static void RecordConstructorOrder(List<object> observations)
        {
            var outerBefore = InitializationWitness.OuterRuns;
            var middleBefore = InitializationWitness.MiddleRuns;
            var first = new DerivedNode();
            var outerAfterFirst = InitializationWitness.OuterRuns;
            var middleAfterFirst = InitializationWitness.MiddleRuns;
            var outerOrderAfterFirst = InitializationWitness.OuterOrder;
            var middleOrderAfterFirst = InitializationWitness.MiddleOrder;
            var second = new DerivedNode();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "cold-construction" },
                { "outerBefore", outerBefore }, { "middleBefore", middleBefore },
                { "outerAfterFirst", outerAfterFirst },
                { "middleAfterFirst", middleAfterFirst },
                { "outerOrderAfterFirst", outerOrderAfterFirst },
                { "middleOrderAfterFirst", middleOrderAfterFirst },
                { "outerAfterSecond", InitializationWitness.OuterRuns },
                { "middleAfterSecond", InitializationWitness.MiddleRuns },
                { "outerOrderAfterSecond", InitializationWitness.OuterOrder },
                { "middleOrderAfterSecond", InitializationWitness.MiddleOrder },
                { "nextOrderAfterSecond", InitializationWitness.NextOrder },
                { "distinctInstances", !ReferenceEquals(first, second) },
                { "firstDetail", first.Detail }, { "secondDetail", second.Detail }
            });
        }

        private static void RecordInitialization(List<object> observations)
        {
            var outerBefore = InitializationWitness.OuterRuns;
            var middleBefore = InitializationWitness.MiddleRuns;
            var owner = new VirtualLabelOwner { Neighbor = 5 };
            var exception = "none";
            try { var ignored = owner.Label; }
            catch (Exception error) { exception = error.GetType().FullName; }
            var outerAfterCast = InitializationWitness.OuterRuns;
            var middleAfterCast = InitializationWitness.MiddleRuns;
            var wrongType = new ChainNode { Text = "unchanged:", Marker = 7 };
            var outerAfterConstruction = InitializationWitness.OuterRuns;
            var middleAfterConstruction = InitializationWitness.MiddleRuns;
            SetCurrent(owner, wrongType);
            var wrongTypeException = "none";
            try { var ignored = owner.Label; }
            catch (Exception error) { wrongTypeException = error.GetType().FullName; }
            var outerAfterNonNullCast = InitializationWitness.OuterRuns;
            var middleAfterNonNullCast = InitializationWitness.MiddleRuns;
            var middleTrigger = MiddleNode.MiddleTrigger;
            var outerAfterMiddle = InitializationWitness.OuterRuns;
            var middleAfterMiddle = InitializationWitness.MiddleRuns;
            var outerTrigger = OuterBase.OuterTrigger;
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "initialization-order" }, { "exception", exception },
                { "outerBefore", outerBefore }, { "middleBefore", middleBefore },
                { "outerAfterCast", outerAfterCast }, { "middleAfterCast", middleAfterCast },
                { "outerAfterConstruction", outerAfterConstruction },
                { "middleAfterConstruction", middleAfterConstruction },
                { "wrongTypeException", wrongTypeException },
                { "outerAfterNonNullCast", outerAfterNonNullCast },
                { "middleAfterNonNullCast", middleAfterNonNullCast },
                { "wrongTypeTextAfter", wrongType.Text },
                { "wrongTypeMarkerAfter", wrongType.Marker },
                { "middleTrigger", middleTrigger },
                { "outerAfterMiddle", outerAfterMiddle }, { "middleAfterMiddle", middleAfterMiddle },
                { "outerTrigger", outerTrigger },
                { "outerAfterExplicit", InitializationWitness.OuterRuns },
                { "middleAfterExplicit", InitializationWitness.MiddleRuns },
                { "neighborAfter", owner.Neighbor }
            });
        }

        private static void SetCurrent(NodeOwner owner, ChainNode node)
        {
            CurrentField.SetValue(owner, node);
        }

        private static ChainNode GetCurrent(NodeOwner owner)
        {
            return (ChainNode)CurrentField.GetValue(owner);
        }

        private static void Record(List<object> observations, string kind,
            VirtualLabelOwner owner, ChainNode expectedNode)
        {
            var neighborBefore = owner == null ? (int?)null : owner.Neighbor;
            var overrideBefore = owner is OverriddenLabelOwner derivedBefore
                ? (int?)derivedBefore.OverrideCalls : null;
            var result = (string)null;
            var exception = "none";
            try { result = owner.Label; }
            catch (Exception error) { exception = error.GetType().FullName; }
            var current = owner == null ? null : GetCurrent(owner);
            var overrideAfter = owner is OverriddenLabelOwner derivedAfter
                ? (int?)derivedAfter.OverrideCalls : null;
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "result", result }, { "exception", exception },
                { "neighborBefore", neighborBefore },
                { "neighborAfter", owner == null ? (int?)null : owner.Neighbor },
                { "overrideBefore", overrideBefore }, { "overrideAfter", overrideAfter },
                { "currentSame", owner == null ? (bool?)null : ReferenceEquals(current, expectedNode) },
                { "currentIsDerived", owner == null ? (bool?)null : current is DerivedNode },
                { "textAfter", expectedNode == null ? null : expectedNode.Text },
                { "markerAfter", expectedNode == null ? (int?)null : expectedNode.Marker },
                { "detailAfter", expectedNode is DerivedNode derivedNode ? (int?)derivedNode.Detail : null },
                { "extraAfter", expectedNode is FurtherNode furtherNode ? (int?)furtherNode.Extra : null },
                { "outerRunsAfter", InitializationWitness.OuterRuns },
                { "middleRunsAfter", InitializationWitness.MiddleRuns }
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
