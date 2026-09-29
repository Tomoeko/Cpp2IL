using System;
using System.Collections.Generic;
using System.IO;
using ParameterClassTestFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var direct = new RemoteValue { Label = "direct", Marker = 17 };
            var subtype = new RemoteChild { Label = "subtype", Marker = 23 };
            var otherDirect = new RemoteValue { Label = "other direct", Marker = 31 };
            var observations = new List<object>
            {
                Row("null", null, null),
                Row("direct", direct, direct),
                Row("subtype", subtype, subtype),
                Row("direct-repeat", direct, direct),
                Row("string", "other", null),
                Row("boxed-int", 17, null),
                Row("array", new object[] { direct }, null),
                Row("object", new object(), null),
                Row("other-direct", otherDirect, otherDirect)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "parameter-class-test" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> Row(string kind, object value, MarshalByRefObject expected)
        {
            MarshalByRefObject result = null;
            var failure = "none";
            try { result = ClassTests.AsRemote(value); }
            catch (Exception error) { failure = error.GetType().FullName; }
            var observed = result as RemoteValue;
            return new Dictionary<string, object>
            {
                { "kind", kind },
                { "sameReference", ReferenceEquals(result, expected) },
                { "resultType", result == null ? "null" : result.GetType().FullName },
                { "label", observed == null ? "null" : observed.Label },
                { "marker", observed == null ? 0 : observed.Marker },
                { "failure", failure }
            };
        }

        private class RemoteValue : MarshalByRefObject
        {
            public string Label;
            public int Marker;
        }

        private sealed class RemoteChild : RemoteValue
        {
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
