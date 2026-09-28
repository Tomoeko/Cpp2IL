using System;
using System.Collections.Generic;
using System.IO;
using CallResultTailGuardFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public class DerivedTailNode : TailNode
    {
        public int ShadowCalls;

        public new void Apply(int value)
        {
            ShadowCalls++;
            LastValue = value + 100;
        }
    }

    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var freshOwner = new TailOwner();
            var freshNode = new TailNode();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructors" },
                { "ownerCreated", freshOwner != null },
                { "nodeCreated", freshNode != null },
                { "nodeIsNull", freshOwner.Node == null },
                { "producerCount", freshOwner.ProducerCount },
                { "lastValue", freshNode.LastValue }
            });

            var witness = new TailNode { LastValue = -11 };
            Record(observations, "success", new TailOwner { Node = witness }, witness);
            Record(observations, "missing-node", new TailOwner(), witness);
            Record(observations, "missing-owner", null, witness);

            var first = new TailOwner { Node = witness };
            var second = new TailOwner { Node = witness };
            Record(observations, "shared-first", first, witness);
            Record(observations, "shared-second", second, witness);

            var derived = new DerivedTailNode { LastValue = -19 };
            Record(observations, "derived-node", new TailOwner { Node = derived }, derived);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "call-result-tail-guard" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind,
            TailOwner owner, TailNode witness)
        {
            var previousValue = witness.LastValue;
            var exception = "none";
            try { owner.Forward(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "exception", exception },
                { "ownerIsNull", owner == null },
                { "nodeIsNull", owner == null ? null : (object)(owner.Node == null) },
                { "nodeSameWitness", owner == null ? null :
                    (object)ReferenceEquals(owner.Node, witness) },
                { "producerCount", owner == null ? null : (object)owner.ProducerCount },
                { "lastValueBefore", previousValue },
                { "lastValueAfter", witness.LastValue },
                { "shadowCalls", witness is DerivedTailNode derived ?
                    (object)derived.ShadowCalls : null }
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
