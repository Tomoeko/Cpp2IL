using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using ReferenceTailCallFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var fresh = new BufferOwner();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "fresh" }, { "bufferIsNull", fresh.Buffer == null },
                { "prefix", fresh.Prefix }, { "suffix", fresh.Suffix }
            });

            var buffer = new StringBuilder("first");
            var owner = new BufferOwner { Prefix = -11, Buffer = buffer, Suffix = 13 };
            Record(observations, "first", owner, buffer, null);
            buffer.Append("again");
            Record(observations, "repeat", owner, buffer, null);

            var shared = new StringBuilder("shared");
            var left = new BufferOwner { Prefix = -17, Buffer = shared, Suffix = 19 };
            var right = new BufferOwner { Prefix = -23, Buffer = shared, Suffix = 29 };
            Record(observations, "alias-left", left, shared, right);
            shared.Append("second");
            Record(observations, "alias-right", right, shared, left);

            var missing = new BufferOwner { Prefix = -31, Buffer = null, Suffix = 31 };
            var nullFieldWitness = new StringBuilder("witness");
            Record(observations, "null-field", missing, nullFieldWitness, null);

            var nullOwnerWitness = new StringBuilder("untouched");
            var unaffected = new BufferOwner
            {
                Prefix = -37, Buffer = nullOwnerWitness, Suffix = 37
            };
            Record(observations, "null-owner", null, nullOwnerWitness, unaffected);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "reference-tail-call" },
                { "observations", observations }
            }));
        }

        private static void Record(List<object> observations, string kind, BufferOwner owner,
            StringBuilder witness, BufferOwner alias)
        {
            var originalBuffer = owner == null ? null : owner.Buffer;
            var lengthBefore = witness.Length;
            StringBuilder result = null;
            var exception = "none";
            try { result = owner.ClearBuffer(); }
            catch (Exception error) { exception = error.GetType().FullName; }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "exception", exception },
                { "ownerIsNull", owner == null },
                { "fieldIsNull", owner == null ? null : (object)(owner.Buffer == null) },
                { "returnedSameWitness", exception == "none" ?
                    (object)ReferenceEquals(result, witness) : null },
                { "returnedSameField", exception == "none" ?
                    (object)ReferenceEquals(result, owner.Buffer) : null },
                { "fieldSameBefore", owner == null ? null :
                    (object)ReferenceEquals(owner.Buffer, originalBuffer) },
                { "fieldSameWitness", owner == null ? null :
                    (object)ReferenceEquals(owner.Buffer, witness) },
                { "lengthBefore", lengthBefore }, { "lengthAfter", witness.Length },
                { "prefixAfter", owner == null ? null : (object)owner.Prefix },
                { "suffixAfter", owner == null ? null : (object)owner.Suffix },
                { "aliasSameWitness", alias == null ? null :
                    (object)ReferenceEquals(alias.Buffer, witness) },
                { "aliasLengthAfter", alias == null ? null : (object)alias.Buffer.Length },
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
