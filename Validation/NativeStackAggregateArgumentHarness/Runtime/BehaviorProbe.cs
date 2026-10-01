using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using NativeStackAggregateArgumentFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        public static void Write(string path, string stage)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Instance |
                BindingFlags.Static | BindingFlags.DeclaredOnly;
            var type = typeof(Triple);
            var calls = typeof(Calls);
            var fields = new List<object>();
            foreach (var name in new[] { "First", "Second", "Third" })
                fields.Add(new Dictionary<string, object>
                {
                    { "name", name }, { "type", type.GetField(name).FieldType.FullName },
                    { "offset", Marshal.OffsetOf(type, name).ToInt32() }
                });
            var observations = new List<object>
            {
                new Dictionary<string, object>
                {
                    { "kind", "declarations" }, { "types", 2 },
                    { "methods", calls.GetMethods(flags).Length + type.GetMethods(flags).Length },
                    { "fields", fields }, { "size", Marshal.SizeOf(type) }, { "sequential", type.IsLayoutSequential },
                    { "sumArgument", calls.GetMethod("Sum").GetParameters()[0].ParameterType.FullName },
                    { "forwardArgument", calls.GetMethod("Forward").GetParameters()[0].ParameterType.FullName },
                    { "return", calls.GetMethod("Forward").ReturnType.FullName }
                }
            };
            var cases = new uint[,]
            {
                { 0u, 0u, 0u }, { 0x80000000u, 0x80000000u, 0x80000000u },
                { 0x80000000u, 0u, 0x80000000u }, { 0x3f800000u, 0x40000000u, 0x40400000u },
                { 0x4b800000u, 0x3f800000u, 0xcb800000u }, { 0x7f7fffffu, 0xff7fffffu, 1u },
                { 0x7f7fffffu, 0x7f7fffffu, 0xff7fffffu }, { 1u, 2u, 3u },
                { 0x80000001u, 0x80000002u, 0x80000003u }, { 0x7f800000u, 0x3f800000u, 0xc0000000u },
                { 0xff800000u, 0x3f800000u, 0xc0000000u }, { 0x7fc12345u, 0x3f800000u, 0x40000000u },
                { 0x3f800000u, 0x40000000u, 0x7fc23456u }
            };
            for (var index = 0; index < cases.GetLength(0); index++)
            {
                var value = new Triple { First = FromBits(cases[index, 0]), Second = FromBits(cases[index, 1]),
                    Third = FromBits(cases[index, 2]) };
                for (var operation = 0; operation < 2; operation++)
                {
                    var exception = "none";
                    var result = "none";
                    try { result = Bits(operation == 0 ? Calls.Sum(value) : Calls.Forward(value)); }
                    catch (Exception error) { exception = error.GetType().FullName; }
                    observations.Add(new Dictionary<string, object>
                    {
                        { "kind", "aggregate" }, { "case", index }, { "operation", operation },
                        { "first", Bits(value.First) }, { "second", Bits(value.Second) }, { "third", Bits(value.Third) },
                        { "result", result }, { "exception", exception }
                    });
                }
            }
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "native-stack-aggregate-argument" }, { "observations", observations }
            }));
        }

        private static float FromBits(uint value) { return BitConverter.ToSingle(BitConverter.GetBytes(value), 0); }
        private static string Bits(float value) { return BitConverter.ToUInt32(BitConverter.GetBytes(value), 0).ToString("x8"); }

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
