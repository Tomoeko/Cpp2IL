using System;
using System.Collections.Generic;
using System.IO;
using CallResultFalseTailFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public class HiddenTailNode : TailNode
    {
        public int HiddenCalls;

        public new void Apply(bool value)
        {
            HiddenCalls++;
            LastValue = true;
        }
    }

    public class OverrideTailOwner : VirtualTailOwner
    {
        public int OverrideCalls;

        public override void VirtualForwardFalse()
        {
            OverrideCalls++;
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
                { "lastValue", freshNode.LastValue },
                { "applyCount", freshNode.ApplyCount }
            });

            var witness = new TailNode { LastValue = true };
            Record(observations, "ordinary-success", new TailOwner { Node = witness }, witness, false);
            Record(observations, "ordinary-missing-node", new TailOwner(), witness, false);
            Record(observations, "ordinary-missing-owner", null, witness, false);
            Record(observations, "shared-first", new TailOwner { Node = witness }, witness, false);
            Record(observations, "shared-second", new TailOwner { Node = witness }, witness, false);

            var hidden = new HiddenTailNode { LastValue = true };
            Record(observations, "hidden-node", new TailOwner { Node = hidden }, hidden, false);

            var virtualNode = new TailNode { LastValue = true };
            Record(observations, "virtual-success",
                new VirtualTailOwner { Node = virtualNode }, virtualNode, true);
            Record(observations, "virtual-missing-node", new VirtualTailOwner(), virtualNode, true);

            var overrideNode = new TailNode { LastValue = true };
            Record(observations, "virtual-override",
                new OverrideTailOwner { Node = overrideNode }, overrideNode, true);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "call-result-false-tail" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind,
            TailOwner owner, TailNode witness, bool virtualCall)
        {
            var beforeValue = witness.LastValue;
            var beforeCount = witness.ApplyCount;
            var exception = "none";
            try
            {
                if (virtualCall)
                    ((VirtualTailOwner)owner).VirtualForwardFalse();
                else
                    owner.ForwardFalse();
            }
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
                { "lastValueBefore", beforeValue },
                { "lastValueAfter", witness.LastValue },
                { "applyCountBefore", beforeCount },
                { "applyCountAfter", witness.ApplyCount },
                { "hiddenCalls", witness is HiddenTailNode hidden ?
                    (object)hidden.HiddenCalls : null },
                { "overrideCalls", owner is OverrideTailOwner overridden ?
                    (object)overridden.OverrideCalls : null }
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
