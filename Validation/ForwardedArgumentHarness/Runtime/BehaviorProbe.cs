using System;
using System.Collections.Generic;
using System.IO;
using ForwardedArgumentFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // The driver is separate from the four selected recovery methods.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var freshOwner = new ForwardOwner();
            var freshTarget = new ForwardTarget();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructors" },
                { "ownerCreated", freshOwner != null },
                { "targetCreated", freshTarget != null },
                { "freshReceiverIsNull", freshOwner.Receiver == null },
                { "freshPrefix", freshOwner.Prefix },
                { "freshSuffix", freshOwner.Suffix },
                { "freshCalls", freshTarget.Calls },
                { "freshNeighbor", freshTarget.Neighbor }
            });

            var target = new ForwardTarget { Calls = 0, Neighbor = 37 };
            var owner = new ForwardOwner { Prefix = -11, Receiver = target, Suffix = 13 };
            Record(observations, "positive", owner, target, null, 7, int.MinValue);
            Record(observations, "zero", owner, target, null, 0, int.MaxValue);
            Record(observations, "negative", owner, target, null, -1, 7);
            Record(observations, "maximum", owner, target, null, int.MaxValue, int.MinValue);
            Record(observations, "minimum", owner, target, null, int.MinValue, int.MaxValue);

            target.Calls = int.MaxValue;
            Record(observations, "overflow", owner, target, null, 1, 0);

            var shared = new ForwardTarget { Calls = -2, Neighbor = 43 };
            var left = new ForwardOwner { Prefix = -17, Receiver = shared, Suffix = 19 };
            var right = new ForwardOwner { Prefix = -23, Receiver = shared, Suffix = 29 };
            Record(observations, "shared-left", left, shared, right, 1, -999);
            Record(observations, "shared-right", right, shared, left, 0, 999);
            Record(observations, "shared-left-again", left, shared, right, -1, int.MinValue);

            var nullReceiverWitness = new ForwardTarget { Calls = 41, Neighbor = 43 };
            var missingReceiver = new ForwardOwner { Prefix = -31, Receiver = null, Suffix = 31 };
            Record(observations, "null-receiver", missingReceiver, nullReceiverWitness, null, 1, 2);

            var nullOwnerWitness = new ForwardTarget { Calls = 47, Neighbor = 53 };
            var unaffectedOwner = new ForwardOwner
            {
                Prefix = -37, Receiver = nullOwnerWitness, Suffix = 37
            };
            Record(observations, "null-owner", null, nullOwnerWitness, unaffectedOwner, 1, 2);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "forwarded-argument" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, ForwardOwner owner,
            ForwardTarget witness, ForwardOwner alias, int value, int ignored)
        {
            var originalReceiver = owner == null ? null : owner.Receiver;
            var countBefore = witness.Calls;
            var exception = "none";
            object result = null;
            try { result = owner.Forward(value, ignored); }
            catch (Exception error) { exception = error.GetType().FullName; }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "value", value }, { "ignored", ignored },
                { "result", result }, { "exception", exception },
                { "ownerIsNull", owner == null },
                { "receiverIsNull", owner == null ? null : (object)(owner.Receiver == null) },
                { "receiverSameBefore", owner == null ? null : (object)ReferenceEquals(owner.Receiver, originalReceiver) },
                { "receiverSameWitness", owner == null ? null : (object)ReferenceEquals(owner.Receiver, witness) },
                { "countBefore", countBefore }, { "countAfter", witness.Calls },
                { "neighborAfter", witness.Neighbor },
                { "prefixAfter", owner == null ? null : (object)owner.Prefix },
                { "suffixAfter", owner == null ? null : (object)owner.Suffix },
                { "aliasSameWitness", alias == null ? null : (object)ReferenceEquals(alias.Receiver, witness) },
                { "aliasCountAfter", alias == null || alias.Receiver == null ? null : (object)alias.Receiver.Calls },
                { "aliasPrefixAfter", alias == null ? null : (object)alias.Prefix },
                { "aliasSuffixAfter", alias == null ? null : (object)alias.Suffix }
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
