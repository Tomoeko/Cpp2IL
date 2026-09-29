using System;
using System.Collections.Generic;
using System.IO;
using BooleanParameterClassTestFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static int _formatCalls;

        public static void Write(string path, string stage)
        {
            _formatCalls = 0;
            var direct = new RemoteValue();
            var subtype = new RemoteChild();
            var other = new object();
            var observations = new List<object>
            {
                Row("null-cold", null),
                Row("object", other),
                Row("string", "other"),
                Row("boxed-int", 17),
                Row("object-vector", new object[] { direct }),
                Row("multidimensional", new object[1, 1]),
                Row("string-vector", new string[] { "other" }),
                Row("direct", direct),
                Row("subtype", subtype),
                Row("direct-repeat", direct),
                Row("object-repeat", other)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "boolean-parameter-class-test" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> Row(string kind, object value)
        {
            var result = false;
            var failure = "none";
            var hresult = 0;
            try { result = ClassTests.IsRemote(value); }
            catch (Exception error)
            {
                failure = error.GetType().FullName;
                hresult = error.HResult;
            }
            return new Dictionary<string, object>
            {
                { "kind", kind },
                { "result", result },
                { "failure", failure },
                { "hresult", hresult },
                { "formatCalls", _formatCalls }
            };
        }

        private class RemoteValue : MarshalByRefObject
        {
            public override string ToString()
            {
                _formatCalls++;
                throw new InvalidOperationException("A class test must not format its input.");
            }
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
