using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using ScalarPositiveZeroLeafFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            var receiver = new PositiveZeroReceiver();
            var alias = receiver;
            var second = new PositiveZeroReceiver();
            var observations = new List<object>
            {
                Bits("static-single", PositiveZeroStatics.SingleZero()),
                Bits("static-double", PositiveZeroStatics.DoubleZero()),
                ProbeSingle(receiver, "receiver-single"),
                ProbeDouble(receiver, "receiver-double"),
                ProbeSingle(alias, "alias-single"),
                ProbeDouble(alias, "alias-double"),
                ProbeSingle(second, "second-single"),
                ProbeDouble(second, "second-double"),
                ProbeSingle(null, "null-single"),
                ProbeDouble(null, "null-double"),
                Bits("negative-single-control", BitConverter.ToSingle(
                    BitConverter.GetBytes(0x80000000U), 0)),
                Bits("negative-double-control", BitConverter.ToDouble(
                    BitConverter.GetBytes(0x8000000000000000UL), 0)),
                Declaration(typeof(PositiveZeroStatics), "SingleZero"),
                Declaration(typeof(PositiveZeroStatics), "DoubleZero"),
                Declaration(typeof(PositiveZeroReceiver), "SingleZero"),
                Declaration(typeof(PositiveZeroReceiver), "DoubleZero"),
                new Dictionary<string, object>
                {
                    { "kind", "receiver-constructors" },
                    { "count", typeof(PositiveZeroReceiver).GetConstructors(
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly).Length },
                },
                new Dictionary<string, object>
                {
                    { "kind", "receiver-alias" },
                    { "same", ReferenceEquals(receiver, alias) },
                    { "secondIsDistinct", !ReferenceEquals(receiver, second) },
                },
            };
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "scalar-positive-zero-leaf" },
                { "observations", observations },
            }));
        }

        private static Dictionary<string, object> Bits(string kind, float value)
        {
            return new Dictionary<string, object>
            {
                { "kind", kind },
                { "bits", BitConverter.ToUInt32(BitConverter.GetBytes(value), 0)
                    .ToString("x8", CultureInfo.InvariantCulture) },
            };
        }

        private static Dictionary<string, object> Bits(string kind, double value)
        {
            return new Dictionary<string, object>
            {
                { "kind", kind },
                { "bits", BitConverter.ToUInt64(BitConverter.GetBytes(value), 0)
                    .ToString("x16", CultureInfo.InvariantCulture) },
            };
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Dictionary<string, object> ProbeSingle(PositiveZeroReceiver receiver, string kind)
        {
            try { return Bits(kind, receiver.SingleZero()); }
            catch (Exception exception)
            {
                return new Dictionary<string, object>
                {
                    { "kind", kind }, { "exception", exception.GetType().FullName },
                };
            }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static Dictionary<string, object> ProbeDouble(PositiveZeroReceiver receiver, string kind)
        {
            try { return Bits(kind, receiver.DoubleZero()); }
            catch (Exception exception)
            {
                return new Dictionary<string, object>
                {
                    { "kind", kind }, { "exception", exception.GetType().FullName },
                };
            }
        }

        private static Dictionary<string, object> Declaration(Type owner, string name)
        {
            var method = owner.GetMethod(name, BindingFlags.Public | BindingFlags.Static |
                BindingFlags.Instance | BindingFlags.DeclaredOnly);
            return new Dictionary<string, object>
            {
                { "kind", "declaration" }, { "owner", owner.FullName }, { "name", name },
                { "returnType", method.ReturnType.FullName }, { "static", method.IsStatic },
                { "virtual", method.IsVirtual }, { "parameters", method.GetParameters().Length },
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
                catch (Exception exception)
                {
                    Debug.LogException(exception);
                    Application.Quit(1);
                }
                return;
            }
        }
    }
}
