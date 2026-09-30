using System;
using System.Linq;
using System.Reflection;
using Cpp2IL.Core.InstructionSets;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Tests;

/// <summary>Synthetic member access over public metadata; no native bodies are recovered here.</summary>
[NonParallelizable]
public class GuardedCallTargetAccessibilityTests
{
    private ApplicationAnalysisContext _app = null!;
    private TypeAnalysisContext _nested = null!;
    private TypeAnalysisContext _outer = null!;
    private InjectedMethodAnalysisContext _private = null!;

    [OneTimeSetUp]
    public void LoadPublicNestingModel()
    {
        Cpp2IlApi.ResetInternalState();
        _app = TestGameLoader.LoadSimple2019Game();
        _nested = _app.Assemblies.SelectMany(assembly => assembly.Types).First(type =>
            type.Definition != null && type.DeclaringType is { Definition: not null, DeclaringType: null } &&
            type.DeclaringType.NestedTypes.Contains(type));
        _outer = _nested.DeclaringType!;
        _private = PrivateMethod(_outer);
    }

    [OneTimeTearDown]
    public void ReleasePublicModel() => Cpp2IlApi.ResetInternalState();

    [Test]
    public void OriginalNestedBodyCanAccessAnEnclosingPrivateMember()
    {
        Assert.That(X64GuardedEnumParameterCallProof.AccessibleTarget(_nested, _private), Is.True);
    }

    [Test]
    public void ACommonOuterTypeDoesNotAllowPrivateAccessToASiblingBody()
    {
        var parent = _app.Assemblies.SelectMany(assembly => assembly.Types)
            .First(type => type.Definition != null && type.NestedTypes.Count >= 2);
        var caller = parent.NestedTypes[0];
        var target = PrivateMethod(parent.NestedTypes[1]);
        Assert.That(X64GuardedEnumParameterCallProof.AccessibleTarget(caller, target), Is.False);
        Assert.That(X64GuardedEnumParameterCallProof.AccessibleTarget(parent, target), Is.False,
            "A containing body is outside the private member's own declaring-type body.");
    }

    [Test]
    public void UnrelatedAndInjectedNestingCannotEstablishPrivateAccess()
    {
        var unrelated = new InjectedTypeAnalysisContext(_outer.DeclaringAssembly, "Synthetic",
            "Unrelated", null, TypeAttributes.Public);
        Assert.That(X64GuardedEnumParameterCallProof.AccessibleTarget(unrelated, _private), Is.False);
        unrelated.DeclaringType = _outer;
        _outer.NestedTypes.Add(unrelated);
        try
        {
            Assert.That(X64GuardedEnumParameterCallProof.AccessibleTarget(unrelated, _private), Is.False,
                "An injected parent pointer has no original metadata relation.");
        }
        finally { _outer.NestedTypes.Remove(unrelated); }
    }

    [TestCase("parent")]
    [TestCase("name")]
    [TestCase("namespace")]
    [TestCase("attributes")]
    [TestCase("membership")]
    public void ChangedNestingOrIdentityCannotGrantPrivateAccess(string change)
    {
        var parent = _nested.DeclaringType;
        var name = _nested.OverrideName;
        var ns = _nested.OverrideNamespace;
        var attributes = _nested.OverrideAttributes;
        var position = _outer.NestedTypes.IndexOf(_nested);
        try
        {
            switch (change)
            {
                case "parent": _nested.DeclaringType = _app.SystemTypes.SystemObjectType; break;
                case "name": _nested.OverrideName = "ChangedNestedIdentity"; break;
                case "namespace": _nested.OverrideNamespace = "ChangedNamespace"; break;
                case "attributes": _nested.OverrideAttributes = TypeAttributes.Public; break;
                case "membership": _outer.NestedTypes.RemoveAt(position); break;
            }
            Assert.That(X64GuardedEnumParameterCallProof.AccessibleTarget(_nested, _private), Is.False);
        }
        finally
        {
            _nested.DeclaringType = parent;
            _nested.OverrideName = name;
            _nested.OverrideNamespace = ns;
            _nested.OverrideAttributes = attributes;
            if (!_outer.NestedTypes.Contains(_nested))
                _outer.NestedTypes.Insert(position, _nested);
        }
        Assert.That(X64GuardedEnumParameterCallProof.AccessibleTarget(_nested, _private), Is.True);
    }

    [Test]
    public void MalformedMetadataParentAndConsistentCyclesAreRejected()
    {
        var definition = _nested.Definition!;
        var rawParent = definition.DeclaringTypeIndex;
        var parent = _nested.DeclaringType;
        definition.DeclaringTypeIndex = definition.ByvalTypeIndex;
        try
        {
            Assert.That(X64GuardedEnumParameterCallProof.AccessibleTarget(_nested, _private), Is.False,
                "The context parent must agree with its original metadata relation.");
            _nested.DeclaringType = _nested;
            _nested.NestedTypes.Add(_nested);
            Assert.That(X64GuardedEnumParameterCallProof.AccessibleTarget(_nested, _private), Is.False,
                "A matching but cyclic parent relation is not a type body.");
        }
        finally
        {
            definition.DeclaringTypeIndex = rawParent;
            _nested.DeclaringType = parent;
            _nested.NestedTypes.Remove(_nested);
        }
    }

    private InjectedMethodAnalysisContext PrivateMethod(TypeAnalysisContext owner) =>
        new(owner, "SyntheticPrivateTarget", _app.SystemTypes.SystemVoidType,
            MethodAttributes.Private, []);
}
