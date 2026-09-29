using System;
using System.Collections.Generic;
using System.IO;
using ExplicitClassCastFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private static Exception previousFailure;

        public static void Write(string path, string stage)
        {
            previousFailure = null;
            var exact = new Exception("exact");
            var subtype = new InvalidOperationException("subtype");
            var longName = new IncompatibleClassWithALongNameToExerciseMoreThanOneNativeStringBufferGrowthDuringCastFailure();
            var observations = new List<object>
            {
                Row("null", null, null),
                Row("exact", exact, exact),
                Row("subtype", subtype, subtype),
                Row("exact-repeat", exact, exact),
                Row("string", "other", null),
                Row("boxed-int", 17, null),
                Row("string-repeat", "other", null),
                Row("string-array", new string[0], null),
                Row("rectangular-array", new double[0, 0], null),
                Row("generic", new GenericSource<int>(), null),
                Row("nested", new Container.NestedSource(), null),
                Row("boxed-value", new NumericSource(), null),
                Row("long-name", longName, null),
                Row("long-name-repeat", longName, null)
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "explicit-class-cast" },
                { "observations", observations }
            }));
        }

        private static Dictionary<string, object> Row(string kind, object value, Exception expected)
        {
            Exception result = null;
            var failure = "none";
            var message = "none";
            var hresult = 0;
            var inner = "none";
            var freshFailure = true;
            try { result = CastMethods.Cast(value); }
            catch (Exception error)
            {
                failure = error.GetType().FullName;
                message = error.Message;
                hresult = error.HResult;
                inner = error.InnerException == null ? "none" : error.InnerException.GetType().FullName;
                freshFailure = !ReferenceEquals(previousFailure, error);
                previousFailure = error;
            }
            return new Dictionary<string, object>
            {
                { "kind", kind },
                { "sameReference", ReferenceEquals(result, expected) },
                { "resultType", result == null ? "null" : result.GetType().FullName },
                { "failure", failure },
                { "message", message },
                { "hresult", hresult },
                { "innerException", inner },
                { "freshFailure", freshFailure },
                { "userFormatCalls", value is IncompatibleClassWithALongNameToExerciseMoreThanOneNativeStringBufferGrowthDuringCastFailure
                    ? ((IncompatibleClassWithALongNameToExerciseMoreThanOneNativeStringBufferGrowthDuringCastFailure)value).FormatCalls : 0 }
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

    public sealed class GenericSource<T> { }
    public static class Container { public sealed class NestedSource { } }
    public struct NumericSource { public long Value; }

    public sealed class IncompatibleClassWithALongNameToExerciseMoreThanOneNativeStringBufferGrowthDuringCastFailure
    {
        public int FormatCalls;
        public override string ToString()
        {
            FormatCalls++;
            throw new InvalidOperationException("User formatting must not run during a managed class cast.");
        }
    }
}
