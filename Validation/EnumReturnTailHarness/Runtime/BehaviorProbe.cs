using System;
using System.Collections.Generic;
using System.IO;
using EnumReturnTailFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var freshOwner = new ChoiceOwner();
            var freshReader = new ChoiceReader();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "fresh" },
                { "receiverIsNull", freshOwner.Receiver == null },
                { "readerValue", (int)freshReader.Current },
                { "readerNeighbor", freshReader.Neighbor },
                { "firstPad", freshOwner.Pad00 }, { "lastPad", freshOwner.Pad17 }
            });

            var reader = new ChoiceReader { Current = Choice.Negative, Neighbor = 37 };
            var owner = new ChoiceOwner
            {
                Pad00 = -11, Receiver = reader, Pad17 = 13
            };
            Record(observations, "negative", owner, reader, null);
            reader.Current = Choice.Zero;
            Record(observations, "zero", owner, reader, null);
            reader.Current = Choice.One;
            Record(observations, "one", owner, reader, null);
            reader.Current = Choice.Maximum;
            Record(observations, "maximum", owner, reader, null);
            reader.Current = unchecked((Choice)int.MinValue);
            Record(observations, "minimum", owner, reader, null);

            var shared = new ChoiceReader { Current = Choice.Negative, Neighbor = 43 };
            var left = new ChoiceOwner { Pad00 = -17, Receiver = shared, Pad17 = 19 };
            var right = new ChoiceOwner { Pad00 = -23, Receiver = shared, Pad17 = 29 };
            Record(observations, "alias-left", left, shared, right);
            shared.Current = Choice.Maximum;
            Record(observations, "alias-right", right, shared, left);

            var nullFieldWitness = new ChoiceReader { Current = Choice.One, Neighbor = 47 };
            var missingReceiver = new ChoiceOwner { Pad00 = -31, Receiver = null, Pad17 = 31 };
            Record(observations, "null-receiver", missingReceiver, nullFieldWitness, null);

            var nullOwnerWitness = new ChoiceReader { Current = Choice.Negative, Neighbor = 53 };
            var unaffected = new ChoiceOwner
            {
                Pad00 = -37, Receiver = nullOwnerWitness, Pad17 = 37
            };
            Record(observations, "null-owner", null, nullOwnerWitness, unaffected);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "enum-return-tail" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind,
            ChoiceOwner owner, ChoiceReader witness, ChoiceOwner alias)
        {
            var originalReceiver = owner == null ? null : owner.Receiver;
            var before = (int)witness.Current;
            var result = 0;
            var exception = "none";
            try { result = (int)owner.CurrentChoice; }
            catch (Exception error) { exception = error.GetType().FullName; }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "exception", exception },
                { "ownerIsNull", owner == null },
                { "receiverIsNull", owner == null ? null : (object)(owner.Receiver == null) },
                { "result", exception == "none" ? (object)result : null },
                { "valueBefore", before }, { "valueAfter", (int)witness.Current },
                { "neighborAfter", witness.Neighbor },
                { "receiverSameBefore", owner == null ? null :
                    (object)ReferenceEquals(owner.Receiver, originalReceiver) },
                { "receiverSameWitness", owner == null ? null :
                    (object)ReferenceEquals(owner.Receiver, witness) },
                { "firstPadAfter", owner == null ? null : (object)owner.Pad00 },
                { "lastPadAfter", owner == null ? null : (object)owner.Pad17 },
                { "aliasSameWitness", alias == null ? null :
                    (object)ReferenceEquals(alias.Receiver, witness) },
                { "aliasValueAfter", alias == null ? null : (object)(int)alias.Receiver.Current },
                { "aliasFirstPadAfter", alias == null ? null : (object)alias.Pad00 },
                { "aliasLastPadAfter", alias == null ? null : (object)alias.Pad17 }
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
