using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using SealedParameterClassTestFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static int _formatCalls;

        public static void Write(string path, string stage)
        {
            _formatCalls = 0;
            var builder = new StringBuilder("seed");
            var other = new object();
            var observations = new List<object>
            {
                Row("null-first", null, builder),
                Row("object", other, builder),
                Row("formatting-trap", new FormattingTrap(), builder),
                Row("string", "seed", builder),
                Row("boxed-int", 17, builder),
                Row("object-vector", new object[] { builder }, builder),
                Row("multidimensional", new object[1, 1], builder),
                Row("builder-vector", new StringBuilder[] { builder }, builder),
                Row("builder", builder, builder),
                Row("builder-repeat", builder, builder),
                Row("second-builder", new StringBuilder("other"), builder),
                Row("null-repeat", null, builder)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "sealed-parameter-class-test" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> Row(string kind, object value,
            StringBuilder original)
        {
            var testResult = false;
            StringBuilder result = null;
            var failure = "none";
            var hresult = 0;
            try
            {
                testResult = ClassTests.IsBuilder(value);
                result = ClassTests.AsBuilder(value);
            }
            catch (Exception error)
            {
                failure = error.GetType().FullName;
                hresult = error.HResult;
            }
            return new Dictionary<string, object>
            {
                { "kind", kind },
                { "testResult", testResult },
                { "sameReference", result != null && ReferenceEquals(result, value) },
                { "nullResult", result == null },
                { "builderText", original.ToString() },
                { "failure", failure },
                { "hresult", hresult },
                { "formatCalls", _formatCalls }
            };
        }

        private sealed class FormattingTrap
        {
            public override string ToString()
            {
                _formatCalls++;
                throw new InvalidOperationException("A class test must not format its input.");
            }
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
