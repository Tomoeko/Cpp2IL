using System;
using System.Collections.Generic;
using System.IO;
using StructStaticForwardCallFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var eventsBeforeDefault = InitializationWitness.Events;
            var value = Pair(0x80000000u, 0x00000001u);
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "default-value" },
                { "eventsBefore", eventsBeforeDefault },
                { "eventsAfter", InitializationWitness.Events },
                { "first", Bits(value.First) },
                { "second", Bits(value.Second) }
            });

            var token = new IgnoredToken { Marker = 17 };
            var nullOwnerWitness = new ForwardTarget
            {
                Last = Pair(0x3f800000u, 0xbf800000u), Neighbor = 41
            };
            observations.Add(Invoke("null-owner", null, nullOwnerWitness, token, value));

            var nullReceiverWitness = new ForwardTarget
            {
                Last = Pair(0x7f800000u, 0xff800000u), Neighbor = 43
            };
            var missingReceiver = new ForwardOwner { Prefix = -7, Suffix = 11 };
            observations.Add(Invoke("null-receiver", missingReceiver, nullReceiverWitness,
                token, value));

            var target = new ForwardTarget { Neighbor = 47 };
            var owner = new ForwardOwner { Prefix = -13, Receiver = target, Suffix = 19 };
            observations.Add(Invoke("forward-before-static-read", owner, target, token, value));

            var beforeStaticRead = InitializationWitness.Events;
            var marker = FloatPair.Marker;
            var bias = Bits(FloatPair.Bias);
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "explicit-static-read" },
                { "eventsBefore", beforeStaticRead },
                { "eventsAfter", InitializationWitness.Events },
                { "marker", marker },
                { "bias", bias }
            });

            observations.Add(Invoke("forward-after-static-read", owner, target, null,
                Pair(0x7fc00001u, 0x7f7fffffu)));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "struct-static-forward-call" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> Invoke(string kind, ForwardOwner owner,
            ForwardTarget witness, IgnoredToken ignored, FloatPair value)
        {
            var originalReceiver = owner == null ? null : owner.Receiver;
            var first = Bits(value.First);
            var second = Bits(value.Second);
            var lastFirstBefore = Bits(witness.Last.First);
            var lastSecondBefore = Bits(witness.Last.Second);
            var eventsBefore = InitializationWitness.Events;
            var markerBefore = ignored == null ? null : (object)ignored.Marker;
            object result = null;
            var exception = "none";
            try { result = owner.Forward(value, ignored); }
            catch (Exception error) { exception = error.GetType().FullName; }
            var sourceFirstAfter = Bits(value.First);
            var sourceSecondAfter = Bits(value.Second);
            var lastFirstAfter = Bits(witness.Last.First);
            var lastSecondAfter = Bits(witness.Last.Second);
            value.First = BitConverter.ToSingle(BitConverter.GetBytes(0x41200000u), 0);
            value.Second = BitConverter.ToSingle(BitConverter.GetBytes(0xc1200000u), 0);
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "first", first }, { "second", second },
                { "result", result }, { "exception", exception },
                { "eventsBefore", eventsBefore }, { "eventsAfter", InitializationWitness.Events },
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
                { "markerAfter", ignored == null ? null : (object)ignored.Marker }
            };
        }

        private static FloatPair Pair(uint first, uint second)
        {
            var result = default(FloatPair);
            result.First = BitConverter.ToSingle(BitConverter.GetBytes(first), 0);
            result.Second = BitConverter.ToSingle(BitConverter.GetBytes(second), 0);
            return result;
        }

        private static string Bits(float value) =>
            BitConverter.ToUInt32(BitConverter.GetBytes(value), 0).ToString("x8");

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
