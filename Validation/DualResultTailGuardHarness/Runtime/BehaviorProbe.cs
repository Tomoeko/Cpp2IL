using System;
using System.Collections.Generic;
using System.IO;
using DualResultTailGuardFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var original = new GuardOwner();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructors" },
                { "ownerCreated", original != null },
                { "firstCreated", new FirstNode() != null },
                { "secondCreated", new SecondNode() != null },
                { "firstIsNull", original.First == null },
                { "secondIsNull", original.Second == null },
                { "stage", original.Stage }
            });

            Record(observations, "base-success", new GuardOwner(), false, false);
            Record(observations, "base-first-null", new GuardOwner(), true, false);
            Record(observations, "base-second-null", new GuardOwner(), false, true);
            Record(observations, "alias-one-success", new FirstAliasOwner(), false, false);
            Record(observations, "alias-one-first-null", new FirstAliasOwner(), true, false);
            Record(observations, "alias-two-success", new SecondAliasOwner(), false, false);
            Record(observations, "alias-two-second-null", new SecondAliasOwner(), false, true);
            Record(observations, "null-owner", null, false, false);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "dual-result-tail-guard" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind,
            GuardOwner owner, bool firstNull, bool secondNull)
        {
            var first = new FirstNode { LastValue = true };
            var second = new SecondNode();
            if (owner != null)
            {
                owner.First = firstNull ? null : first;
                owner.Second = secondNull ? null : second;
            }
            var exception = "none";
            try { owner.Forward(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "exception", exception },
                { "ownerIsNull", owner == null },
                { "firstIsNull", owner == null ? null : (object)(owner.First == null) },
                { "secondIsNull", owner == null ? null : (object)(owner.Second == null) },
                { "firstSameWitness", owner == null ? null :
                    (object)ReferenceEquals(owner.First, first) },
                { "secondSameWitness", owner == null ? null :
                    (object)ReferenceEquals(owner.Second, second) },
                { "firstGetterCount", owner == null ? null :
                    (object)owner.FirstGetterCount },
                { "secondGetterCount", owner == null ? null :
                    (object)owner.SecondGetterCount },
                { "stage", owner == null ? null : (object)owner.Stage },
                { "firstApplyCount", first.ApplyCount },
                { "lastValueAfter", first.LastValue },
                { "secondFinishCount", second.FinishCount }
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
