using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BaseEffectBooleanTailFixture;
using UnityEngine;

namespace RecoveryValidation
{
    public static class BehaviorProbe
    {
        private const BindingFlags Declared = BindingFlags.DeclaredOnly | BindingFlags.Public |
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        public static void Write(string path, string stage)
        {
            var observations = new List<object> { Declarations() };
            var target = new SequenceTarget { Neighbor = 23 };
            var alternate = new SequenceTarget { Flag = true, Neighbor = 41 };
            var folded = new FoldedTarget { Neighbor = 47 };
            SequenceBase enable = new EnableSequence { Target = target, Neighbor = 17 };
            SequenceBase disable = new DisableSequence { Target = target, Neighbor = 31 };
            var plain = new SequenceBase();
            observations.Add(new Dictionary<string, object>
            {
                { "kind", "constructors" }, { "plainTargetNull", plain.Target == null },
                { "plainEffect", plain.EffectCount }, { "plainProducer", plain.ProducerCount },
                { "plainOrder", plain.Order }, { "plainNeighbor", plain.Neighbor },
                { "enableDefault", Defaults(new EnableSequence()) },
                { "disableDefault", Defaults(new DisableSequence()) },
                { "targetDefault", !new SequenceTarget().Flag && new SequenceTarget().Neighbor == 0 },
                { "foldedDefault", !new FoldedTarget().Flag && new FoldedTarget().Neighbor == 0 }
            });
            Record(observations, "base-only", plain, target, alternate, folded);
            folded.Enabled = true;
            RecordFolded(observations, "folded-true", folded, target, alternate);
            folded.Enabled = false;
            RecordFolded(observations, "folded-false", folded, target, alternate);
            Record(observations, "enable", enable, target, alternate, folded, disable);
            Record(observations, "enable-repeat", enable, target, alternate, folded, disable);
            Record(observations, "disable-alias", disable, target, alternate, folded, enable);
            Record(observations, "disable-repeat", disable, target, alternate, folded, enable);

            SequenceBase missingEnable = new EnableSequence { Neighbor = 53 };
            Record(observations, "enable-null-result", missingEnable, target, alternate, folded, enable);
            Record(observations, "enable-null-repeat", missingEnable, target, alternate, folded, enable);
            missingEnable.Target = target;
            Record(observations, "enable-reuse", missingEnable, target, alternate, folded, enable);
            SequenceBase missingDisable = new DisableSequence { Neighbor = 59 };
            Record(observations, "disable-null-result", missingDisable, target, alternate, folded, disable);
            missingDisable.Target = target;
            Record(observations, "disable-reuse", missingDisable, target, alternate, folded, disable);
            Record(observations, "null-holder", null, target, alternate, folded, enable);
            Record(observations, "enable-after-null", enable, target, alternate, folded, disable);
            enable.Target = alternate;
            Record(observations, "enable-new-target", enable, target, alternate, folded, disable);
            disable.Target = alternate;
            Record(observations, "disable-new-alias", disable, target, alternate, folded, enable);

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            File.WriteAllText(path, ReportJson.Encode(new Dictionary<string, object>
            {
                { "unityVersion", Application.unityVersion }, { "platform", Application.platform.ToString() },
                { "stage", stage }, { "profile", "base-effect-boolean-tail" },
                { "observations", observations }
            }));
        }

        private static bool Defaults(SequenceBase value)
        {
            return value.Target == null && value.EffectCount == 0 && value.ProducerCount == 0 &&
                value.Order == 0 && value.Neighbor == 0;
        }

        private static object Declarations()
        {
            var root = typeof(SequenceBase);
            var apply = root.GetMethod("Apply", Declared);
            var producer = root.GetMethod("GetTarget", Declared);
            var enabled = typeof(SequenceTarget).GetProperty("Enabled", Declared);
            var enable = typeof(EnableSequence).GetMethod("Apply", Declared);
            var disable = typeof(DisableSequence).GetMethod("Apply", Declared);
            return new Dictionary<string, object>
            {
                { "kind", "declarations" },
                { "methodCount", root.GetMethods(Declared).Length + root.GetConstructors(Declared).Length +
                    typeof(EnableSequence).GetMethods(Declared).Length + typeof(EnableSequence).GetConstructors(Declared).Length +
                    typeof(DisableSequence).GetMethods(Declared).Length + typeof(DisableSequence).GetConstructors(Declared).Length +
                    typeof(SequenceTarget).GetMethods(Declared).Length + typeof(SequenceTarget).GetConstructors(Declared).Length +
                    typeof(FoldedTarget).GetMethods(Declared).Length + typeof(FoldedTarget).GetConstructors(Declared).Length },
                { "baseVirtual", apply != null && apply.IsPublic && apply.IsVirtual && !apply.IsFinal &&
                    apply.GetBaseDefinition() == apply && apply.ReturnType == typeof(void) && apply.GetParameters().Length == 0 },
                { "producerProtected", producer != null && producer.IsFamily && !producer.IsStatic && !producer.IsVirtual &&
                    producer.ReturnType == typeof(SequenceTarget) && producer.GetParameters().Length == 0 },
                { "enableOverride", Override(enable, apply) && typeof(EnableSequence).BaseType == root },
                { "disableOverride", Override(disable, apply) && typeof(DisableSequence).BaseType == root },
                { "targetProperty", enabled != null && enabled.PropertyType == typeof(bool) &&
                    enabled.GetMethod != null && enabled.SetMethod != null && enabled.GetMethod.IsPublic &&
                    enabled.SetMethod.IsPublic && !enabled.GetMethod.IsVirtual && !enabled.SetMethod.IsVirtual },
                { "fieldCounts", root.GetFields(Declared).Length == 5 && typeof(EnableSequence).GetFields(Declared).Length == 0 &&
                    typeof(DisableSequence).GetFields(Declared).Length == 0 && typeof(SequenceTarget).GetFields(Declared).Length == 2 &&
                    typeof(FoldedTarget).GetFields(Declared).Length == 2 },
                { "targetClass", typeof(SequenceTarget).IsPublic && typeof(SequenceTarget).IsSealed &&
                    typeof(SequenceTarget).BaseType == typeof(object) },
                { "foldedDistinct", typeof(FoldedTarget) != typeof(SequenceTarget) }
            };
        }

        private static bool Override(MethodInfo method, MethodInfo root)
        {
            return method != null && method.IsPublic && method.IsVirtual && !method.IsFinal &&
                method.GetBaseDefinition() == root && method.ReturnType == typeof(void) && method.GetParameters().Length == 0;
        }

        private static void RecordFolded(List<object> observations, string kind, FoldedTarget folded,
            SequenceTarget witness, SequenceTarget alternate)
        {
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "foldedFlag", folded.Enabled }, { "foldedNeighbor", folded.Neighbor },
                { "witnessFlag", witness.Enabled }, { "alternateFlag", alternate.Enabled }
            });
        }

        private static void Record(List<object> observations, string kind, SequenceBase holder,
            SequenceTarget witness, SequenceTarget alternate, FoldedTarget folded, SequenceBase alias = null)
        {
            var before = holder == null ? null : holder.Target;
            var exception = "none";
            try { holder.Apply(); }
            catch (Exception error) { exception = error.GetType().FullName; }
            observations.Add(new Dictionary<string, object>
            {
                { "kind", kind }, { "exception", exception }, { "holderNull", holder == null },
                { "effect", holder == null ? null : (object)holder.EffectCount },
                { "producer", holder == null ? null : (object)holder.ProducerCount },
                { "order", holder == null ? null : (object)holder.Order },
                { "neighbor", holder == null ? null : (object)holder.Neighbor },
                { "targetNull", holder == null ? null : (object)(holder.Target == null) },
                { "targetSameBefore", holder == null ? null : (object)ReferenceEquals(holder.Target, before) },
                { "targetSameWitness", holder == null ? null : (object)ReferenceEquals(holder.Target, witness) },
                { "targetSameAlternate", holder == null ? null : (object)ReferenceEquals(holder.Target, alternate) },
                { "witnessFlag", witness.Enabled }, { "witnessNeighbor", witness.Neighbor },
                { "alternateFlag", alternate.Enabled }, { "alternateNeighbor", alternate.Neighbor },
                { "foldedFlag", folded.Enabled }, { "foldedNeighbor", folded.Neighbor },
                { "aliasPresent", alias != null },
                { "aliasSameTarget", alias == null || holder == null ? null : (object)ReferenceEquals(alias.Target, holder.Target) },
                { "aliasEffect", alias == null ? null : (object)alias.EffectCount },
                { "aliasProducer", alias == null ? null : (object)alias.ProducerCount },
                { "aliasOrder", alias == null ? null : (object)alias.Order },
                { "aliasNeighbor", alias == null ? null : (object)alias.Neighbor }
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
                try { Write(arguments[index + 1], "player"); Application.Quit(0); }
                catch (Exception error) { Debug.LogException(error); Application.Quit(1); }
                return;
            }
        }
    }
}
