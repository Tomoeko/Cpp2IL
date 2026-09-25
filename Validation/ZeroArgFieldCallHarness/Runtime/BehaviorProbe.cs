using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using ZeroArgFieldCallFixture;

namespace RecoveryValidation
{
    // This driver is separate from the four selected recovery methods.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var freshOwner = new CallOwner();
            var freshReceiver = new CallReceiver();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructors" },
                { "ownerCreated", freshOwner != null },
                { "receiverCreated", freshReceiver != null },
                { "freshReceiverIsNull", freshOwner.Receiver == null },
                { "freshPrefix", freshOwner.Prefix },
                { "freshSuffix", freshOwner.Suffix },
                { "freshCalls", freshReceiver.Calls },
                { "freshNeighbor", freshReceiver.Neighbor }
            });

            var receiver = new CallReceiver { Calls = 0, Neighbor = 37 };
            var owner = new CallOwner { Prefix = -11, Receiver = receiver, Suffix = 13 };
            Record(observations, "first", owner, receiver, null);
            Record(observations, "second", owner, receiver, null);
            Record(observations, "third", owner, receiver, null);

            receiver.Calls = int.MaxValue;
            Record(observations, "overflow", owner, receiver, null);

            var shared = new CallReceiver { Calls = -2, Neighbor = 43 };
            var left = new CallOwner { Prefix = -17, Receiver = shared, Suffix = 19 };
            var right = new CallOwner { Prefix = -23, Receiver = shared, Suffix = 29 };
            Record(observations, "shared-left", left, shared, right);
            Record(observations, "shared-right", right, shared, left);
            Record(observations, "shared-left-again", left, shared, right);

            var nullReceiverWitness = new CallReceiver { Calls = 41, Neighbor = 43 };
            var missingReceiver = new CallOwner { Prefix = -31, Receiver = null, Suffix = 31 };
            Record(observations, "null-receiver", missingReceiver, nullReceiverWitness, null);

            var nullOwnerWitness = new CallReceiver { Calls = 47, Neighbor = 53 };
            var unaffectedOwner = new CallOwner
            {
                Prefix = -37, Receiver = nullOwnerWitness, Suffix = 37
            };
            Record(observations, "null-owner", null, nullOwnerWitness, unaffectedOwner);

            var twin = new CallReceiverTwin { Calls = int.MinValue, Neighbor = 59 };
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "folded-target-control" },
                { "result", twin.ReadCalls() },
                { "callsAfter", twin.Calls },
                { "neighborAfter", twin.Neighbor }
            });
            RecordRead(observations, "read-first", owner, receiver);
            receiver.Calls = int.MinValue;
            RecordRead(observations, "read-minimum", owner, receiver);
            receiver.Calls = int.MaxValue;
            RecordRead(observations, "read-maximum", owner, receiver);
            RecordRead(observations, "read-null-receiver", missingReceiver, nullReceiverWitness);
            RecordRead(observations, "read-null-owner", null, nullOwnerWitness);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "zero-arg-field-call" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, CallOwner owner,
            CallReceiver witness, CallOwner alias)
        {
            var originalReceiver = owner == null ? null : owner.Receiver;
            var countBefore = witness.Calls;
            var exception = "none";
            try { owner.Forward(); }
            catch (Exception error) { exception = error.GetType().FullName; }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "exception", exception },
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

        private static void RecordRead(List<object> observations, string kind,
            CallOwner owner, CallReceiver witness)
        {
            var before = witness.Calls;
            var result = 0;
            var exception = "none";
            try { result = owner.ForwardRead(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "exception", exception },
                { "result", exception == "none" ? (object)result : null },
                { "callsBefore", before }, { "callsAfter", witness.Calls },
                { "neighborAfter", witness.Neighbor },
                { "prefixAfter", owner == null ? null : (object)owner.Prefix },
                { "suffixAfter", owner == null ? null : (object)owner.Suffix },
                { "receiverSameWitness", owner == null ? null :
                    (object)ReferenceEquals(owner.Receiver, witness) }
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
