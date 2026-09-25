#!/usr/bin/env python3
"""Compile/build a synthetic or recovered fixture in an isolated exact-version project."""

import argparse
import alias_ambiguity
import array_access
import array_sequence
import array_call
import boolean_parameter_branch
import enum_passthrough
import external_references
import numerics_reference
import static_field_getter
import catch_divide
import class_cast_lookup
import reference_field
import reference_array
import exception_regions
import field_array
import field_guard
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import time

import byte_fields
import boolean_getter
import boolean_getter_metadata
import byte_threshold
import composed_array
import composed_read
import parameter_array
import dense_switch
import float_array
import float_comparison
import forwarded_argument
import struct_forward_call
import struct_static_forward_call
import integer_extensions
import instance_reference_property
import instance_reference_setter
import iterator_factory
import iterator_factory_manual
import iterator_factory_variant
import iterator_factory_direct_ctor
import literal_concat
import metadata_guard_move
import metadata_guard_parameter
import metadata_forwarding
import metadata_accessor
import loop_calls
import narrow_array
import nested_boolean_store
import nested_boolean_getter
import nested_flag_setter
import reference_null
import reference_store
import runtime_cast_concat
import static_literal_concat
import throw_only
import scalar_truncation
import sequential_null_guards
import call_result_null_guard
import static_word_getter
import static_scalar_setter
import xmm_spill
import xmm_ref_mutation
import word_array
import word_fields
import zero_arg_field_call
import virtual_string_call
import generic_dispatch
import guarded_sink
import folded_state_constructor
import constructor_thunk_chain


VERSION = "2021.3.35f1"
ROOT = Path(__file__).resolve().parent.parent
VALIDATION = ROOT / "Validation"
VALUES = [-(2**31), -(2**31) + 1, -17, -1, 0, 1, 17, 2**31 - 2, 2**31 - 1]
PROFILES = {
    "catch-divide": {"assembly": "ExceptionRegionFixture", "source": VALIDATION / "CatchDivideFixture", "methods": 1},
    "exception-regions": {"assembly": "ExceptionRegionFixture", "source": VALIDATION / "ExceptionRegionFixture", "methods": 2},
    "array-access": {"assembly": "ArrayAccessFixture", "source": VALIDATION / "ArrayAccessFixture", "methods": 8},
    "array-sequence": {"assembly": "ArraySequenceFixture", "source": VALIDATION / "ArraySequenceFixture", "methods": 2},
    "field-array": {"assembly": "FieldArrayFixture", "source": VALIDATION / "FieldArrayFixture", "methods": 4},
    "narrow-array": {"assembly": "NarrowArrayFixture", "source": VALIDATION / "NarrowArrayFixture", "methods": 4},
    "nested-boolean-store": {"assembly": "NestedBooleanStoreFixture", "source": VALIDATION / "NestedBooleanStoreFixture", "methods": 3},
    "nested-boolean-getter": {"assembly": "NestedBooleanGetterFixture", "source": VALIDATION / "NestedBooleanGetterFixture", "methods": 4},
    "unused-reference-nested-store": {"assembly": "NestedFlagSetterFixture", "source": VALIDATION / "NestedFlagSetterFixture", "methods": 4},
    "float-array": {"assembly": "FloatArrayFixture", "source": VALIDATION / "FloatArrayFixture", "methods": 2},
    "word-array": {"assembly": "WordArrayFixture", "source": VALIDATION / "WordArrayFixture", "methods": 2},
    "reference-array": {"assembly": "ReferenceArrayFixture", "source": VALIDATION / "ReferenceArrayFixture", "methods": 3},
    "boolean-getter": {"assembly": "BooleanGetterFixture", "source": VALIDATION / "BooleanGetterFixture", "methods": 5},
    "boolean-getter-metadata": {"assembly": "BooleanGetterMetadataFixture", "source": VALIDATION / "BooleanGetterMetadataFixture", "methods": 11},
    "virtual-string-call": {"assembly": "VirtualStringCallFixture", "source": VALIDATION / "VirtualStringCallFixture", "methods": 5},
    "generic-dispatch": {"assembly": "GenericDispatchFixture", "source": VALIDATION / "GenericDispatchFixture", "methods": 4,
                         "noManagedBody": (("GenericDispatchFixture.IRead`1", "Read"),)},
    "guarded-sink": {"assembly": "GuardedSinkFixture", "source": VALIDATION / "GuardedSinkFixture", "methods": 3},
    "folded-state-constructor": {"assembly": "FoldedStateConstructorFixture", "source": VALIDATION / "FoldedStateConstructorFixture", "methods": 2},
    "constructor-thunk-chain": {"assembly": "ConstructorThunkChainFixture", "source": VALIDATION / "ConstructorThunkChainFixture", "methods": 7},
    "array-call": {"assembly": "ArrayCallFixture", "source": VALIDATION / "ArrayCallFixture", "methods": 10},
    "enum-passthrough": {"assembly": "EnumPassthroughFixture", "source": VALIDATION / "EnumPassthroughFixture", "methods": 4},
    "static-field-getter": {"assembly": "StaticFieldGetterFixture", "source": VALIDATION / "StaticFieldGetterFixture", "methods": 4},
    "static-word-getter": {"assembly": "StaticWordGetterFixture", "source": VALIDATION / "StaticWordGetterFixture", "methods": 2},
    "static-scalar-setter": {"assembly": "StaticScalarSetterFixture", "source": VALIDATION / "StaticScalarSetterFixture", "methods": 1},
    "instance-reference-property": {"assembly": "InstanceReferencePropertyFixture", "source": VALIDATION / "InstanceReferencePropertyFixture", "methods": 5},
    "instance-reference-setter": {"assembly": "InstanceReferenceSetterFixture", "source": VALIDATION / "InstanceReferenceSetterFixture", "methods": 4},
    "reference-field": {"assembly": "ReferenceFieldFixture", "source": VALIDATION / "ReferenceFieldFixture", "methods": 25},
    "reference-null": {"assembly": "ReferenceNullFixture", "source": VALIDATION / "ReferenceNullFixture", "methods": 3},
    "sequential-null-guards": {"assembly": "SequentialNullGuardFixture", "source": VALIDATION / "SequentialNullGuardFixture", "methods": 3},
    "call-result-null-guards": {"assembly": "CallResultNullGuardFixture", "source": VALIDATION / "CallResultNullGuardFixture", "methods": 13},
    "reference-store": {"assembly": "ReferenceStoreFixture", "source": VALIDATION / "ReferenceStoreFixture", "methods": 2},
    "external-references": {"assembly": "ExternalReferenceFixture", "source": VALIDATION / "ExternalReferenceFixture", "methods": 1},
    "numerics-reference": {"assembly": "NumericsReferenceFixture", "source": VALIDATION / "NumericsReferenceFixture", "methods": 1},
    "iterator-factory": {"assembly": "IteratorFactoryFixture", "source": VALIDATION / "IteratorFactoryFixture", "methods": 8},
    "iterator-factory-manual": {"assembly": "IteratorFactoryManualFixture", "source": VALIDATION / "IteratorFactoryManualFixture", "methods": 7},
    "iterator-factory-variant": {"assembly": "IteratorFactoryVariantFixture", "source": VALIDATION / "IteratorFactoryVariantFixture", "methods": 8},
    "iterator-factory-direct-ctor": {"assembly": "IteratorFactoryDirectCtorFixture", "source": VALIDATION / "IteratorFactoryDirectCtorFixture", "methods": 8},
    "literal-concat": {"assembly": "LiteralConcatFixture", "source": VALIDATION / "LiteralConcatFixture", "methods": 7},
    "class-cast-lookup": {"assembly": "ClassCastLookupFixture", "source": VALIDATION / "ClassCastLookupFixture", "methods": 5},
    "runtime-cast-concat": {"assembly": "RuntimeCastConcatFixture", "source": VALIDATION / "RuntimeCastConcatFixture", "methods": 10},
    "static-literal-concat": {"assembly": "StaticLiteralConcatFixture", "source": VALIDATION / "StaticLiteralConcatFixture", "methods": 1},
    "throw-only": {"assembly": "ThrowOnlyFixture", "source": VALIDATION / "ThrowOnlyFixture", "methods": 5},
    "metadata-guard-move": {"assembly": "MetadataGuardMoveFixture", "source": VALIDATION / "MetadataGuardMoveFixture", "methods": 3},
    "metadata-guard-parameter": {"assembly": "MetadataGuardParameterFixture", "source": VALIDATION / "MetadataGuardParameterFixture", "methods": 1},
    "metadata-forwarding": {"assembly": "MetadataForwardingFixture", "source": VALIDATION / "MetadataForwardingFixture", "methods": 5},
    "metadata-accessor": {"assembly": "MetadataAccessorFixture", "source": VALIDATION / "MetadataAccessorFixture", "methods": 6},
    "alias-ambiguity": {"assembly": "AliasAmbiguityFixture", "source": VALIDATION / "AliasAmbiguityFixture", "methods": 3},
    "boolean-parameter-branch": {"assembly": "BooleanParameterBranchFixture", "source": VALIDATION / "BooleanParameterBranchFixture", "methods": 3},
    "byte-threshold": {"assembly": "ByteThresholdFixture", "source": VALIDATION / "ByteThresholdFixture", "methods": 2},
    "dense-switch": {"assembly": "DenseSwitchFixture", "source": VALIDATION / "DenseSwitchFixture", "methods": 2},
    "composed-array": {"assembly": "ComposedArrayFixture", "source": VALIDATION / "ComposedArrayFixture", "methods": 6},
    "composed-read": {"assembly": "ComposedReadFixture", "source": VALIDATION / "ComposedReadFixture", "methods": 4},
    "parameter-array": {"assembly": "ParameterArrayFixture", "source": VALIDATION / "ParameterArrayFixture", "methods": 3},
    "field-guard": {"assembly": "FieldGuardFixture", "source": VALIDATION / "FieldGuardFixture", "methods": 19},
    "zero-arg-field-call": {"assembly": "ZeroArgFieldCallFixture", "source": VALIDATION / "ZeroArgFieldCallFixture", "methods": 4},
    "forwarded-argument": {"assembly": "ForwardedArgumentFixture", "source": VALIDATION / "ForwardedArgumentFixture", "methods": 4},
    "struct-forward-call": {"assembly": "StructForwardCallFixture", "source": VALIDATION / "StructForwardCallFixture", "methods": 5},
    "struct-static-forward-call": {"assembly": "StructStaticForwardCallFixture", "source": VALIDATION / "StructStaticForwardCallFixture", "methods": 6},
    "scalar-truncation": {"assembly": "ScalarTruncationFixture", "source": VALIDATION / "ScalarTruncationFixture", "methods": 2},
    "loop-calls": {"assembly": "LoopCallFixture", "source": VALIDATION / "LoopCallFixture", "methods": 4},
    "word-fields": {"assembly": "WordFieldFixture", "source": VALIDATION / "WordFieldFixture", "methods": 4},
    "integer-extensions": {"assembly": "IntegerExtensionFixture", "source": VALIDATION / "IntegerExtensionFixture", "methods": 12},
    "byte-fields": {"assembly": "ByteFieldFixture", "source": VALIDATION / "ByteFieldFixture", "methods": 4},
    "float-comparisons": {"assembly": "FloatComparisonFixture", "source": VALIDATION / "FloatComparisonFixture", "methods": 12},
    "xmm-spill": {"assembly": "XmmSpillFixture", "source": VALIDATION / "XmmSpillFixture", "methods": 2},
    "xmm-ref-mutation": {"assembly": "XmmRefMutationFixture", "source": VALIDATION / "XmmRefMutationFixture", "methods": 2},
    "components": {"assembly": "ComponentFixture", "source": VALIDATION / "ComponentFixture", "methods": 3},
    "metadata-literal": {"assembly": "MetadataLiteralFixture", "source": VALIDATION / "MetadataLiteralFixture", "methods": 1},
    "narrow-comparisons": {"assembly": "NarrowComparisonFixture", "source": VALIDATION / "NarrowComparisonFixture", "methods": 14},
    "division": {"assembly": "DivisionFixture", "source": VALIDATION / "DivisionFixture", "methods": 8},
    "shifts": {"assembly": "ShiftFixture", "source": VALIDATION / "ShiftFixture", "methods": 4},
    "arithmetic": {"assembly": "RecoveryFixture", "source": VALIDATION / "Fixture", "methods": 4},
    "integers": {"assembly": "IntegerFixture", "source": VALIDATION / "IntegerFixture", "methods": 8},
    "scalar-structs": {"assembly": "ScalarStructFixture", "source": VALIDATION / "ScalarStructFixture", "methods": 4},
    "scalar-structs-negative": {"assembly": "ScalarStructNegativeFixture", "source": VALIDATION / "ScalarStructNegativeFixture", "methods": 3},
}

EMBEDDED_FIXTURE_PACKAGES = {
    "virtual-string-call": {
        "name": "com.example.cast-hierarchy",
        "source": VALIDATION / "VirtualStringCallDependencies",
        "files": ("package.json", "Runtime/Neutral.CastHierarchy.asmdef", "Runtime/Hierarchy.cs"),
        "assembly": "Neutral.CastHierarchy.dll",
    },
    "generic-dispatch": {
        "name": "com.example.generic-dispatch",
        "source": VALIDATION / "GenericDispatchDependencies",
        "files": ("package.json", "Runtime/Neutral.GenericDispatch.asmdef", "Runtime/DispatchBase.cs"),
        "assembly": "Neutral.GenericDispatch.dll",
    },
    "guarded-sink": {
        "name": "com.example.guarded-sink",
        "source": VALIDATION / "GuardedSinkDependencies",
        "files": ("package.json", "Runtime/Neutral.GuardedSink.asmdef", "Runtime/GuardedSink.cs"),
        "assembly": "Neutral.GuardedSink.dll",
    },
}

def write_json(path, value):
    path.write_text(json.dumps(value, indent=2) + "\n", encoding="utf-8")


def int32(value):
    return (value + 2**31) % 2**32 - 2**31


def verify_behavior(path, stage, profile="arithmetic"):
    if profile == "alias-ambiguity":
        return alias_ambiguity.verify(path, stage, VERSION)
    if profile == "boolean-parameter-branch":
        return boolean_parameter_branch.verify(path, stage, VERSION)
    if profile == "catch-divide":
        return catch_divide.verify(path, stage, VERSION)
    if profile == "exception-regions":
        return exception_regions.verify(path, stage, VERSION)
    if profile == "array-access":
        return array_access.verify(path, stage, VERSION)
    if profile == "array-sequence":
        return array_sequence.verify(path, stage, VERSION)
    if profile == "field-array":
        return field_array.verify(path, stage, VERSION)
    if profile == "narrow-array":
        return narrow_array.verify(path, stage, VERSION)
    if profile == "nested-boolean-store":
        return nested_boolean_store.verify(path, stage, VERSION)
    if profile == "nested-boolean-getter":
        return nested_boolean_getter.verify(path, stage, VERSION)
    if profile == "unused-reference-nested-store":
        return nested_flag_setter.verify(path, stage, VERSION)
    if profile == "float-array":
        return float_array.verify(path, stage, VERSION)
    if profile == "word-array":
        return word_array.verify(path, stage, VERSION)
    if profile == "reference-array":
        return reference_array.verify(path, stage, VERSION)
    if profile == "boolean-getter":
        return boolean_getter.verify(path, stage, VERSION)
    if profile == "boolean-getter-metadata":
        return boolean_getter_metadata.verify(path, stage, VERSION)
    if profile == "virtual-string-call":
        return virtual_string_call.verify(path, stage, VERSION)
    if profile == "generic-dispatch":
        return generic_dispatch.verify(path, stage, VERSION)
    if profile == "guarded-sink":
        return guarded_sink.verify(path, stage, VERSION)
    if profile == "folded-state-constructor":
        return folded_state_constructor.verify(path, stage, VERSION)
    if profile == "constructor-thunk-chain":
        return constructor_thunk_chain.verify(path, stage, VERSION)
    if profile == "array-call":
        return array_call.verify(path, stage, VERSION)
    if profile == "enum-passthrough":
        return enum_passthrough.verify(path, stage, VERSION)
    if profile == "static-field-getter":
        return static_field_getter.verify(path, stage, VERSION)
    if profile == "static-word-getter":
        return static_word_getter.verify(path, stage, VERSION)
    if profile == "static-scalar-setter":
        return static_scalar_setter.verify(path, stage, VERSION)
    if profile == "instance-reference-property":
        return instance_reference_property.verify(path, stage, VERSION)
    if profile == "instance-reference-setter":
        return instance_reference_setter.verify(path, stage, VERSION)
    if profile == "reference-field":
        return reference_field.verify(path, stage, VERSION)
    if profile == "reference-null":
        return reference_null.verify(path, stage, VERSION)
    if profile == "sequential-null-guards":
        return sequential_null_guards.verify(path, stage, VERSION)
    if profile == "call-result-null-guards":
        return call_result_null_guard.verify(path, stage, VERSION)
    if profile == "reference-store":
        return reference_store.verify(path, stage, VERSION)
    if profile == "external-references":
        return external_references.verify(path, stage, VERSION)
    if profile == "numerics-reference":
        return numerics_reference.verify(path, stage, VERSION)
    if profile == "iterator-factory":
        return iterator_factory.verify(path, stage, VERSION)
    if profile == "iterator-factory-manual":
        return iterator_factory_manual.verify(path, stage, VERSION)
    if profile == "iterator-factory-variant":
        return iterator_factory_variant.verify(path, stage, VERSION)
    if profile == "iterator-factory-direct-ctor":
        return iterator_factory_direct_ctor.verify(path, stage, VERSION)
    if profile == "literal-concat":
        return literal_concat.verify(path, stage, VERSION)
    if profile == "class-cast-lookup":
        return class_cast_lookup.verify(path, stage, VERSION)
    if profile == "runtime-cast-concat":
        return runtime_cast_concat.verify(path, stage, VERSION)
    if profile == "static-literal-concat":
        return static_literal_concat.verify(path, stage, VERSION)
    if profile == "throw-only":
        return throw_only.verify(path, stage, VERSION)
    if profile == "metadata-guard-move":
        return metadata_guard_move.verify(path, stage, VERSION)
    if profile == "metadata-guard-parameter":
        return metadata_guard_parameter.verify(path, stage, VERSION)
    if profile == "metadata-forwarding":
        return metadata_forwarding.verify(path, stage, VERSION)
    if profile == "metadata-accessor":
        return metadata_accessor.verify(path, stage, VERSION)
    if profile == "byte-threshold":
        return byte_threshold.verify(path, stage, VERSION)
    if profile == "dense-switch":
        return dense_switch.verify(path, stage, VERSION)
    if profile == "composed-array":
        return composed_array.verify(path, stage, VERSION)
    if profile == "composed-read":
        return composed_read.verify(path, stage, VERSION)
    if profile == "parameter-array":
        return parameter_array.verify(path, stage, VERSION)
    if profile == "field-guard":
        return field_guard.verify(path, stage, VERSION)
    if profile == "zero-arg-field-call":
        return zero_arg_field_call.verify(path, stage, VERSION)
    if profile == "forwarded-argument":
        return forwarded_argument.verify(path, stage, VERSION)
    if profile == "struct-forward-call":
        return struct_forward_call.verify(path, stage, VERSION)
    if profile == "struct-static-forward-call":
        return struct_static_forward_call.verify(path, stage, VERSION)
    if profile == "scalar-truncation":
        return scalar_truncation.verify(path, stage, VERSION)
    if profile == "loop-calls":
        return loop_calls.verify(path, stage, VERSION)
    if profile == "word-fields":
        return word_fields.verify(path, stage, VERSION)
    if profile == "integer-extensions":
        return integer_extensions.verify(path, stage, VERSION)
    if profile == "byte-fields":
        return byte_fields.verify(path, stage, VERSION)
    if profile == "float-comparisons":
        return float_comparison.verify(path, stage, VERSION)
    if profile == "xmm-spill":
        return xmm_spill.verify(path, stage, VERSION)
    if profile == "xmm-ref-mutation":
        return xmm_ref_mutation.verify(path, stage, VERSION)
    if profile == "components":
        return verify_component_behavior(path, stage)
    if profile == "metadata-literal":
        return verify_metadata_literal_behavior(path, stage)
    if profile == "narrow-comparisons":
        return verify_narrow_behavior(path, stage)
    if profile == "division":
        return verify_division_behavior(path, stage)
    if profile in ("scalar-structs", "scalar-structs-negative"):
        return verify_scalar_struct_behavior(path, stage, profile)
    if profile == "shifts":
        return verify_shift_behavior(path, stage)
    if profile == "integers":
        return verify_integer_behavior(path, stage)
    if profile != "arithmetic":
        raise ValueError("Unknown fixture profile")
    report = json.loads(path.read_text(encoding="utf-8"))
    if report["unityVersion"] != VERSION or report["stage"] != stage:
        raise ValueError("Behavior report has the wrong version or stage")
    expected = []
    for left in VALUES:
        for right in VALUES:
            total = int32(left + right)
            expected.append({"left": left, "right": right, "add": total,
                             "select": int32(right - left if left < right else left + right),
                             "accumulated": total, "stored": total})
    if report["observations"] != expected:
        raise ValueError("Behavior differs from the independent integer oracle")
    if stage == "player" and report["platform"] != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": len(expected), "methods": 3,
            "platform": report["platform"], "scope": "finite integer vectors; not a whole-program equivalence proof"}


def integer_observations():
    # Python integers retain every bit of UInt64 JSON numbers, including values above 2**53.
    for width in (32, 64):
        values = [0, 1, 2**(width - 1) - 1, 2**(width - 1), 2**width - 1]
        for left in values:
            for right in values:
                yield {"width": width, "left": left, "right": right,
                       "less": left < right, "greater": left > right,
                       "lessOrEqual": left <= right, "greaterOrEqual": left >= right}


def verify_integer_behavior(path, stage):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report["unityVersion"] != VERSION or report["stage"] != stage or report.get("profile") != "integers":
        raise ValueError("Integer behavior report has the wrong version, stage or profile")
    observations = report["observations"]
    if not isinstance(observations, list) or any(not isinstance(item, dict) for item in observations):
        raise ValueError("Integer behavior observations must be a list of objects")
    # Avoid Python's permissive 1 == True and float/integer equality accepting a malformed report.
    for observation in observations:
        if any(type(observation.get(key)) is not int for key in ("width", "left", "right")):
            raise ValueError("Integer observation operands must be exact JSON integers")
        if any(type(observation.get(key)) is not bool for key in ("less", "greater", "lessOrEqual", "greaterOrEqual")):
            raise ValueError("Integer comparison observations must be JSON booleans")
    expected = list(integer_observations())
    if observations != expected:
        raise ValueError("Behavior differs from the independent unsigned integer oracle")
    if stage == "player" and report["platform"] != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": len(expected), "predicateChecks": len(expected) * 4,
            "methods": 8, "platform": report["platform"], "profile": "integers",
            "scope": "finite UInt32/UInt64 comparison vectors; not a whole-program equivalence proof"}


def division_observations():
    for width in (32, 64):
        minimum, maximum = -(2**(width - 1)), 2**(width - 1) - 1
        for signed in (True, False):
            values = ([minimum, minimum + 1, -17, -2, -1, 0, 1, 2, 17, maximum] if signed
                      else [0, 1, 2, maximum, maximum + 1, 2**width - 2, 2**width - 1])
            for left in values:
                for right in values:
                    if right == 0:
                        quotient, remainder = 0, 0
                    elif signed and left == minimum and right == -1:
                        quotient, remainder = minimum, 0
                    else:
                        # CLR integer division truncates toward zero. Never use a float:
                        # UInt64 boundary operands cannot all be represented exactly by one.
                        quotient = abs(left) // abs(right)
                        if (left < 0) != (right < 0):
                            quotient = -quotient
                        remainder = left - quotient * right
                    yield {"width": width, "signed": signed, "left": left, "right": right,
                           "quotient": quotient, "remainder": remainder}


def verify_division_behavior(path, stage):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report["unityVersion"] != VERSION or report["stage"] != stage or report.get("profile") != "division":
        raise ValueError("Division report has the wrong version, stage or profile")
    observations = report["observations"]
    if not isinstance(observations, list) or any(not isinstance(item, dict) for item in observations):
        raise ValueError("Division observations must be a list of objects")
    for observation in observations:
        if (any(type(observation.get(key)) is not int for key in ("width", "left", "right", "quotient", "remainder"))
                or type(observation.get("signed")) is not bool):
            raise ValueError("Division observations require exact JSON integers and a signedness boolean")
    expected = list(division_observations())
    if observations != expected:
        raise ValueError("Behavior differs from the independent division oracle")
    if stage == "player" and report["platform"] != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": len(expected), "resultChecks": len(expected) * 2,
            "methods": 8, "platform": report["platform"], "profile": "division",
            "scope": "finite guarded integer division vectors; general division exceptions are not covered"}


def shift_observations():
    for width in (32, 64):
        values = [0, 1, 2**(width - 1) - 1, 2**(width - 1), 2**width - 1]
        for value in values:
            signed = value if value < 2**(width - 1) else value - 2**width
            for count in (-65, -64, -33, -32, -1, 0, 1, 31, 32, 33, 63, 64, 65):
                masked = count & (width - 1)
                yield {"width": width, "value": value, "count": count,
                       "arithmetic": signed >> masked, "logical": value >> masked}


def verify_shift_behavior(path, stage):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report["unityVersion"] != VERSION or report["stage"] != stage or report.get("profile") != "shifts":
        raise ValueError("Shift report has the wrong version, stage or profile")
    observations = report["observations"]
    if not isinstance(observations, list) or any(not isinstance(item, dict) for item in observations):
        raise ValueError("Shift observations must be a list of objects")
    for observation in observations:
        if any(type(observation.get(key)) is not int for key in ("width", "value", "count", "arithmetic", "logical")):
            raise ValueError("Shift observations require exact JSON integers")
    expected = list(shift_observations())
    if observations != expected:
        raise ValueError("Behavior differs from the independent shift oracle")
    if stage == "player" and report["platform"] != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": len(expected), "resultChecks": len(expected) * 2,
            "methods": 4, "platform": report["platform"], "profile": "shifts",
            "scope": "finite Int32/UInt32/Int64/UInt64 shift vectors; not a whole-program equivalence proof"}


def scalar_struct_observations(profile):
    if profile == "scalar-structs":
        for item in integer_observations():
            width, left, right = item["width"], item["left"], item["right"]
            yield {"width": width, "left": left, "right": right, "equal": left == right,
                   "sum": (left + right) % 2**width}
    else:
        for case, count in (("padded", 2), ("multiple", 2), ("reference", 3)):
            for left in range(count):
                for right in range(count):
                    yield {"case": case, "left": left, "right": right, "equal": left == right}


def verify_scalar_struct_behavior(path, stage, profile):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report["unityVersion"] != VERSION or report["stage"] != stage or report.get("profile") != profile:
        raise ValueError("Scalar struct report has the wrong version, stage or profile")
    observations = report["observations"]
    if not isinstance(observations, list) or any(not isinstance(item, dict) for item in observations):
        raise ValueError("Scalar struct observations must be a list of objects")
    integer_keys = ("width", "left", "right", "sum") if profile == "scalar-structs" else ("left", "right")
    for observation in observations:
        if any(type(observation.get(key)) is not int for key in integer_keys) or type(observation.get("equal")) is not bool:
            raise ValueError("Scalar struct observations require exact JSON integers and booleans")
    expected = list(scalar_struct_observations(profile))
    if observations != expected:
        raise ValueError("Behavior differs from the independent scalar struct oracle")
    if stage == "player" and report["platform"] != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": len(expected), "methods": PROFILES[profile]["methods"],
            "platform": report["platform"], "profile": profile,
            "scope": "finite struct value vectors; not a whole-program equivalence proof"}


def narrow_observations():
    for condition in (False, True):
        for initial in (False, True):
            yield {"case": "boolean", "condition": condition, "initial": initial,
                   "conditionAfter": condition, "observed": initial or condition}
    for value in (0, 1, 127, 128, 255):
        yield {"case": "byte", "value": value, "zero": value == 0, "highBit": value >= 128}
    for value in (-128, -1, 0, 1, 127):
        yield {"case": "signed", "value": value, "negative": value < 0}
    for value in (0, 1, 255, 256, 65535):
        yield {"case": "word", "value": value, "zero": value == 0}
    for repeat in range(2):
        yield {"case": "metadata", "repeat": repeat, "literal": "neutral metadata literal", "typeMatches": True}
    yield {"case": "initialization", "initialCompleted": 0, "initialThrowing": 0,
           "first": 17, "second": 17, "completedCount": 1, "throwingCount": 1,
           "failures": ["TypeInitializationException/InvalidOperationException"] * 2}


def verify_narrow_behavior(path, stage):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report["unityVersion"] != VERSION or report["stage"] != stage or report.get("profile") != "narrow-comparisons":
        raise ValueError("Narrow-comparison report has the wrong version, stage or profile")
    expected = list(narrow_observations())
    # Canonical JSON keeps Boolean/number types distinct and preserves exact integer spelling.
    if json.dumps(report["observations"], sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Behavior differs from the independent narrow-comparison and initialization oracle")
    if stage == "player" and report["platform"] != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": len(expected), "methods": 14,
            "platform": report["platform"], "profile": "narrow-comparisons",
            "scope": "finite field, metadata and initialization controls; not a whole-program equivalence proof"}


def verify_metadata_literal_behavior(path, stage):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report["unityVersion"] != VERSION or report["stage"] != stage or report.get("profile") != "metadata-literal":
        raise ValueError("Metadata-literal report has the wrong version, stage or profile")
    expected = [{"repeat": repeat, "literal": "neutral metadata literal", "sameInstance": True} for repeat in range(2)]
    if json.dumps(report["observations"], sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Metadata literal observations differ from the independent oracle")
    if stage == "player" and report["platform"] != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": 2, "methods": 1,
            "platform": report["platform"], "profile": "metadata-literal",
            "scope": "one repeated literal-return method; not a whole-program equivalence proof"}


def component_observations():
    fields = [("MarkerBehaviour", "Count", "System.Int32", 0, 17),
              ("MarkerBehaviour", "caption", "System.String", None, "neutral caption"),
              ("MarkerBehaviour", "Configuration", "ComponentFixture.DataAsset", None,
               {"class": "ComponentFixture.DataAsset", "assembly": "ComponentFixture", "sameAsset": True}),
              ("MarkerBehaviour", "Payload.Value", "System.Int32", 0, -41),
              ("DataAsset", "Value", "System.Int32", 0, 73),
              ("DataAsset", "label", "System.String", None, "neutral label")]
    for repeat in range(2):
        for phase in ("fresh", "assigned"):
            for owner, path, managed_type, fresh, assigned in fields:
                yield {"repeat": repeat, "phase": phase, "class": "ComponentFixture." + owner,
                       "assembly": "ComponentFixture", "path": path, "managedType": managed_type,
                       "value": fresh if phase == "fresh" else assigned}


def verify_component_behavior(path, stage):
    report = json.loads(path.read_text(encoding="utf-8"))
    if report.get("unityVersion") != VERSION or report.get("stage") != stage or report.get("profile") != "components":
        raise ValueError("Component report has the wrong version, stage or profile")
    expected = list(component_observations())
    if json.dumps(report.get("observations"), sort_keys=True) != json.dumps(expected, sort_keys=True):
        raise ValueError("Component field observations differ from the independent initialization and assignment oracle")
    helpers = [{"input": value, "output": value} for value in (-(2**31), -1, 0, 1, 2**31 - 1)]
    if json.dumps(report.get("helpers"), sort_keys=True) != json.dumps(helpers, sort_keys=True):
        raise ValueError("Component helper observations differ from the independent identity oracle")
    if stage == "player" and report.get("platform") != "WindowsPlayer":
        raise ValueError("The behavioral run was not a Windows player")
    return {"status": "passed", "observations": len(expected), "helperObservations": len(helpers), "methods": 3,
            "platform": report["platform"], "profile": "components",
            "scope": "fresh runtime instances and reflected fields; no serialized asset, GUID or scene restoration"}


def copy_sources(source, destination):
    if not source.is_dir():
        raise ValueError("Source directory does not exist")
    destination.mkdir(parents=True)
    copied = []
    for item in sorted(source.rglob("*")):
        if item.is_symlink():
            raise ValueError("Fixture source may not contain symbolic links")
        if item.is_file() and (item.suffix in {".cs", ".asmdef", ".asmref", ".rsp"} or item.name == "link.xml"):
            relative = item.relative_to(source)
            target = destination / relative
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copyfile(item, target)
            copied.append({"path": relative.as_posix(), "sha256": hashlib.sha256(item.read_bytes()).hexdigest()})
    if not any(item["path"].endswith(".cs") for item in copied):
        raise ValueError("No C# source found")
    return copied


def profile_harness_directory(profile):
    source = PROFILES[profile]["source"]
    if not source.name.endswith("Fixture"):
        raise ValueError("Fixture source directory does not identify its harness")
    return source.parent / (source.name[:-len("Fixture")] + "Harness")


def copy_harness(profile, destination):
    if profile == "arithmetic":
        return copy_sources(VALIDATION / "Harness", destination)
    copied = copy_sources(profile_harness_directory(profile), destination)
    for item in copy_sources(VALIDATION / "Harness" / "Editor", destination / "Editor"):
        copied.append({**item, "path": "Editor/" + item["path"]})
    serializer = VALIDATION / "Harness" / "Runtime" / "ReportJson.cs"
    shutil.copyfile(serializer, destination / "Runtime" / "ReportJson.cs")
    copied.append({"path": "Runtime/ReportJson.cs", "sha256": hashlib.sha256(serializer.read_bytes()).hexdigest()})
    return sorted(copied, key=lambda item: item["path"])


def run_process(command, environment, log, timeout, cwd=None):
    started = time.monotonic()
    timed_out = False
    with log.open("wb") as output:
        process = subprocess.Popen(command, env=environment, stdout=output, stderr=subprocess.STDOUT,
                                   start_new_session=True, cwd=cwd)
        try:
            code = process.wait(timeout=timeout)
        except subprocess.TimeoutExpired:
            timed_out = True
            if os.name == "nt":
                subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"],
                               stdout=output, stderr=subprocess.STDOUT, check=False)
            else:
                os.killpg(process.pid, signal.SIGTERM)
            try:
                process.wait(timeout=10)
            except subprocess.TimeoutExpired:
                if os.name == "nt":
                    process.kill()
                else:
                    os.killpg(process.pid, signal.SIGKILL)
                process.wait()
            code = process.returncode
    return {"command": command, "exitCode": code, "timedOut": timed_out,
            "seconds": round(time.monotonic() - started, 3)}


def isolate_player(player, destination):
    def excluded(_directory, names):
        return [name for name in names if "BackUpThisFolder" in name or "BurstDebugInformation" in name
                or Path(name).suffix.lower() in {".pdb", ".mdb", ".cs", ".cpp", ".h", ".map"}]
    shutil.copytree(player, destination, ignore=excluded)
    required = ["RecoveryFixture.exe", "GameAssembly.dll", "UnityPlayer.dll",
                "RecoveryFixture_Data/il2cpp_data/Metadata/global-metadata.dat"]
    for relative in required:
        if not (destination / relative).is_file():
            raise ValueError("Fresh player is missing required file: " + relative)
    manifest = []
    for item in sorted(destination.rglob("*")):
        if item.is_file():
            manifest.append({"path": item.relative_to(destination).as_posix(), "bytes": item.stat().st_size,
                             "sha256": hashlib.sha256(item.read_bytes()).hexdigest()})
    return manifest


def resolved_package_lock_sha256(project):
    """Hash Unity's resolved package graph without exposing package details."""
    lock = project / "Packages" / "packages-lock.json"
    if not lock.is_file():
        raise ValueError("Explicit package manifest run did not produce a Unity package lock file")
    data = lock.read_bytes()
    try:
        contents = json.loads(data.decode("utf-8"))
    except (UnicodeError, ValueError) as error:
        raise ValueError("Unity package lock file is not valid UTF-8 JSON") from error
    if not isinstance(contents, dict) or not isinstance(contents.get("dependencies"), dict):
        raise ValueError("Unity package lock file has no resolved dependency graph")
    return hashlib.sha256(data).hexdigest()


def embedded_package_lock_sha256(project, package_name):
    """Require a synthetic fixture package to resolve as an embedded dependency."""
    lock = project / "Packages" / "packages-lock.json"
    if not lock.is_file():
        raise ValueError("Embedded fixture package did not produce a Unity package lock")
    try:
        dependencies = json.loads(lock.read_text(encoding="utf-8"))["dependencies"]
        entry = dependencies[package_name]
    except (UnicodeError, ValueError, KeyError, TypeError) as error:
        raise ValueError("Embedded fixture package is missing from Unity's resolved lock") from error
    if not isinstance(entry, dict) or entry.get("source") != "embedded" or \
            entry.get("version") != "file:" + package_name:
        raise ValueError("Unity did not resolve the fixture package as an embedded dependency")
    return resolved_package_lock_sha256(project)


def embedded_reference_lock_sha256(project):
    return embedded_package_lock_sha256(project, "com.example.recovery-reference")


def install_embedded_fixture_dependency(project, receipt, profile):
    """Install and record one explicit synthetic fixture package."""
    config = EMBEDDED_FIXTURE_PACKAGES[profile]
    source = config["source"] / config["name"]
    package = project / "Packages" / config["name"]
    copied = []
    for relative in config["files"]:
        original = source / relative
        target = package / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(original, target)
        copied.append({"path": relative, "sha256": hashlib.sha256(original.read_bytes()).hexdigest()})
    receipt["auxiliaryDependencies"] = {
        "provenance": "synthetic-explicit-package", "embeddedPackageFiles": copied,
    }


def verify_embedded_fixture_dependency(project, dependency, profile):
    """Check package sources, compiled assembly and Unity package resolution."""
    config = EMBEDDED_FIXTURE_PACKAGES[profile]
    source = config["source"] / config["name"]
    package = project / "Packages" / config["name"]
    expected = set(config["files"])
    if not isinstance(dependency, dict) or dependency.get("provenance") != "synthetic-explicit-package":
        raise ValueError("Synthetic fixture package provenance is missing")
    files = dependency.get("embeddedPackageFiles")
    if not isinstance(files, list) or len(files) != len(expected):
        raise ValueError("Synthetic fixture package source inventory differs")
    recorded = {}
    for item in files:
        if not isinstance(item, dict) or item.get("path") not in expected or \
                item["path"] in recorded or not isinstance(item.get("sha256"), str):
            raise ValueError("Synthetic fixture package source inventory differs")
        relative, source_hash = item["path"], item["sha256"]
        for root in (source, package):
            path = root / relative
            if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != source_hash:
                raise ValueError("Synthetic fixture package source changed since its receipt")
        recorded[relative] = source_hash
    if set(recorded) != expected:
        raise ValueError("Synthetic fixture package source inventory differs")
    assembly = project / "Library/ScriptAssemblies" / config["assembly"]
    if not assembly.is_file() or hashlib.sha256(assembly.read_bytes()).hexdigest() != \
            dependency.get("compiledAssemblySha256"):
        raise ValueError("Synthetic fixture package assembly differs from its receipt")
    lock_hash = embedded_package_lock_sha256(project, config["name"])
    if lock_hash != dependency.get("embeddedPackageLockSha256"):
        raise ValueError("Synthetic fixture package resolution differs from its receipt")
    return {"embeddedPackageFiles": recorded, "embeddedPackageLockSha256": lock_hash}


def install_external_reference_fixture(project, run_dir, receipt, timeout):
    """Install the fixture's synthetic embedded package and managed plug-in."""
    source = VALIDATION / "ExternalReferenceDependencies"
    package_name = "com.example.recovery-reference"
    package_files = ["package.json", "Runtime/Neutral.Package.asmdef", "Runtime/PackageNode.cs"]
    package = project / "Packages" / package_name
    package.mkdir()
    copied = []
    for relative in package_files:
        original = source / package_name / relative
        target = package / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(original, target)
        copied.append({"path": relative, "sha256": hashlib.sha256(target.read_bytes()).hexdigest()})

    plugin_source = run_dir / "dependency-plugin-source"
    plugin_source.mkdir()
    plugin_files = []
    for relative in ("Neutral.Plugin.csproj", "PluginNode.cs"):
        original = source / "Plugin" / relative
        target = plugin_source / relative
        shutil.copyfile(original, target)
        plugin_files.append({"path": relative, "sha256": hashlib.sha256(target.read_bytes()).hexdigest()})
    sdk_version = subprocess.check_output(["dotnet", "--version"], cwd=ROOT, text=True, timeout=30).strip()
    if not sdk_version:
        raise ValueError("Synthetic managed plug-in SDK version is unavailable")

    plugin_output = run_dir / "dependency-plugin"
    plugin_intermediate = run_dir / "dependency-plugin-obj"
    command = ["dotnet", "build", str(plugin_source / "Neutral.Plugin.csproj"),
               "-c", "Release", "--nologo", "-v", "quiet", "-o", str(plugin_output),
               "-p:BaseIntermediateOutputPath=" + str(plugin_intermediate) + os.sep,
               "-p:MSBuildProjectExtensionsPath=" + str(plugin_intermediate) + os.sep]
    result = run_process(command, os.environ.copy(), run_dir / "dependency-plugin.log", timeout, cwd=ROOT)
    receipt["commands"].append(result)
    binary = plugin_output / "Neutral.Plugin.dll"
    if result["timedOut"] or result["exitCode"] != 0 or not binary.is_file():
        raise ValueError("Synthetic managed plug-in build did not complete")
    destination = project / "Assets" / "Plugins"
    destination.mkdir(parents=True)
    shutil.copyfile(binary, destination / "Neutral.Plugin.dll")
    receipt["externalDependencies"] = {
        "provenance": "synthetic-explicit-fixture",
        "embeddedPackageFiles": copied,
        "pluginSourceFiles": plugin_files,
        "pluginSdkVersion": sdk_version,
        "precompiledPluginSha256": hashlib.sha256(binary.read_bytes()).hexdigest(),
    }


def verify_external_reference_fixture(project, run_dir, dependency):
    """Verify one run's source inputs, SDK, lock and compiled dependency binaries."""
    source = VALIDATION / "ExternalReferenceDependencies"
    if not isinstance(dependency, dict) or dependency.get("provenance") != "synthetic-explicit-fixture":
        raise ValueError("Synthetic external dependency provenance is missing")

    def checked_sources(key, expected, copied_root, source_root):
        items = dependency.get(key)
        if not isinstance(items, list) or len(items) != len(expected):
            raise ValueError("Synthetic external dependency source inventory differs")
        hashes = {}
        for item in items:
            if not isinstance(item, dict):
                raise ValueError("Synthetic external dependency source inventory differs")
            relative, expected_hash = item.get("path"), item.get("sha256")
            if not isinstance(relative, str) or relative not in expected or relative in hashes or \
                    not isinstance(expected_hash, str):
                raise ValueError("Synthetic external dependency source inventory differs")
            for root in (copied_root, source_root):
                path = root / relative
                if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != expected_hash:
                    raise ValueError("Synthetic external dependency source changed since its receipt")
            hashes[relative] = expected_hash
        if set(hashes) != set(expected):
            raise ValueError("Synthetic external dependency source inventory differs")
        return hashes

    package_hashes = checked_sources(
        "embeddedPackageFiles", {"package.json", "Runtime/Neutral.Package.asmdef", "Runtime/PackageNode.cs"},
        project / "Packages/com.example.recovery-reference", source / "com.example.recovery-reference")
    plugin_hashes = checked_sources(
        "pluginSourceFiles", {"Neutral.Plugin.csproj", "PluginNode.cs"},
        run_dir / "dependency-plugin-source", source / "Plugin")
    sdk_version = subprocess.check_output(["dotnet", "--version"], cwd=ROOT, text=True, timeout=30).strip()
    if not sdk_version or sdk_version != dependency.get("pluginSdkVersion"):
        raise ValueError("Synthetic managed plug-in SDK differs from its build receipt")

    binaries = (
        (run_dir / "dependency-plugin/Neutral.Plugin.dll", "precompiledPluginSha256"),
        (project / "Assets/Plugins/Neutral.Plugin.dll", "compiledPluginSha256"),
        (project / "Library/ScriptAssemblies/Neutral.Package.dll", "compiledPackageSha256"),
    )
    for path, key in binaries:
        if not path.is_file() or hashlib.sha256(path.read_bytes()).hexdigest() != dependency.get(key):
            raise ValueError("Synthetic external dependency binary changed since its build receipt")
    if dependency["precompiledPluginSha256"] != dependency["compiledPluginSha256"]:
        raise ValueError("Unity plug-in differs from the compiled synthetic dependency")
    lock_hash = embedded_reference_lock_sha256(project)
    if lock_hash != dependency.get("embeddedPackageLockSha256"):
        raise ValueError("Embedded package resolution changed since its build receipt")
    return {"embeddedPackageFiles": package_hashes, "pluginSourceFiles": plugin_hashes,
            "pluginSdkVersion": sdk_version, "embeddedPackageLockSha256": lock_hash}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--editor", type=Path, required=True, help="Supplied Unity editor executable")
    parser.add_argument("--wine", help="Wine executable, required for a Windows editor on a non-Windows host")
    parser.add_argument("--toolchain-root", type=Path,
                        help="Optional VS2019 toolchain root containing VC/Tools/MSVC and Windows Kits/10")
    parser.add_argument("--profile", choices=PROFILES, default="arithmetic",
                        help="Independent selected-assembly fixture contract")
    parser.add_argument("--source-dir", type=Path,
                        help="Replacement source must expose the selected profile's API and assembly")
    parser.add_argument("--package-manifest", type=Path,
                        help="Explicit Unity Packages/manifest.json to copy into the fresh project")
    parser.add_argument("--run-dir", type=Path, required=True, help="New directory under this repository's ignored Files/")
    parser.add_argument("--stage", choices=["compile", "build", "run"], default="compile",
                        help="run builds and executes; build also compiles; every invocation uses a fresh project")
    parser.add_argument("--code-generation", choices=("OptimizeSpeed", "OptimizeSize"), default="OptimizeSpeed",
                        help="IL2CPP code generation setting for a native build")
    parser.add_argument("--timeout", type=int, default=600, help="Per-process deadline in seconds")
    args = parser.parse_args()
    profile = PROFILES[args.profile]
    if args.source_dir is None:
        args.source_dir = profile["source"]
    if args.timeout <= 0:
        parser.error("--timeout must be positive")
    package_manifest = args.package_manifest.expanduser().resolve() if args.package_manifest else None
    if package_manifest is not None and not package_manifest.is_file():
        parser.error("--package-manifest must name an existing file")
    editor = args.editor.expanduser().resolve()
    if not editor.is_file():
        parser.error("--editor must name an existing executable")
    run_dir = args.run_dir.expanduser().resolve()
    private_root = (ROOT / "Files").resolve()
    if run_dir == private_root or private_root not in run_dir.parents:
        parser.error("--run-dir must be a new child directory of the repository's Files/")
    if subprocess.run(["git", "check-ignore", "--quiet", str(run_dir)], cwd=ROOT).returncode != 0:
        parser.error("--run-dir must be gitignored")
    if run_dir.exists():
        parser.error("--run-dir already exists; select a fresh directory")
    if editor.suffix.lower() == ".exe" and os.name != "nt" and not args.wine:
        parser.error("Windows editor requires --wine on this host")
    if args.stage == "run" and os.name != "nt" and not args.wine:
        parser.error("Running a Windows player requires --wine on this host")
    environment = os.environ.copy()
    # Do not accidentally inherit a previous run's discovery override.
    environment.pop("CPP2IL_VALIDATION_TOOLCHAIN", None)
    environment["CPP2IL_VALIDATION_CODE_GENERATION"] = args.code_generation
    if args.wine:
        prefix = Path.home() / ".wine_unity"
        if not prefix.is_dir():
            parser.error("The required existing Wine prefix is unavailable")
        environment["WINEPREFIX"] = str(prefix)
        environment["WINEDEBUG"] = "-all"

    def target_path(path):
        path = path.resolve()
        if not args.wine:
            return str(path)
        result = subprocess.run([args.wine, "winepath", "-w", str(path)], env=environment,
                                capture_output=True, text=True, timeout=30, check=True)
        return result.stdout.strip()

    run_dir.mkdir(parents=True)
    receipt = {"unityVersionRequired": VERSION, "requestedStage": args.stage,
               "requestedCodeGeneration": args.code_generation,
               "profile": args.profile, "assembly": profile["assembly"],
               "sourceKind": "synthetic-baseline" if args.source_dir.resolve() == profile["source"].resolve() else "replacement-source",
               "status": "running", "stages": {
                   "unityCompilation": {"status": "unverified"},
                   "editorBehavior": {"status": "unverified"},
                   "nativeBuild": {"status": "unverified" if args.stage != "compile" else "not-requested"},
                   "playerBehavior": {"status": "unverified" if args.stage == "run" else "not-requested"}
               }, "commands": [],
               "commit": subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=ROOT, text=True).strip()}
    receipt_path = run_dir / "receipt.json"
    write_json(receipt_path, receipt)
    try:
        temporary = run_dir / "environment" / "tmp"
        dotnet_home = run_dir / "environment" / "dotnet-home"
        temporary.mkdir(parents=True)
        dotnet_home.mkdir()
        # Each editor and its tools receive their own scratch/cache locations.
        # Leave the parent environment and the existing Wine prefix unchanged.
        environment.update(LC_ALL="C", LANG="C", DOTNET_MULTILEVEL_LOOKUP="0",
                           DOTNET_ROLL_FORWARD="Disable", TMPDIR=str(temporary),
                           TEMP=target_path(temporary), TMP=target_path(temporary),
                           DOTNET_CLI_HOME=target_path(dotnet_home))
        receipt["launchEnvironment"] = {key: environment[key] for key in (
            "LC_ALL", "LANG", "DOTNET_MULTILEVEL_LOOKUP", "DOTNET_ROLL_FORWARD",
            "TMPDIR", "TEMP", "TMP", "DOTNET_CLI_HOME")}
        if args.toolchain_root:
            toolchain = args.toolchain_root.expanduser().resolve()
            if not (toolchain / "VC" / "Tools" / "MSVC").is_dir() or not (toolchain / "Windows Kits" / "10").is_dir():
                raise ValueError("The toolchain root must contain VC/Tools/MSVC and Windows Kits/10")
            environment["CPP2IL_VALIDATION_TOOLCHAIN"] = target_path(toolchain)
            environment["VS160COMNTOOLS"] = target_path(toolchain / "Common7" / "Tools")
            receipt["toolchainRoot"] = str(toolchain)
            receipt["toolchainInventory"] = {
                "msvcDirectories": sorted(item.name for item in (toolchain / "VC" / "Tools" / "MSVC").iterdir() if item.is_dir()),
                "sdkIncludeDirectories": sorted(item.name for item in (toolchain / "Windows Kits" / "10" / "Include").iterdir() if item.is_dir())
            }
        project = run_dir / "project"
        (project / "ProjectSettings").mkdir(parents=True)
        (project / "Packages").mkdir()
        (project / "Reports").mkdir()
        (project / "ProjectSettings" / "ProjectVersion.txt").write_text("m_EditorVersion: " + VERSION + "\n", encoding="utf-8")
        project_manifest = project / "Packages" / "manifest.json"
        if package_manifest is None:
            write_json(project_manifest, {"dependencies": {}})
        else:
            shutil.copyfile(package_manifest, project_manifest)
            receipt["packageManifest"] = {
                "provenance": "explicit-auxiliary",
                "sha256": hashlib.sha256(project_manifest.read_bytes()).hexdigest(),
            }
        if args.profile == "external-references":
            install_external_reference_fixture(project, run_dir, receipt, args.timeout)
        if args.profile in EMBEDDED_FIXTURE_PACKAGES:
            install_embedded_fixture_dependency(project, receipt, args.profile)
        receipt["sourceFiles"] = copy_sources(args.source_dir.resolve(), project / "Assets" / profile["assembly"])
        receipt["harnessFiles"] = copy_harness(args.profile, project / "Assets" / "Validation")
        prefix_command = [args.wine, str(editor)] if args.wine else [str(editor)]
        common = prefix_command + ["-batchmode", "-nographics", "-quit", "-projectPath", target_path(project)]
        if args.stage != "compile":
            common += ["-buildTarget", "Win64"]

        def editor_stage(method, label):
            command = common + ["-executeMethod", method, "-logFile", target_path(run_dir / (label + "-editor.log"))]
            outcome = run_process(command, environment, run_dir / (label + "-process.log"), args.timeout)
            receipt["commands"].append(outcome)
            write_json(receipt_path, receipt)
            return outcome

        def behavior_stage(path, label, stage):
            try:
                receipt["stages"][label] = verify_behavior(path, stage, args.profile)
            except ValueError as error:
                receipt["stages"][label] = {"status": "failed", "reason": str(error)}
                raise

        label = "compile" if args.stage == "compile" else "build"
        method = "RecoveryValidation.ValidationEntry." + ("Compile" if args.stage == "compile" else "Build")
        outcome = editor_stage(method, label)
        marker = project / "Reports" / "compilation-complete.txt"
        if marker.read_text(encoding="utf-8").strip() != VERSION:
            raise ValueError("Fresh compilation completion marker is missing or incorrect")
        receipt["stages"]["unityCompilation"] = {"status": "passed", "version": VERSION}
        behavior_stage(project / "Reports" / "editor-behavior.json", "editorBehavior", "editor")
        if outcome["timedOut"] or outcome["exitCode"] != 0:
            raise RuntimeError(label + " editor process failed")
        if args.stage != "compile":
            build = json.loads((project / "Reports" / "build.json").read_text(encoding="utf-8"))
            expected = {"unityVersion": VERSION, "target": "StandaloneWindows64", "backend": "IL2CPP",
                        "compilerConfiguration": "Release", "codeGeneration": args.code_generation,
                        "development": False, "result": "Succeeded", "errors": 0}
            if any(build.get(key) != value for key, value in expected.items()):
                raise ValueError("Native build receipt does not establish the required profile")
            receipt["stages"]["nativeBuild"] = {"status": "passed", **build}
            receipt["playerInputs"] = isolate_player(run_dir / "player", run_dir / "player-input")
            if args.stage == "run":
                player = run_dir / "player-input" / "RecoveryFixture.exe"
                report_path = run_dir / "player-behavior.json"
                command = ([args.wine, str(player)] if args.wine else [str(player)]) + [
                    "-batchmode", "-nographics", "-logFile", target_path(run_dir / "player.log"),
                    "--validation-report", target_path(report_path)]
                outcome = run_process(command, environment, run_dir / "player-process.log", args.timeout)
                receipt["commands"].append(outcome)
                if outcome["timedOut"] or outcome["exitCode"] != 0:
                    raise RuntimeError("Native player process failed")
                behavior_stage(report_path, "playerBehavior", "player")
        if package_manifest is not None:
            receipt["packageManifest"]["resolvedLockSha256"] = resolved_package_lock_sha256(project)
        if args.profile == "external-references":
            dependencies = receipt["externalDependencies"]
            dependencies["compiledPackageSha256"] = hashlib.sha256((
                project / "Library/ScriptAssemblies/Neutral.Package.dll").read_bytes()).hexdigest()
            dependencies["compiledPluginSha256"] = hashlib.sha256((
                project / "Assets/Plugins/Neutral.Plugin.dll").read_bytes()).hexdigest()
            dependencies["embeddedPackageLockSha256"] = embedded_reference_lock_sha256(project)
            verify_external_reference_fixture(project, run_dir, dependencies)
        if args.profile in EMBEDDED_FIXTURE_PACKAGES:
            dependencies = receipt["auxiliaryDependencies"]
            config = EMBEDDED_FIXTURE_PACKAGES[args.profile]
            dependencies["compiledAssemblySha256"] = hashlib.sha256((
                project / "Library/ScriptAssemblies" / config["assembly"]).read_bytes()).hexdigest()
            dependencies["embeddedPackageLockSha256"] = embedded_package_lock_sha256(
                project, config["name"])
            verify_embedded_fixture_dependency(project, dependencies, args.profile)
        receipt["status"] = "passed"
    except (OSError, ValueError, RuntimeError, subprocess.SubprocessError, KeyError) as error:
        receipt["status"] = "failed"
        receipt["error"] = str(error)
    finally:
        write_json(receipt_path, receipt)
    print(json.dumps({"status": receipt["status"], "stages": receipt["stages"]}, indent=2))
    return 0 if receipt["status"] == "passed" else 1


if __name__ == "__main__":
    sys.exit(main())
