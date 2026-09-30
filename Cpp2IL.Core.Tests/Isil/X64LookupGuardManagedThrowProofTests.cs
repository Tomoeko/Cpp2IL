using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AsmResolver.DotNet;
using AsmResolver.PE.DotNet.Cil;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.OutputFormats;
using Cpp2IL.Core.Utils;
using Iced.Intel;

namespace Cpp2IL.Core.Tests.Isil;

[NonParallelizable]
public class X64LookupGuardManagedThrowProofTests
{
    [TestCase("Read", false)]
    [TestCase("ReadProperty", true)]
    public void ExactCallerRequiresBothExitBodiesAndEveryManagedIdentity(string name, bool usesGetter)
    {
        var directory = Environment.GetEnvironmentVariable("CPP2IL_LOOKUP_GUARD_MANAGED_THROW_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_LOOKUP_GUARD_MANAGED_THROW_FIXTURE_INPUT to the neutral exact player input.");
        var binary = Path.Combine(directory!, "GameAssembly.dll");
        var metadata = Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data",
            "Metadata", "global-metadata.dat");
        Assert.That(File.Exists(binary) && File.Exists(metadata), Is.True);
        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(binary, metadata, UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            var fixtureMethods = app.GetAssemblyByName("LookupGuardManagedThrowFixture")!.Types
                .SelectMany(type => type.Methods).ToArray();
            Assert.That(fixtureMethods, Has.Length.EqualTo(3));
            var method = fixtureMethods.Single(candidate => candidate.Name == name);
            method.EnsureRawBytes();
            var native = X64NativeInstructionReader.ReadRootBody(method)!;
            var body = native.Take(57).ToArray();
            var shape = X64LookupGuardManagedThrowProof.TryProveShape(body);
            var proof = X64LookupGuardManagedThrowProof.Find(method);
            Assert.That(shape, Is.Not.Null);
            Assert.That(proof, Is.Not.Null);
            Assert.Multiple(() =>
            {
                Assert.That(proof!.ExceptionType.FullName, Is.EqualTo("System.ArgumentException"));
                Assert.That(proof.Constructor.Parameters.Single().ParameterType,
                    Is.SameAs(app.SystemTypes.SystemStringType));
                Assert.That(proof.IntegerFormatter.DeclaringType, Is.SameAs(app.SystemTypes.SystemInt32Type));
                Assert.That(proof.IntegerFormatter.Name, Is.EqualTo("ToString"));
                Assert.That(proof.Concat.Name, Is.EqualTo("Concat"));
                Assert.That(proof.StringGetter != null, Is.EqualTo(usesGetter));
                Assert.That(proof.StringField.Visibility,
                    Is.EqualTo(usesGetter ? FieldAttributes.Private : FieldAttributes.Public));
                Assert.That(X64LookupGuardManagedThrowProof.TryProveShape(body[..56]), Is.Null,
                    "The final nonvolatile restore belongs to the complete native body.");
            });

            var mutations = new (int Index, Action<Instruction[]> Change)[]
            {
                (0, changed => changed[0].Op1Register = Register.ECX),
                (3, changed => changed[3].NearBranch64 = body[11].IP),
                (8, changed => changed[8].MemoryDisplacement64++),
                (14, changed => changed[14].NearBranch64 = body[26].IP),
                (15, changed => changed[15].MemoryDisplacement64 = 0x30),
                (20, changed => changed[20].Code = Code.Jne_rel8_64),
                (21, changed => changed[21].Op0Register = Register.EAX),
                (27, changed => changed[27].MemoryDisplacement64 = 0x30),
                (29, changed => changed[29].Op1Register = Register.RBX),
                (33, changed => changed[33].NearBranch64 = shape!.Allocator),
                (36, changed => changed[36].Op1Register = Register.RDI),
                (44, changed => changed[44].Op1Register = Register.RBX),
                (47, changed => changed[47].Op1Register = Register.RAX),
                (54, changed => changed[54].Code = Code.Jmp_rel32_64),
                (56, changed => changed[56].MemoryDisplacement64 = 0x20),
            };
            foreach (var (index, change) in mutations)
            {
                var altered = body.ToArray();
                change(altered);
                Assert.That(X64LookupGuardManagedThrowProof.TryProveShape(altered), Is.Null,
                    $"Instruction {index} changes a proved guard, argument, width or effect.");
            }

            foreach (var altered in new[]
            {
                shape! with { OnceFlag = shape.ProducerTypeInfoSlot },
                shape with { ProducerTypeInfoSlot = shape.ExceptionTypeInfoSlot },
                shape with { LiteralSlot = shape.MethodDefSlot },
                shape with { MethodDefSlot = shape.ProducerTypeInfoSlot },
                shape with { Allocator = shape.Producer },
                shape with { RuntimeNullThrow = shape.NullGuard },
                shape with { NullGuard = shape.RuntimeNullThrow },
                shape with { Constructor = shape.Concat },
                shape with { IntegerFormatter = shape.Lookup },
                shape with { Raiser = shape.Constructor },
                shape with { StringOffset = shape.StringOffset + 1 },
            })
                Assert.That(X64LookupGuardManagedThrowProof.BindProvedShape(method, altered), Is.Null,
                    "Slots, managed identities and exact runtime helper bodies must bind independently.");
            var sibling = fixtureMethods.Single(candidate => candidate.Name ==
                (usesGetter ? "Read" : "ReadProperty"));
            sibling.EnsureRawBytes();
            var siblingShape = X64LookupGuardManagedThrowProof.TryProveShape(
                X64NativeInstructionReader.ReadRootBody(sibling)!.Take(57).ToArray())!;
            Assert.That(X64LookupGuardManagedThrowProof.BindProvedShape(method,
                shape! with { MethodDefSlot = siblingShape.MethodDefSlot }), Is.Null,
                "A valid MethodDef slot for a different caller cannot supply the throwing frame.");

            void Reject(Action change, Action restore, string reason)
            {
                try
                {
                    change();
                    Assert.That(X64LookupGuardManagedThrowProof.Find(method), Is.Null, reason);
                }
                finally { restore(); }
                Assert.That(X64LookupGuardManagedThrowProof.Find(method), Is.Not.Null,
                    "Restoring original evidence reestablishes the complete proof.");
            }

            var initializer = proof!.Producer.DeclaringType!.Methods.Single(candidate => candidate.Name == ".cctor");
            var initializerAttributes = initializer.Definition!.flags;
            foreach (var specialName in new[] { MethodAttributes.SpecialName, MethodAttributes.RTSpecialName })
                Reject(() => initializer.Definition.flags =
                        (ushort)(initializerAttributes & ~(ushort)specialName),
                    () => initializer.Definition.flags = initializerAttributes,
                    "The runtime class-init call requires a canonical original type initializer declaration.");
            Reject(() => initializer.Definition.flags = (ushort)(initializerAttributes | (ushort)MethodAttributes.Virtual),
                () => initializer.Definition.flags = initializerAttributes,
                "A virtual declaration cannot supply the evidenced static type-initializer ABI.");
            foreach (var target in new[] { method, proof.Producer, initializer, proof.Lookup,
                         proof.IntegerFormatter, proof.Concat, proof.Constructor }
                         .Concat(proof.StringGetter == null ? [] : new[] { proof.StringGetter }))
            {
                var flags = target.Definition!.iflags;
                foreach (var forbidden in new ushort[]
                    { (ushort)MethodImplAttributes.Synchronized, (ushort)MethodImplAttributes.InternalCall, 0xF000 })
                    Reject(() => target.Definition.iflags = (ushort)(flags | forbidden),
                        () => target.Definition.iflags = flags,
                        "Synchronized, runtime-provided and extension ABIs are not established by this body.");
            }

            foreach (var target in new[] { method, proof.Producer, proof.Lookup, proof.Constructor })
            {
                var bindings = app.MethodsByAddress[target.UnderlyingPointer];
                Reject(() => bindings.Add(target), () => bindings.RemoveAt(bindings.Count - 1),
                    "A duplicate managed native destination is ambiguous.");
                Reject(() => app.MethodsByAddress[target.UnderlyingPointer] = [proof.Concat],
                    () => app.MethodsByAddress[target.UnderlyingPointer] = bindings,
                    "A displaced native destination does not bind the original managed declaration.");
            }
            var bytes = method.RawBytes;
            var conflicting = bytes.AsSpan().ToArray();
            conflicting[0] ^= 1;
            Reject(() => method.RawBytes = new BinarySlice(conflicting),
                () => method.RawBytes = bytes, "Cached native evidence must agree with the PE.");
            Reject(() => method.Parameters[0].ParameterType = app.SystemTypes.SystemUInt32Type,
                () => method.Parameters[0].OverrideParameterType = null,
                "The preserved and formatted value is the original signed Int32 parameter.");
            Reject(() => proof.Constructor.Parameters[0].ParameterType = app.SystemTypes.SystemObjectType,
                () => proof.Constructor.Parameters[0].OverrideParameterType = null,
                "The raised exception must invoke the exact String constructor.");
            var fieldOffset = proof.StringField.Offset;
            Reject(() => proof.StringField.Offset++, () => proof.StringField.Offset = fieldOffset,
                "The normal exit must read its original, nonoverlapping String storage.");
            var fieldOwnerDefinition = proof.StringField.DeclaringType.Definition!;
            var fieldOwnerFlags = fieldOwnerDefinition.Flags;
            Reject(() => fieldOwnerDefinition.Flags =
                    fieldOwnerFlags & ~(uint)TypeAttributes.VisibilityMask,
                () => fieldOwnerDefinition.Flags = fieldOwnerFlags,
                "Original raw metadata must keep the accessed storage owner source-accessible.");
            var callerOwner = method.DeclaringType!;
            var callerDefinition = callerOwner.Definition!;
            var callerDeclaringType = callerOwner.DeclaringType;
            var callerDeclaringIndex = callerDefinition.DeclaringTypeIndex;
            var callerOwnerFlags = callerDefinition.Flags;
            Reject(() =>
                {
                    callerDefinition.Flags = (callerOwnerFlags & ~(uint)TypeAttributes.VisibilityMask) |
                        (uint)TypeAttributes.NestedPublic;
                    callerDefinition.DeclaringTypeIndex = callerDefinition.ByvalTypeIndex;
                    callerOwner.DeclaringType = callerOwner;
                },
                () =>
                {
                    callerOwner.DeclaringType = callerDeclaringType;
                    callerDefinition.DeclaringTypeIndex = callerDeclaringIndex;
                    callerDefinition.Flags = callerOwnerFlags;
                }, "Cyclic original nested ownership cannot establish accessible class identity.");
            foreach (var owner in new[] { proof.Producer.ReturnType, proof.Lookup.DeclaringType! }.Distinct())
            {
                var rawBase = owner.Definition!.RawBaseType!;
                var modifiers = rawBase.NumMods;
                var byref = rawBase.Byref;
                var pinned = rawBase.Pinned;
                Reject(() => rawBase.NumMods = 1, () => rawBase.NumMods = modifiers,
                    "Every original lookup receiver base descriptor must retain its unmodified reference ABI.");
                Reject(() => rawBase.Byref = 1, () => rawBase.Byref = byref,
                    "A byref base descriptor cannot establish the evidenced ordinary receiver.");
                Reject(() => rawBase.Pinned = 1, () => rawBase.Pinned = pinned,
                    "A pinned base descriptor is not part of the evidenced ordinary receiver declaration.");
            }
            Reject(() => app.MethodsByAddress[method.UnderlyingPointer + 1] = [method],
                () => app.MethodsByAddress.Remove(method.UnderlyingPointer + 1),
                "An interior managed entry invalidates complete body ownership.");

            if (proof.StringGetter is { } getter)
            {
                var property = getter.DeclaringType!.Properties.Single(candidate => ReferenceEquals(candidate.Getter, getter));
                var visibility = getter.Attributes;
                Reject(() => getter.Attributes = (visibility & ~MethodAttributes.MemberAccessMask) | MethodAttributes.Private,
                    () => getter.OverrideAttributes = null, "The substitute getter must remain source-accessible.");
                Reject(() => getter.Attributes |= MethodAttributes.Virtual, () => getter.OverrideAttributes = null,
                    "Virtual getter dispatch is not equivalent to this inlined private field read.");
                Reject(() => getter.ReturnType = app.SystemTypes.SystemObjectType,
                    () => getter.OverrideReturnType = null, "The getter's original String signature is required.");
                Reject(() => property.PropertyType = app.SystemTypes.SystemObjectType,
                    () => property.OverridePropertyType = null, "The property's original String signature is required.");
                Reject(() => getter.DeclaringType.Properties.Add(property),
                    () => getter.DeclaringType.Properties.RemoveAt(getter.DeclaringType.Properties.Count - 1),
                    "Only one original applicable property may explain the private field read.");
                var originalGetterIndex = property.Definition!.get;
                Reject(() => property.Definition.get = property.Definition.set,
                    () => property.Definition.get = originalGetterIndex,
                    "Changing the original property-to-getter metadata association is rejected.");
                var getterBytes = getter.RawBytes;
                var changedGetter = getterBytes.AsSpan().ToArray();
                changedGetter[0] ^= 1;
                Reject(() => getter.RawBytes = new BinarySlice(changedGetter),
                    () => getter.RawBytes = getterBytes, "The replacement getter requires unchanged native bytes.");
            }

            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var output = method.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            Assert.That(X64LookupGuardManagedThrowRecovery.TryGenerate(method, output), Is.True);
            var il = output.CilMethodBody!.Instructions;
            Assert.Multiple(() =>
            {
                Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Callvirt), Is.EqualTo(1));
                Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Ldfld),
                    Is.EqualTo(usesGetter ? 0 : 1), "Inaccessible private storage is never emitted as a direct read.");
                Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Newobj), Is.EqualTo(1));
                Assert.That(il.Count(instruction => instruction.OpCode == CilOpCodes.Throw), Is.EqualTo(1));
                var format = il.Single(instruction => ReferenceEquals(instruction.Operand,
                    proof.IntegerFormatter.GetExtraData<MethodDefinition>("AsmResolverMethod")));
                var literal = il.Single(instruction => instruction.OpCode == CilOpCodes.Ldstr);
                Assert.That(il.IndexOf(format), Is.LessThan(il.IndexOf(literal)),
                    "Formatting remains before literal resolution and message construction.");
            });
            Assert.DoesNotThrow(() => output.CilMethodBody.ComputeMaxStack());
        }
        finally { Cpp2IlApi.ResetInternalState(); }
    }
}
