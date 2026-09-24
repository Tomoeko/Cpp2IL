using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using CallResultNullGuardFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private const BindingFlags DeclaredMembers = BindingFlags.DeclaredOnly |
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance |
            BindingFlags.Static;

        public static void Write(string path, string stage)
        {
            var observations = new List<object> { DeclarationFacts() };

            var first = new ChainNode { Value = -5 };
            var second = new ChainNode { Value = 17 };
            first.Next = second;
            var success = new ChainRoot { First = first };
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "helpers" },
                { "firstIdentity", ReferenceEquals(success.GetFirst(), first) },
                { "secondIdentity", ReferenceEquals(first.GetNext(), second) },
                { "readValue", second.Read() }
            });
            Record(observations, "success", success);
            Record(observations, "repeat-success", success);

            Record(observations, "first-null", new ChainRoot());
            Record(observations, "second-null", new ChainRoot
            {
                First = new ChainNode { Value = 23 }
            });

            var shared = new ChainNode { Value = -17 };
            shared.Next = shared;
            Record(observations, "self-alias", new ChainRoot { First = shared });

            var boundaryFirst = new ChainNode { Value = 1 };
            boundaryFirst.Next = new ChainNode { Value = int.MinValue };
            Record(observations, "trace-overflow", new ChainRoot
            {
                First = boundaryFirst,
                Trace = int.MaxValue
            });
            Record(observations, "root-null", null);

            RecordRepeated(observations, "success", NewRepeatedRoot(7, 11), false);
            RecordRepeated(observations, "first-null", NewRepeatedRoot(null, 11), false);
            RecordRepeated(observations, "second-null", NewRepeatedRoot(7, null), false);
            var sharedRepeated = new ChainNode { Value = -3 };
            RecordRepeated(observations, "self-alias", new ChainRoot
            {
                First = sharedRepeated,
                Second = sharedRepeated
            }, false);
            RecordRepeated(observations, "sum-overflow",
                NewRepeatedRoot(1, int.MaxValue), false);
            RecordRepeated(observations, "root-null", null, false);

            RecordRepeated(observations, "success", NewRepeatedRoot(7, 11), true);
            RecordRepeated(observations, "first-null", NewRepeatedRoot(null, 11), true);
            RecordRepeated(observations, "second-null", NewRepeatedRoot(7, null), true);
            var sharedInherited = new ChainNode { Value = -3 };
            RecordRepeated(observations, "self-alias", new ChainRoot
            {
                First = sharedInherited,
                Second = sharedInherited
            }, true);
            RecordRepeated(observations, "sum-overflow",
                NewRepeatedRoot(1, int.MaxValue), true);
            RecordRepeated(observations, "root-null", null, true);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "call-result-null-guards" },
                { "observations", observations }
            }));
        }

        private static object DeclarationFacts()
        {
            return new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "rootExact", HasRootDeclarations() },
                { "nodeExact", HasNodeDeclarations() },
                { "baseExact", HasBaseDeclarations() },
                { "methodCountExact", CountMethods(typeof(ChainRoot)) +
                    CountMethods(typeof(ChainNode)) +
                    CountMethods(typeof(InheritedFlagBase)) == 13 }
            };
        }

        private static bool HasRootDeclarations()
        {
            Type type = typeof(ChainRoot);
            return IsSealedPublicClass(type, typeof(object)) &&
                HasParameterlessConstructor(type) &&
                type.GetFields(DeclaredMembers).Length == 5 &&
                HasField(type, "First", typeof(ChainNode)) &&
                HasField(type, "Second", typeof(ChainNode)) &&
                HasField(type, "Trace", typeof(int)) &&
                HasField(type, "GetterCalls", typeof(int)) &&
                HasField(type, "Marker", typeof(byte)) &&
                type.GetMethods(DeclaredMembers).Length == 5 &&
                HasMethod(type, "GetFirst", typeof(ChainNode)) &&
                HasMethod(type, "Execute", typeof(int)) &&
                HasMethod(type, "GetRepeated", typeof(ChainNode)) &&
                HasMethod(type, "ExecuteRepeated", typeof(int)) &&
                HasMethod(type, "ExecuteInheritedRepeated", typeof(int));
        }

        private static bool HasNodeDeclarations()
        {
            Type type = typeof(ChainNode);
            return IsSealedPublicClass(type, typeof(InheritedFlagBase)) &&
                HasParameterlessConstructor(type) &&
                type.GetFields(DeclaredMembers).Length == 2 &&
                HasField(type, "Next", typeof(ChainNode)) &&
                HasField(type, "Value", typeof(int)) &&
                type.GetMethods(DeclaredMembers).Length == 3 &&
                HasMethod(type, "GetNext", typeof(ChainNode)) &&
                HasMethod(type, "Read", typeof(int)) &&
                HasMethod(type, "ReadWith", typeof(int), typeof(int));
        }

        private static bool HasBaseDeclarations()
        {
            Type type = typeof(InheritedFlagBase);
            PropertyInfo property = type.GetProperty("Enabled", DeclaredMembers);
            FieldInfo backing = type.GetField("<Enabled>k__BackingField", DeclaredMembers);
            return type.IsClass && type.IsPublic && !type.IsSealed &&
                type.BaseType == typeof(object) && HasParameterlessConstructor(type) &&
                type.GetFields(DeclaredMembers).Length == 1 &&
                backing != null && backing.IsPrivate && !backing.IsStatic &&
                !backing.IsInitOnly && backing.FieldType == typeof(bool) &&
                type.GetProperties(DeclaredMembers).Length == 1 &&
                property != null && property.PropertyType == typeof(bool) &&
                property.GetIndexParameters().Length == 0 &&
                type.GetMethods(DeclaredMembers).Length == 2 &&
                HasMethod(type, "get_Enabled", typeof(bool)) &&
                HasMethod(type, "set_Enabled", typeof(void), typeof(bool));
        }

        private static bool IsSealedPublicClass(Type type, Type baseType)
        {
            return type.IsClass && type.IsPublic && type.IsSealed &&
                type.BaseType == baseType;
        }

        private static bool HasParameterlessConstructor(Type type)
        {
            ConstructorInfo[] constructors = type.GetConstructors(DeclaredMembers);
            return constructors.Length == 1 && constructors[0].IsPublic &&
                !constructors[0].IsStatic &&
                constructors[0].GetParameters().Length == 0;
        }

        private static int CountMethods(Type type)
        {
            return type.GetMethods(DeclaredMembers).Length +
                type.GetConstructors(DeclaredMembers).Length;
        }

        private static bool HasField(Type type, string name, Type fieldType)
        {
            FieldInfo field = type.GetField(name, DeclaredMembers);
            return field != null && field.IsPublic && !field.IsStatic &&
                !field.IsInitOnly && field.FieldType == fieldType;
        }

        private static bool HasMethod(Type type, string name, Type returnType,
            params Type[] parameters)
        {
            MethodInfo method = type.GetMethod(name, DeclaredMembers, null,
                parameters, null);
            return method != null && method.IsPublic && !method.IsStatic &&
                !method.IsVirtual && !method.ContainsGenericParameters &&
                method.ReturnType == returnType;
        }

        private static ChainRoot NewRepeatedRoot(int? firstValue, int? secondValue)
        {
            return new ChainRoot
            {
                First = firstValue.HasValue ? new ChainNode { Value = firstValue.Value } : null,
                Second = secondValue.HasValue ? new ChainNode { Value = secondValue.Value } : null
            };
        }

        private static void Record(List<object> observations, string name, ChainRoot root)
        {
            ChainNode first = root == null ? null : root.First;
            ChainNode second = first == null ? null : first.Next;
            object initialTrace = root == null ? null : (object)root.Trace;
            object result = null;
            string exception = "none";
            try { result = root.Execute(); }
            catch (Exception error) { exception = error.GetType().FullName; }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", "execute:" + name },
                { "initialTrace", initialTrace },
                { "result", result },
                { "exception", exception },
                { "finalTrace", root == null ? null : (object)root.Trace },
                { "firstPreserved", root == null || ReferenceEquals(root.First, first) },
                { "nextPreserved", first == null || ReferenceEquals(first.Next, second) },
                { "firstValue", first == null ? null : (object)first.Value },
                { "secondValue", second == null ? null : (object)second.Value },
                { "sameReference", first != null && ReferenceEquals(first, second) }
            });
        }

        private static void RecordRepeated(List<object> observations, string name,
            ChainRoot root, bool inheritedSetter)
        {
            ChainNode first = root == null ? null : root.First;
            ChainNode second = root == null ? null : root.Second;
            object initialTrace = root == null ? null : (object)root.Trace;
            object initialCalls = root == null ? null : (object)root.GetterCalls;
            object initialMarker = root == null ? null : (object)(int)root.Marker;
            object result = null;
            string exception = "none";
            try
            {
                result = inheritedSetter ? root.ExecuteInheritedRepeated() :
                    root.ExecuteRepeated();
            }
            catch (Exception error) { exception = error.GetType().FullName; }

            observations.Add(new Dictionary<string, object>
            {
                { "kind", (inheritedSetter ? "inherited:" : "repeated:") + name },
                { "initialTrace", initialTrace },
                { "initialCalls", initialCalls },
                { "initialMarker", initialMarker },
                { "result", result },
                { "exception", exception },
                { "finalTrace", root == null ? null : (object)root.Trace },
                { "finalCalls", root == null ? null : (object)root.GetterCalls },
                { "finalMarker", root == null ? null : (object)(int)root.Marker },
                { "firstPreserved", root == null || ReferenceEquals(root.First, first) },
                { "secondPreserved", root == null || ReferenceEquals(root.Second, second) },
                { "firstValue", first == null ? null : (object)first.Value },
                { "secondValue", second == null ? null : (object)second.Value },
                { "firstEnabled", first == null ? null : (object)first.Enabled },
                { "secondEnabled", second == null ? null : (object)second.Enabled },
                { "sameReference", first != null && ReferenceEquals(first, second) }
            });
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
