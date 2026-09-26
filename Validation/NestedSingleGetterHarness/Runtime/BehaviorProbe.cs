using System;
using System.Collections.Generic;
using System.IO;
using NestedSingleGetterFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            foreach (var sample in new[]
            {
                new { Kind = "positive", Bits = 0x3F800000 },
                new { Kind = "negative", Bits = unchecked((int)0xC0200000) },
                new { Kind = "positive-zero", Bits = 0 },
                new { Kind = "negative-zero", Bits = unchecked((int)0x80000000) },
                new { Kind = "positive-infinity", Bits = 0x7F800000 },
                new { Kind = "negative-infinity", Bits = unchecked((int)0xFF800000) },
                new { Kind = "nan-payload", Bits = 0x7FC01234 }
            })
            {
                var child = new CellContainer.Cell
                {
                    Before = -37, Level = FromBits(sample.Bits), After = 41
                };
                var owner = new FloatReader { Before = -53, Child = child, After = 59 };
                observations.Add(Observe(sample.Kind, owner));
            }
            observations.Add(Observe("child-null", new FloatReader
            {
                Before = -53, Child = null, After = 59
            }));
            observations.Add(Observe("owner-null", null));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "nested-single-getter" },
                { "observations", observations }
            }));
        }

        private static float FromBits(int bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);

        private static int Bits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);

        private static object Observe(string kind, FloatReader owner)
        {
            var child = owner == null ? null : owner.Child;
            object result = null;
            var exception = "none";
            try { result = Bits(owner.ReadLevel()); }
            catch (Exception error) { exception = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "resultBits", result },
                { "exception", exception },
                { "sameChild", owner == null ? null : (object)ReferenceEquals(child, owner.Child) },
                { "ownerBefore", owner == null ? null : (object)owner.Before },
                { "ownerAfter", owner == null ? null : (object)owner.After },
                { "childBefore", child == null ? null : (object)child.Before },
                { "childAfter", child == null ? null : (object)child.After },
                { "childLevelBits", child == null ? null : (object)Bits(child.Level) }
            };
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
