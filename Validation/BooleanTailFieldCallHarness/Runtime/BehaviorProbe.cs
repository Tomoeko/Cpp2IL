using System;
using System.Collections.Generic;
using System.IO;
using BooleanTailFieldCallFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var freshOwner = new BoolOwner();
            var freshReceiver = new BoolReceiver();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructors" },
                { "ownerCreated", freshOwner != null },
                { "receiverCreated", freshReceiver != null },
                { "receiverIsNull", freshOwner.Receiver == null },
                { "enabled", freshReceiver.Enabled },
                { "neighbor", freshReceiver.Neighbor },
                { "prefix", freshOwner.Prefix },
                { "suffix", freshOwner.Suffix }
            });

            var receiver = new BoolReceiver { Enabled = false, Neighbor = 23 };
            var owner = new BoolOwner { Prefix = -7, Receiver = receiver, Suffix = 11 };
            Record(observations, "false", owner, receiver);
            receiver.Enabled = true;
            Record(observations, "true", owner, receiver);

            var alias = new BoolOwner { Prefix = -13, Receiver = receiver, Suffix = 17 };
            Record(observations, "shared", alias, receiver);
            Record(observations, "shared-original", owner, receiver);

            var missing = new BoolOwner { Prefix = -19, Receiver = null, Suffix = 29 };
            Record(observations, "null-receiver", missing, receiver);
            Record(observations, "null-owner", null, receiver);

            var twin = new BoolReceiverTwin { Enabled = true, Neighbor = 31 };
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "folded-target-control" },
                { "result", twin.ReadEnabled() },
                { "enabled", twin.Enabled },
                { "neighbor", twin.Neighbor }
            });

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "boolean-tail-field-call" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind,
            BoolOwner owner, BoolReceiver witness)
        {
            bool? result = null;
            var exception = "none";
            try { result = owner.ForwardRead(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "result", result },
                { "exception", exception },
                { "ownerIsNull", owner == null },
                { "receiverIsNull", owner == null ? null : (object)(owner.Receiver == null) },
                { "receiverSameWitness", owner == null ? null :
                    (object)ReferenceEquals(owner.Receiver, witness) },
                { "enabledAfter", witness.Enabled },
                { "neighborAfter", witness.Neighbor },
                { "prefixAfter", owner == null ? null : (object)owner.Prefix },
                { "suffixAfter", owner == null ? null : (object)owner.Suffix }
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
