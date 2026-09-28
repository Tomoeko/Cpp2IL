using System;
using System.Collections.Generic;
using System.IO;
using OrderedCallTailGuardFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public sealed class HiddenNode : GuardNode
    {
        public int HiddenCalls;

        public new void Apply(bool value)
        {
            HiddenCalls++;
            Flag = !value;
        }
    }

    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var witness = new GuardNode();
            Observe(observations, "success", new GuardOwner { Node = witness }, witness);
            Observe(observations, "missing-node", new GuardOwner(), witness);
            Observe(observations, "missing-owner", null, witness);
            var hidden = new HiddenNode();
            Observe(observations, "hidden-method", new GuardOwner { Node = hidden }, hidden);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "ordered-call-tail-guard" },
                { "observations", observations }
            }));
        }

        private static void Observe(List<object> observations, string kind,
            GuardOwner owner, GuardNode node)
        {
            var beforeFlag = node.Flag;
            var failure = "none";
            try { owner.Forward(); }
            catch (Exception error) { failure = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "failure", failure },
                { "markCount", owner == null ? null : (object)owner.MarkCount },
                { "overrideCount", owner == null ? null : (object)owner.OverrideCount },
                { "producerCount", owner == null ? null : (object)owner.ProducerCount },
                { "nodeWasNull", owner == null ? null : (object)(owner.Node == null) },
                { "beforeFlag", beforeFlag },
                { "afterFlag", node.Flag },
                { "hiddenCalls", node is HiddenNode hidden ? (object)hidden.HiddenCalls : null }
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
