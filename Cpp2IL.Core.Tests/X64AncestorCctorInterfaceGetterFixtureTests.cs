using System;
using System.IO;
using System.Linq;
using System.Reflection;
using AssetRipper.Primitives;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.OutputFormats;
using MethodDefinition = AsmResolver.DotNet.MethodDefinition;

namespace Cpp2IL.Core.Tests;

[NonParallelizable]
public class X64AncestorCctorInterfaceGetterFixtureTests
{
    [Test]
    public void FinalInterfaceGetterRequiresUnchangedAncestorClassConstructor()
    {
        var directory = Environment.GetEnvironmentVariable(
            "CPP2IL_ANCESTOR_CCTOR_INTERFACE_GETTER_FIXTURE_INPUT");
        if (string.IsNullOrEmpty(directory))
            Assert.Ignore("Set CPP2IL_ANCESTOR_CCTOR_INTERFACE_GETTER_FIXTURE_INPUT to the neutral exact player input.");

        Cpp2IlApi.ResetInternalState();
        TestGameLoader.EnsureInit();
        try
        {
            Cpp2IlApi.InitializeLibCpp2Il(Path.Combine(directory!, "GameAssembly.dll"),
                Path.Combine(directory!, "RecoveryFixture_Data", "il2cpp_data", "Metadata", "global-metadata.dat"),
                UnityVersion.Parse("2021.3.35f1"));
            var app = Cpp2IlApi.CurrentAppContext!;
            _ = new AsmResolverDllOutputFormatEmpty().BuildAssemblies(app);
            var owner = app.GetAssemblyByName("AncestorCctorInterfaceGetterFixture")!.Types
                .Single(type => type.Name == "FlagComponent");
            var getter = owner.Methods.Single(method => method.IsVirtual && method.IsFinal &&
                method.Overrides.Count == 1);
            var ancestor = owner.BaseType;
            while (ancestor != null && !ancestor.Definition!.HasCctor)
                ancestor = ancestor.BaseType;
            Assert.That(ancestor, Is.Not.Null,
                "The fixture must exercise an ancestor with a native class initializer.");
            var constructor = ancestor!.Methods.Single(method => method.Name == ".cctor");

            Assert.That(X64FinalInterfaceBooleanFieldGetterProof.Find(getter), Is.Not.Null);
            try
            {
                constructor.OverrideAttributes = constructor.DefaultAttributes & ~MethodAttributes.Static;
                Assert.That(X64FinalInterfaceBooleanFieldGetterProof.Find(getter), Is.Null);
            }
            finally
            {
                constructor.OverrideAttributes = null;
            }
            var originalParameterCount = constructor.Definition!.parameterCount;
            try
            {
                constructor.Definition.parameterCount = 1;
                Assert.That(X64FinalInterfaceBooleanFieldGetterProof.Find(getter), Is.Null);
            }
            finally
            {
                constructor.Definition.parameterCount = originalParameterCount;
            }

            foreach (var attributes in new[]
            {
                constructor.DefaultAttributes | MethodAttributes.Virtual,
                constructor.DefaultAttributes | MethodAttributes.PinvokeImpl,
                constructor.DefaultAttributes & ~MethodAttributes.SpecialName,
                constructor.DefaultAttributes & ~MethodAttributes.RTSpecialName,
            })
            {
                var original = constructor.Definition.flags;
                try
                {
                    // Change the input descriptor itself: comparing an override
                    // with that descriptor alone must not admit invalid cctors.
                    constructor.Definition.flags = (ushort)attributes;
                    Assert.That(X64FinalInterfaceBooleanFieldGetterProof.Find(getter), Is.Null,
                        $"Invalid class initializer attributes: {attributes}");
                }
                finally { constructor.Definition.flags = original; }
            }

            var originalImplementation = constructor.Definition.iflags;
            foreach (var invalid in new ushort[] { (ushort)MethodImplAttributes.InternalCall, 0xF000 })
            {
                try
                {
                    constructor.Definition.iflags |= invalid;
                    Assert.That(X64FinalInterfaceBooleanFieldGetterProof.Find(getter), Is.Null,
                        "The initializer must retain the ordinary managed declaration and ABI.");
                }
                finally { constructor.Definition.iflags = originalImplementation; }
            }

            var originalSlot = constructor.Definition.slot;
            try
            {
                constructor.Definition.slot = 0;
                Assert.That(X64FinalInterfaceBooleanFieldGetterProof.Find(getter), Is.Null,
                    "A static class initializer cannot occupy a virtual dispatch slot.");
            }
            finally { constructor.Definition.slot = originalSlot; }

            var constructorReturn = constructor.Definition.RawReturnType!;
            var originalReturnByref = constructorReturn.Byref;
            try
            {
                constructorReturn.Byref = 1;
                Assert.That(X64FinalInterfaceBooleanFieldGetterProof.Find(getter), Is.Null,
                    "The class initializer must retain an ordinary void return.");
            }
            finally { constructorReturn.Byref = originalReturnByref; }

            var ownerBitfield = owner.Definition!.Bitfield;
            try
            {
                owner.Definition.Bitfield |= 8;
                Assert.That(X64FinalInterfaceBooleanFieldGetterProof.Find(getter), Is.Null,
                    "The allowance applies only to ancestors; owner initialization remains unproved.");
            }
            finally { owner.Definition.Bitfield = ownerBitfield; }

            var contractDefinition = getter.Overrides.Single().Definition!;
            var contractImplementation = contractDefinition.iflags;
            try
            {
                contractDefinition.iflags |= 0xF000;
                Assert.That(contractDefinition.IsUnmanagedCallersOnly, Is.True);
                Assert.That(X64FinalInterfaceBooleanFieldGetterProof.Find(getter), Is.Null,
                    "The interface declaration must retain its ordinary managed dispatch ABI.");
            }
            finally { contractDefinition.iflags = contractImplementation; }

            Assert.That(X64FinalInterfaceBooleanFieldGetterProof.Find(getter), Is.Not.Null);
            getter.Analyze();
            var generated = getter.GetExtraData<MethodDefinition>("AsmResolverMethod")!;
            Assert.DoesNotThrow(() => IlGenerator.GenerateIl(getter, generated));
            try
            {
                contractDefinition.iflags |= 0xF000;
                Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(getter, generated),
                    "Emission must reject a contract ABI changed after lifting.");
            }
            finally { contractDefinition.iflags = contractImplementation; }
            Assert.DoesNotThrow(() => IlGenerator.GenerateIl(getter, generated));
            var constructorFlags = constructor.Definition.flags;
            try
            {
                constructor.Definition.flags ^= (ushort)MethodAttributes.HideBySig;
                Assert.That(X64FinalInterfaceBooleanFieldGetterProof.Find(getter), Is.Not.Null,
                    "This remains a valid initializer declaration for a fresh proof.");
                Assert.Throws<DecompilerException>(() => IlGenerator.GenerateIl(getter, generated),
                    "Emission must reject an ancestor declaration changed after lifting.");
            }
            finally { constructor.Definition.flags = constructorFlags; }
            Assert.DoesNotThrow(() => IlGenerator.GenerateIl(getter, generated));
        }
        finally
        {
            Cpp2IlApi.ResetInternalState();
        }
    }
}
