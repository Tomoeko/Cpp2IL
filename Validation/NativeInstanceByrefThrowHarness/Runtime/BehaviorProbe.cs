using System;
using System.Collections.Generic;
using System.IO;
using NativeInstanceByrefThrowFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var rows = new List<object>();
            var owner = new ThrowCases();
            Single(rows, owner, -23);
            Single(rows, owner, 0);
            Single(rows, owner, int.MinValue);
            Single(rows, owner, int.MaxValue);
            Single(rows, null, 17);
            Three(rows, owner, "nulls", null, null, null);
            Three(rows, owner, "distinct", new List<int> { 5 }, new List<int> { 7, 11 }, new List<int> { 13 });
            var shared = new List<int> { -3, 17 };
            Three(rows, owner, "aliases", shared, shared, shared);
            Three(rows, null, "null-owner", shared, null, shared);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-instance-byref-throw" }, { "observations", rows }
            }));
        }

        private static void Single(List<object> rows, ThrowCases owner, int value)
        {
            var initial = value;
            byte[] result = null;
            var exception = "none";
            try { result = owner.ThrowSingle(ref value); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", "single" }, { "ownerNull", owner == null }, { "initial", initial },
                { "value", value }, { "result", result }, { "exception", exception }
            });
        }

        private static void Three(List<object> rows, ThrowCases owner, string kind,
            List<int> first, List<int> second, List<int> third)
        {
            var a = first; var b = second; var c = third;
            int? result = null;
            var exception = "none";
            try { result = owner.ThrowThree(ref first, ref second, ref third); }
            catch (Exception error) { exception = error.GetType().FullName; }
            rows.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "result", result }, { "exception", exception },
                { "referencesUnchanged", ReferenceEquals(a, first) && ReferenceEquals(b, second) && ReferenceEquals(c, third) },
                { "first", first }, { "second", second }, { "third", third },
                { "firstSecondAlias", ReferenceEquals(first, second) }, { "firstThirdAlias", ReferenceEquals(first, third) }
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
