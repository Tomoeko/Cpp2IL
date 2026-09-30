using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using InternalCallFieldFixture;
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
            var fresh = new TargetHolder();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructor" },
                { "created", fresh != null },
                { "targetIsNull", ReferenceEquals(fresh.Target, null) },
                { "labelIsNull", fresh.Label == null },
                { "neighbor", fresh.Neighbor }
            });

            var target = new GameObject("neutral-start");
            var holder = new TargetHolder
            {
                Target = target, Label = "neutral-label", Neighbor = 17
            };
            Record(observations, "read-active", false, holder, target);
            target.SetActive(false);
            Record(observations, "read-inactive", false, holder, target);

            holder.Label = "";
            Record(observations, "rename-empty", true, holder, target);
            Record(observations, "rename-empty-repeat", true, holder, target);
            holder.Label = "neutral-renamed";
            Record(observations, "rename-nonempty", true, holder, target);
            Record(observations, "rename-nonempty-repeat", true, holder, target);

            var shared = new TargetHolder
            {
                Target = target, Label = "neutral-alias", Neighbor = 23
            };
            Record(observations, "rename-shared-target", true, shared, target, holder);
            Record(observations, "read-shared-target", false, holder, target, shared);

            var missingTarget = new TargetHolder
            {
                Target = null, Label = "neutral-missing", Neighbor = 31
            };
            Record(observations, "read-null-target", false, missingTarget, target, holder);
            Record(observations, "rename-null-target", true, missingTarget, target, holder);
            missingTarget.Target = target;
            Record(observations, "read-reused-target", false, missingTarget, target, holder);
            Record(observations, "rename-reused-target", true, missingTarget, target, holder);

            Record(observations, "read-null-holder", false, null, target, holder);
            Record(observations, "rename-null-holder", true, null, target, holder);
            target.SetActive(true);
            Record(observations, "read-reused-holder", false, holder, target, shared);
            Record(observations, "rename-reused-holder", true, holder, target, shared);

            UnityEngine.Object.DestroyImmediate(target);
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion },
                { "platform", Application.platform.ToString() },
                { "stage", stage },
                { "profile", "internal-call-field" },
                { "observations", observations }
            }));
        }

        private static object DeclarationFacts()
        {
            var type = typeof(TargetHolder);
            var target = type.GetField("Target", DeclaredMembers);
            var label = type.GetField("Label", DeclaredMembers);
            var neighbor = type.GetField("Neighbor", DeclaredMembers);
            var read = type.GetMethod("ReadActive", DeclaredMembers);
            var rename = type.GetMethod("Rename", DeclaredMembers);
            var constructors = type.GetConstructors(DeclaredMembers);
            return new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "sealedClass", type.IsClass && type.IsPublic && type.IsSealed &&
                    !type.IsAbstract && type.BaseType == typeof(object) },
                { "threeFields", type.GetFields(DeclaredMembers).Length == 3 },
                { "twoMethods", type.GetMethods(DeclaredMembers).Length == 2 },
                { "oneConstructor", constructors.Length == 1 && constructors[0].IsPublic &&
                    !constructors[0].IsStatic && constructors[0].GetParameters().Length == 0 },
                { "targetField", target != null && target.IsPublic && !target.IsStatic &&
                    target.FieldType == typeof(GameObject) },
                { "labelField", label != null && label.IsPublic && !label.IsStatic &&
                    label.FieldType == typeof(string) },
                { "neighborField", neighbor != null && neighbor.IsPublic && !neighbor.IsStatic &&
                    neighbor.FieldType == typeof(int) },
                { "readSignature", HasSignature(read, typeof(bool)) },
                { "renameSignature", HasSignature(rename, typeof(void)) }
            };
        }

        private static bool HasSignature(MethodInfo method, Type returnType)
        {
            return method != null && method.IsPublic && !method.IsStatic &&
                !method.IsVirtual && !method.IsGenericMethod &&
                method.ReturnType == returnType && method.GetParameters().Length == 0;
        }

        private static void Record(List<object> observations, string kind, bool rename,
            TargetHolder holder, GameObject witness, TargetHolder alias = null)
        {
            var beforeTarget = holder == null ? null : holder.Target;
            var beforeName = witness.name;
            var beforeActive = witness.activeSelf;
            object result = null;
            var exception = "none";
            try
            {
                if (rename)
                    holder.Rename();
                else
                    result = holder.ReadActive();
            }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind },
                { "operation", rename ? "rename" : "read" },
                { "exception", exception },
                { "result", result },
                { "activeBefore", beforeActive },
                { "activeAfter", witness.activeSelf },
                { "nameBefore", beforeName },
                { "nameAfter", witness.name },
                { "holderIsNull", holder == null },
                { "targetIsNull", holder == null ? null : (object)ReferenceEquals(holder.Target, null) },
                { "targetSameBefore", holder == null ? null : (object)ReferenceEquals(holder.Target, beforeTarget) },
                { "targetSameWitness", holder == null ? null : (object)ReferenceEquals(holder.Target, witness) },
                { "labelAfter", holder == null ? null : holder.Label },
                { "neighborAfter", holder == null ? null : (object)holder.Neighbor },
                { "aliasSameWitness", alias == null ? null : (object)ReferenceEquals(alias.Target, witness) },
                { "aliasLabelAfter", alias == null ? null : alias.Label },
                { "aliasNeighborAfter", alias == null ? null : (object)alias.Neighbor }
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
