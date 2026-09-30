using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using ScalarWordWrapperConversionFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static readonly int[] Boundaries =
        {
            0, 1, 2, 0x7e, 0x7f, 0x80, 0xff, 0x100, 0x101, 0x7ffe, 0x7fff,
            0x8000, 0x8001, 0x80ff, 0xff00, 0xff7f, 0xff80, 0xfffe, 0xffff, 0x1234, 0xabcd
        };

        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            var type = typeof(WordCell);
            const BindingFlags flags = BindingFlags.DeclaredOnly | BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;
            var signatures = new List<string>();
            foreach (var method in type.GetMethods(flags))
            {
                var parameters = method.GetParameters();
                signatures.Add(method.Name + ":" + parameters[0].ParameterType.FullName + "->" + method.ReturnType.FullName);
            }
            signatures.Sort(StringComparer.Ordinal);
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "methods", type.GetMethods(flags).Length + (type.TypeInitializer == null ? 0 : 1) },
                { "fields", type.GetFields(flags).Length }, { "valueType", type.IsValueType },
                { "sequentialLayout", type.IsLayoutSequential }, { "size", Marshal.SizeOf(type) },
                { "beforeFieldInit", (type.Attributes & TypeAttributes.BeforeFieldInit) != 0 },
                { "hasTypeInitializer", type.TypeInitializer != null },
                { "valueFieldType", type.GetField("Value").FieldType.FullName },
                { "seedFieldType", type.GetField("Seed").FieldType.FullName },
                { "seedIsStatic", type.GetField("Seed").IsStatic },
                { "seedIsReadOnly", type.GetField("Seed").IsInitOnly }, { "signatures", signatures }
            });
            var empty = default(WordCell);
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "default" }, { "value", (int)empty.Value }, { "unwrapped", (int)(short)empty },
                { "after", (int)empty.Value }
            });
            RecordSeed(observations, "seed-before");
            for (var start = 0; start < 65536; start += 256)
            {
                var wrapped = new StringBuilder(1024);
                var unwrapped = new StringBuilder(1024);
                var wrapInputsUnchanged = true;
                var unwrapInputsUnchanged = true;
                for (var bits = start; bits < start + 256; bits++)
                {
                    var input = unchecked((short)bits);
                    var original = input;
                    var produced = (WordCell)input;
                    wrapped.Append(unchecked((ushort)produced.Value).ToString("x4"));
                    wrapInputsUnchanged &= input == original;
                    var cell = new WordCell { Value = original };
                    var result = (short)cell;
                    unwrapped.Append(unchecked((ushort)result).ToString("x4"));
                    unwrapInputsUnchanged &= cell.Value == original;
                }
                RecordBlock(observations, "wrap", start, wrapped, wrapInputsUnchanged);
                RecordBlock(observations, "unwrap", start, unwrapped, unwrapInputsUnchanged);
            }
            foreach (var bits in Boundaries)
            {
                var input = unchecked((short)bits);
                var original = new WordCell { Value = input };
                var cells = new[] { new WordCell { Value = 31 }, WordCell.Seed };
                var alias = cells;
                var copy = cells[0];
                var produced = (WordCell)input;
                ref WordCell slot = ref alias[0];
                slot = produced;
                observations.Add(new Dictionary<string, object>
                {
                    { "kind", "boundary-alias" }, { "bits", bits }, { "input", (int)input },
                    { "constructedValue", (int)produced.Value }, { "unwrappedValue", (int)(short)original },
                    { "slotResult", (int)(short)slot }, { "slotValue", (int)cells[0].Value },
                    { "originalAfter", (int)original.Value }, { "copyValue", (int)copy.Value },
                    { "sameArray", ReferenceEquals(alias, cells) }, { "neighborValue", (int)cells[1].Value },
                    { "seedValue", (int)WordCell.Seed.Value }
                });
            }
            RecordSeed(observations, "seed-after");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "scalar-word-wrapper-conversion" }, { "observations", observations }
            }));
        }

        private static void RecordSeed(List<object> observations, string kind)
        {
            var seed = WordCell.Seed;
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "value", (int)seed.Value }, { "bits", (int)unchecked((ushort)seed.Value) },
                { "unwrapped", (int)(short)seed }, { "after", (int)WordCell.Seed.Value }
            });
        }

        private static void RecordBlock(List<object> observations, string operation, int start,
            StringBuilder results, bool inputsUnchanged)
        {
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "exhaustive" }, { "operation", operation }, { "startBits", start }, { "count", 256 },
                { "resultBits", results.ToString() }, { "inputsUnchanged", inputsUnchanged },
                { "seedValue", (int)WordCell.Seed.Value }
            });
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void RunPlayer()
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (arguments[index] != "--validation-report") continue;
                try { Write(arguments[index + 1], "player"); Application.Quit(0); }
                catch (Exception error) { Debug.LogException(error); Application.Quit(1); }
                return;
            }
        }
    }
}
