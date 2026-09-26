using System;
using System.Collections.Generic;
using System.IO;
using FloatForwardStoreFixture;
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
                observations.Add(Observe(sample.Kind, NewOwner(), FromBits(sample.Bits)));
            }
            var nullTarget = NewOwner();
            nullTarget.Target = null;
            observations.Add(Observe("target-null", nullTarget, FromBits(0x3F800000)));
            observations.Add(Observe("owner-null", null, FromBits(0x3F800000)));
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "float-forward-store" },
                { "observations", observations }
            }));
        }

        private static FloatOwner NewOwner()
        {
            var target = new FloatTarget { Before = -37, After = 41 };
            target.SetLevel(1.25f);
            return new FloatOwner { Before = -53, Target = target, After = 59 };
        }

        private static float FromBits(int bits) => BitConverter.ToSingle(BitConverter.GetBytes(bits), 0);

        private static int Bits(float value) => BitConverter.ToInt32(BitConverter.GetBytes(value), 0);

        private static object Observe(string kind, FloatOwner owner, float value)
        {
            var target = owner == null ? null : owner.Target;
            object before = target == null ? null : (object)Bits(target.ReadLevel());
            var exception = "none";
            try { owner.Forward(value); }
            catch (Exception error) { exception = error.GetType().FullName; }
            return new Dictionary<string, object>
            {
                { "kind", kind }, { "beforeBits", before },
                { "afterBits", target == null ? null : (object)Bits(target.ReadLevel()) },
                { "exception", exception },
                { "sameTarget", owner == null ? null : (object)ReferenceEquals(target, owner.Target) },
                { "ownerBefore", owner == null ? null : (object)owner.Before },
                { "ownerAfter", owner == null ? null : (object)owner.After },
                { "targetBefore", target == null ? null : (object)target.Before },
                { "targetAfter", target == null ? null : (object)target.After }
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
