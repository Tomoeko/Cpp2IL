using System;
using System.Collections.Generic;
using System.IO;
using TripleLiteralGuardFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            Record(observations, "false", false, false, false, false, "left-");
            Record(observations, "true", false, false, false, true, "left-");
            Record(observations, "null-label", false, false, false, true, null);
            Record(observations, "first-null", true, false, false, true, "left-");
            Record(observations, "second-null", false, true, false, true, "left-");
            Record(observations, "third-null", false, false, true, true, "left-");
            Record(observations, "owner-null", false, false, false, true, "left-", true);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "triple-literal-guard" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind,
            bool firstNull, bool secondNull, bool thirdNull, bool flag,
            string label, bool ownerNull = false)
        {
            var first = new LabelNode { Label = label };
            var second = new LabelNode();
            var choice = new ChoiceCell { Flag = flag };
            second.Choice = thirdNull ? null : choice;
            var owner = ownerNull ? null : new LabelOwner
            {
                First = firstNull ? null : first,
                Second = secondNull ? null : second
            };
            string result = null;
            var exception = "none";
            try { result = owner.Compose(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "result", result },
                { "exception", exception },
                { "ownerIsNull", owner == null },
                { "firstIsNull", owner == null ? null : (object)(owner.First == null) },
                { "secondIsNull", owner == null ? null : (object)(owner.Second == null) },
                { "thirdIsNull", second.Choice == null },
                { "firstSameWitness", owner == null ? null :
                    (object)ReferenceEquals(owner.First, first) },
                { "secondSameWitness", owner == null ? null :
                    (object)ReferenceEquals(owner.Second, second) },
                { "getterCount", owner == null ? null : (object)owner.NodeGetterCount },
                { "nestedGetterCount", second.ChoiceGetterCount },
                { "flagAfter", choice.Flag },
                { "labelAfter", first.Label }
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
