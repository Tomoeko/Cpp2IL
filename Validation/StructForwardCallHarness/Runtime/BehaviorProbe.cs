using System;
using System.Collections.Generic;
using System.IO;
using StructForwardCallFixture;
using UnityEngine;

namespace RecoveryValidation
{
    // The driver is separate from the five selected recovery methods.
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var freshOwner = new ForwardOwner();
            var freshTarget = new ForwardTarget();
            var freshToken = new IgnoredToken();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructors" },
                { "ownerCreated", freshOwner != null },
                { "targetCreated", freshTarget != null },
                { "tokenCreated", freshToken != null },
                { "freshReceiverIsNull", freshOwner.Receiver == null },
                { "freshPrefix", freshOwner.Prefix },
                { "freshSuffix", freshOwner.Suffix },
                { "freshNeighbor", freshTarget.Neighbor },
                { "freshLastFirst", Bits(freshTarget.Last.First) },
                { "freshLastSecond", Bits(freshTarget.Last.Second) },
                { "freshMarker", freshToken.Marker }
            });

            var target = new ForwardTarget { Neighbor = 37 };
            var owner = new ForwardOwner { Prefix = -11, Receiver = target, Suffix = 13 };
            var token = new IgnoredToken { Marker = 17 };
            Record(observations, "negative-zero", owner, target, null, token,
                Pair(0x80000000u, 0x00000000u));
            Record(observations, "positive-zero", owner, target, null, null,
                Pair(0x00000000u, 0x80000000u));
            Record(observations, "positive", owner, target, null, token,
                Pair(0x3f800000u, 0xbf800000u));
            Record(observations, "negative", owner, target, null, token,
                Pair(0xbf800000u, 0x3f800000u));
            Record(observations, "infinity", owner, target, null, token,
                Pair(0x7f800000u, 0x7f7fffffu));
            Record(observations, "nan", owner, target, null, token,
                Pair(0x7fc00001u, 0x3f800000u));
            Record(observations, "subnormal", owner, target, null, token,
                Pair(0x00000001u, 0x3f800000u));

            Record(observations, "overwrite", owner, target, null, token,
                Pair(0x3f800000u, 0x00000000u));

            var shared = new ForwardTarget { Neighbor = 43 };
            var left = new ForwardOwner { Prefix = -17, Receiver = shared, Suffix = 19 };
            var right = new ForwardOwner { Prefix = -23, Receiver = shared, Suffix = 29 };
            Record(observations, "shared-left", left, shared, right, token,
                Pair(0x3f800000u, 0xbf800000u));
            Record(observations, "shared-right", right, shared, left, token,
                Pair(0xbf800000u, 0x3f800000u));

            var nullReceiverWitness = new ForwardTarget
            {
                Last = Pair(0x40400000u, 0xc0400000u), Neighbor = 43
            };
            var missingReceiver = new ForwardOwner { Prefix = -31, Receiver = null, Suffix = 31 };
            Record(observations, "null-receiver", missingReceiver, nullReceiverWitness, null,
                token, Pair(0x3f800000u, 0x00000000u));

            var nullOwnerWitness = new ForwardTarget
            {
                Last = Pair(0x40800000u, 0xc0800000u), Neighbor = 53
            };
            var unaffectedOwner = new ForwardOwner
            {
                Prefix = -37, Receiver = nullOwnerWitness, Suffix = 37
            };
            Record(observations, "null-owner", null, nullOwnerWitness, unaffectedOwner,
                token, Pair(0x3f800000u, 0x00000000u));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "struct-forward-call" },
                { "observations", observations }
            }));
        }

        private static FloatPair Pair(uint first, uint second)
        {
            return new FloatPair
            {
                First = BitConverter.ToSingle(BitConverter.GetBytes(first), 0),
                Second = BitConverter.ToSingle(BitConverter.GetBytes(second), 0)
            };
        }

        private static string Bits(float value)
        {
            return BitConverter.ToUInt32(BitConverter.GetBytes(value), 0).ToString("x8");
        }

        private static void Record(List<object> observations, string kind, ForwardOwner owner,
            ForwardTarget witness, ForwardOwner alias, IgnoredToken ignored, FloatPair value)
        {
            var originalReceiver = owner == null ? null : owner.Receiver;
            var first = Bits(value.First);
            var second = Bits(value.Second);
            var lastFirstBefore = Bits(witness.Last.First);
            var lastSecondBefore = Bits(witness.Last.Second);
            var markerBefore = ignored == null ? null : (object)ignored.Marker;
            var exception = "none";
            object result = null;
            try { result = owner.Forward(value, ignored); }
            catch (Exception error) { exception = error.GetType().FullName; }
            var sourceFirstAfter = Bits(value.First);
            var sourceSecondAfter = Bits(value.Second);
            var lastFirstAfter = Bits(witness.Last.First);
            var lastSecondAfter = Bits(witness.Last.Second);
            value.First = BitConverter.ToSingle(BitConverter.GetBytes(0x41200000u), 0);
            value.Second = BitConverter.ToSingle(BitConverter.GetBytes(0xc1200000u), 0);

            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "first", first }, { "second", second },
                { "result", result }, { "exception", exception },
                { "sourceFirstAfter", sourceFirstAfter },
                { "sourceSecondAfter", sourceSecondAfter },
                { "lastFirstBefore", lastFirstBefore },
                { "lastSecondBefore", lastSecondBefore },
                { "lastFirstAfter", lastFirstAfter },
                { "lastSecondAfter", lastSecondAfter },
                { "lastFirstAfterSourceChange", Bits(witness.Last.First) },
                { "lastSecondAfterSourceChange", Bits(witness.Last.Second) },
                { "neighborAfter", witness.Neighbor },
                { "ownerIsNull", owner == null },
                { "receiverIsNull", owner == null ? null : (object)(owner.Receiver == null) },
                { "receiverSameBefore", owner == null ? null : (object)ReferenceEquals(owner.Receiver, originalReceiver) },
                { "receiverSameWitness", owner == null ? null : (object)ReferenceEquals(owner.Receiver, witness) },
                { "prefixAfter", owner == null ? null : (object)owner.Prefix },
                { "suffixAfter", owner == null ? null : (object)owner.Suffix },
                { "ignoredIsNull", ignored == null },
                { "markerBefore", markerBefore },
                { "markerAfter", ignored == null ? null : (object)ignored.Marker },
                { "aliasSameWitness", alias == null ? null : (object)ReferenceEquals(alias.Receiver, witness) },
                { "aliasLastFirstAfter", alias == null || alias.Receiver == null ? null : (object)Bits(alias.Receiver.Last.First) },
                { "aliasLastSecondAfter", alias == null || alias.Receiver == null ? null : (object)Bits(alias.Receiver.Last.Second) },
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
