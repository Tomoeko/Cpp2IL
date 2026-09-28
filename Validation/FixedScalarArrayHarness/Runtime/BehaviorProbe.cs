using System;
using System.Collections.Generic;
using System.IO;
using FixedScalarArrayFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var observations = new List<object>();
            for (var length = 0; length <= 5; length++)
            {
                var values = NewValues(length);
                var owner = new ScalarCatalog(101) { Before = -53, Values = values, After = 59 };
                observations.Add(Observe("length-" + length, 1, owner));
                observations.Add(Observe("length-" + length, 3, owner));
            }

            var missingArray = new ScalarCatalog(101) { Before = -53, Values = null, After = 59 };
            observations.Add(Observe("array-null", 1, missingArray));
            observations.Add(Observe("array-null", 3, missingArray));
            observations.Add(Observe("owner-null", 1, null));
            observations.Add(Observe("owner-null", 3, null));

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "fixed-scalar-array" },
                { "observations", observations }
            }));
        }

        private static int[] NewValues(int length)
        {
            var values = new int[length];
            for (var index = 0; index < length; index++)
                values[index] = index == 1 ? int.MinValue : index == 3 ? int.MaxValue : index - 37;
            return values;
        }

        private static object Observe(string kind, int index, ScalarCatalog owner)
        {
            var values = owner == null ? null : owner.Values;
            int? result = null;
            var exception = "none";
            try { result = index == 1 ? owner.ReadSecond() : owner.ReadFourth(); }
            catch (Exception error) { exception = error.GetType().FullName; }

            return new Dictionary<string, object>
            {
                { "kind", kind }, { "index", index }, { "result", result },
                { "exception", exception },
                { "sameArray", owner == null ? null : (object)ReferenceEquals(values, owner.Values) },
                { "ownerBefore", owner == null ? null : (object)owner.Before },
                { "ownerAfter", owner == null ? null : (object)owner.After },
                { "state", owner == null ? null : (object)owner.State },
                { "valuesUnchanged", values == null ? null : (object)Equal(values, NewValues(values.Length)) }
            };
        }

        private static bool Equal(int[] left, int[] right)
        {
            if (left.Length != right.Length)
                return false;
            for (var index = 0; index < left.Length; index++)
                if (left[index] != right[index])
                    return false;
            return true;
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
