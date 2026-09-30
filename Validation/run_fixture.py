#!/usr/bin/env python3
"""Compile/build a synthetic or recovered fixture in an isolated exact-version project."""

import argparse
from contextlib import contextmanager
import alias_ambiguity
import array_access
import array_read_increment
import array_sequence
import array_call
import boolean_parameter_branch
from behavior_oracle import read_report
import enum_passthrough
import external_references
import numerics_reference
import static_field_getter
import catch_divide
import class_cast_lookup
import guarded_boxed_cast
import reference_field
import native_int_field
import reference_array
import reference_array_search
import range_array_read
import exception_regions
import field_array
import enum_field_array
import field_boolean_array
import field_boolean_array_read
import fixed_boolean_conjunction
import constructed_base_boolean_array
import float_forward_store
import nested_single_getter
import fixed_reference_array
import folded_reference_array
import folded_boolean_array_store
import boolean_array_fill_loop
import base_effect_boolean_tail
import scalar_positive_zero_leaf
import owner_indexed_enum_array
import nested_array_call
import array_element_scalar_field
import inherited_reference_array_read
import fixed_scalar_array
import array_element_store
import field_guard
import inherited_field_guard
import hashlib
import json
import os
from pathlib import Path, PureWindowsPath
import shutil
import signal
import subprocess
import sys
import time

import byte_fields
import byte_mask_parameter
import byte_mask_one
import boolean_getter
import boolean_composition
import conditional_boolean_store
import conditional_generic_boolean_store
import conditional_generic_terminal_store
import instance_parameter_reference_read
import boolean_literal_store
import parameter_boolean_array_store
import scalar_field_comparison
import integer_literal_store
import aggregate_scalar_compare
import ancestor_cctor_interface_getter
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
import register_zero_extension
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
import narrow_test_arithmetic
import nested_boolean_store
import nested_boolean_getter
import nested_flag_setter
import reference_null
import reference_field_null
import wide_reference_null
import field_parameter_boolean_array_store
import reference_store
import object_reference_store
import string_reference_store
import runtime_cast_concat
import static_literal_concat
import conditional_managed_throw
import throw_only
import scalar_truncation
import scalar_zero_return
import arithmetic_zero_flag
import sequential_null_guards
import call_result_null_guard
import static_word_getter
import static_scalar_setter
import xmm_spill
import xmm_ref_mutation
import word_array
import word_fields
import zero_arg_field_call
import reference_tail_call
import enum_return_tail
import explicit_class_cast
import parameter_class_test
import boolean_parameter_class_test
import sealed_parameter_class_test
import guarded_array_length
import narrow_scalar_getter
import final_interface_boolean_getter
import integer_truncation
import small_aggregate_getter
import wide_field_low32
import byref_integer_halves
import guarded_scalar_accessor
import ordered_generic_tail_field
import enum_integer_conversion
import closed_generic_storage
import closed_generic_guarded_call
import dynamic_virtual_dispatch
import open_generic_early_field
import open_generic_prefix_operations
import nested_literal_store
import nested_byte_field_read
import array_element_argument_tail
import final_override_boolean_getter
import false_boolean_virtual_tail
import call_result_string_tail
import cctor_boolean_getter
import side_effect_class_cctor
import field_plus_one_reference_array
import call_before_capture_boolean_store
import nullable_delegate_field_tail
import boolean_tail_field_call
import call_result_boolean_store
import call_result_tail_guard
import call_result_false_tail
import call_result_boolean_tail
import conditional_call_result_tail
import lookup_guard_managed_throw
import guarded_array_operations
import array_call_origins
import guarded_array_tail_invocation
import scalar_float_selection
import scalar_float_conversion
import scalar_int32_single_conversion
import scalar_word_wrapper_conversion
import scalar_double_accumulator
import scalar_float_conversion_composition
import native_null_checked_invocation
import native_scalar_pair_invocation
import native_scalar_field_invocation
import native_scalar_producer_invocation
import native_reference_producer_invocation
import native_reference_field_invocation
import signed_field_comparison
import typed_field_address
import native_scalar_invocation_effects
import native_subnormal_field_store
import native_derived_receiver_invocation
import native_boolean_toggle_invocation
import native_boolean_predicate_invocation
import call_result_engine_false_tail
import engine_component_false_tail
import internal_call_field
import dual_result_tail_guard
import triple_literal_guard
import ordered_call_tail_guard
import ordered_noarg_tail_guard
import unsealed_zero_store
import virtual_string_call
import virtual_tail_dispatch
import generic_dispatch
import guarded_sink
import folded_state_constructor
import folded_literal_constructor
import shared_inert_constructor
import empty_object_constructor
import layered_virtual_tail_dispatch
import float_initializer_constructor
import scalar_wrapper
import scalar_wrapper_cctor
import shared_abi
import constructor_thunk_chain


VERSION = "2021.3.35f1"
ROOT = Path(__file__).resolve().parent.parent
VALIDATION = ROOT / "Validation"
VALUES = [-(2**31), -(2**31) + 1, -17, -1, 0, 1, 17, 2**31 - 2, 2**31 - 1]
PROFILES = {
    "catch-divide": {"assembly": "ExceptionRegionFixture", "source": VALIDATION / "CatchDivideFixture", "methods": 1},
    "exception-regions": {"assembly": "ExceptionRegionFixture", "source": VALIDATION / "ExceptionRegionFixture", "methods": 2},
    "array-access": {"assembly": "ArrayAccessFixture", "source": VALIDATION / "ArrayAccessFixture", "methods": 8},
    "array-read-increment": {"assembly": "ArrayReadIncrementFixture",
                             "source": VALIDATION / "ArrayReadIncrementFixture", "methods": 1},
    "array-sequence": {"assembly": "ArraySequenceFixture", "source": VALIDATION / "ArraySequenceFixture", "methods": 2},
    "enum-field-array": {"assembly": "EnumFieldArrayFixture", "source": VALIDATION / "EnumFieldArrayFixture", "methods": 8},
    "field-array": {"assembly": "FieldArrayFixture", "source": VALIDATION / "FieldArrayFixture", "methods": 4},
    "field-boolean-array": {"assembly": "BooleanFieldArrayFixture", "source": VALIDATION / "BooleanFieldArrayFixture", "methods": 3},
    "field-parameter-boolean-array-store": {
        "assembly": "FieldParameterBooleanArrayStoreFixture",
        "source": VALIDATION / "FieldParameterBooleanArrayStoreFixture", "methods": 2},
    "field-boolean-array-read": {"assembly": "FieldBooleanArrayReadFixture", "source": VALIDATION / "FieldBooleanArrayReadFixture", "methods": 4},
    "fixed-boolean-conjunction": {"assembly": "FixedBooleanConjunctionFixture",
                                  "source": VALIDATION / "FixedBooleanConjunctionFixture", "methods": 4},
    "range-array-read": {"assembly": "RangeArrayReadFixture", "source": VALIDATION / "RangeArrayReadFixture", "methods": 4},
    "constructed-base-boolean-array": {"assembly": "ConstructedBaseBooleanArrayFixture", "source": VALIDATION / "ConstructedBaseBooleanArrayFixture", "methods": 6},
    "folded-boolean-array-store": {"assembly": "FoldedBooleanArrayStoreFixture",
                                   "source": VALIDATION / "FoldedBooleanArrayStoreFixture", "methods": 5},
    "boolean-array-fill-loop": {"assembly": "BooleanArrayFillLoopFixture",
                                "source": VALIDATION / "BooleanArrayFillLoopFixture", "methods": 3},
    "base-effect-boolean-tail": {"assembly": "BaseEffectBooleanTailFixture",
                                "source": VALIDATION / "BaseEffectBooleanTailFixture", "methods": 13},
    "call-result-boolean-tail": {"assembly": "CallResultBooleanTailFixture",
                                "source": VALIDATION / "CallResultBooleanTailFixture", "methods": 10},
    "conditional-call-result-tail": {"assembly": "ConditionalCallResultTailFixture",
                                     "source": VALIDATION / "ConditionalCallResultTailFixture", "methods": 12},
    "lookup-guard-managed-throw": {"assembly": "LookupGuardManagedThrowFixture",
                                  "source": VALIDATION / "LookupGuardManagedThrowFixture", "methods": 4},
    "guarded-array-operations": {"assembly": "GuardedArrayOperationsFixture",
                                 "source": VALIDATION / "GuardedArrayOperationsFixture", "methods": 9},
    "array-call-origins": {"assembly": "ArrayCallOriginsFixture",
                           "source": VALIDATION / "ArrayCallOriginsFixture", "methods": 9},
    "guarded-array-tail-invocation": {"assembly": "GuardedArrayTailInvocationFixture",
                                      "source": VALIDATION / "GuardedArrayTailInvocationFixture", "methods": 10},
    "scalar-float-selection": {"assembly": "ScalarFloatSelectionFixture",
                               "source": VALIDATION / "ScalarFloatSelectionFixture", "methods": 9},
    "scalar-double-accumulator": {"assembly": "ScalarDoubleAccumulatorFixture",
                                  "source": VALIDATION / "ScalarDoubleAccumulatorFixture", "methods": 2},
    "native-boolean-predicate-invocation": {"assembly": "NativeBooleanPredicateInvocationFixture",
                                           "source": VALIDATION / "NativeBooleanPredicateInvocationFixture", "methods": 6},
    "native-boolean-toggle-invocation": {"assembly": "NativeBooleanToggleInvocationFixture",
                                          "source": VALIDATION / "NativeBooleanToggleInvocationFixture", "methods": 4},
    "scalar-float-conversion": {"assembly": "ScalarFloatConversionFixture",
                                "source": VALIDATION / "ScalarFloatConversionFixture", "methods": 2},
    "scalar-int32-single-conversion": {"assembly": "ScalarInt32SingleConversionFixture",
                                      "source": VALIDATION / "ScalarInt32SingleConversionFixture", "methods": 7},
    "scalar-word-wrapper-conversion": {"assembly": "ScalarWordWrapperConversionFixture",
                                        "source": VALIDATION / "ScalarWordWrapperConversionFixture", "methods": 3},
    "scalar-float-conversion-composition": {"assembly": "ScalarFloatConversionCompositionFixture",
                                            "source": VALIDATION / "ScalarFloatConversionCompositionFixture", "methods": 8},
    "native-null-checked-invocation": {"assembly": "NativeNullCheckedInvocationFixture",
                                      "source": VALIDATION / "NativeNullCheckedInvocationFixture", "methods": 18},
    "native-scalar-pair-invocation": {"assembly": "NativeScalarPairInvocationFixture",
                                     "source": VALIDATION / "NativeScalarPairInvocationFixture", "methods": 15},
    "native-scalar-field-invocation": {"assembly": "NativeScalarFieldInvocationFixture",
                                      "source": VALIDATION / "NativeScalarFieldInvocationFixture", "methods": 12},
    "native-scalar-producer-invocation": {"assembly": "NativeScalarProducerInvocationFixture",
                                        "source": VALIDATION / "NativeScalarProducerInvocationFixture", "methods": 7},
    "native-reference-producer-invocation": {"assembly": "NativeReferenceProducerInvocationFixture",
                                           "source": VALIDATION / "NativeReferenceProducerInvocationFixture", "methods": 8},
    "native-reference-field-invocation": {"assembly": "NativeReferenceFieldInvocationFixture",
                                        "source": VALIDATION / "NativeReferenceFieldInvocationFixture", "methods": 5},
    "signed-field-comparison": {"assembly": "SignedFieldComparisonFixture",
                                "source": VALIDATION / "SignedFieldComparisonFixture", "methods": 6},
    "typed-field-address": {"assembly": "TypedFieldAddressFixture",
                            "source": VALIDATION / "TypedFieldAddressFixture", "methods": 12},
    "reference-array-search": {"assembly": "ReferenceArraySearchFixture",
                               "source": VALIDATION / "ReferenceArraySearchFixture", "methods": 8},
    "native-scalar-invocation-effects": {"assembly": "NativeScalarInvocationEffectsFixture",
                                       "source": VALIDATION / "NativeScalarInvocationEffectsFixture", "methods": 6},
    "native-subnormal-field-store": {"assembly": "NativeSubnormalFieldStoreFixture",
                                    "source": VALIDATION / "NativeSubnormalFieldStoreFixture", "methods": 11},
    "native-derived-receiver-invocation": {"assembly": "NativeDerivedReceiverInvocationFixture",
                                          "source": VALIDATION / "NativeDerivedReceiverInvocationFixture", "methods": 7},
    "scalar-positive-zero-leaf": {"assembly": "ScalarPositiveZeroLeafFixture",
                                  "source": VALIDATION / "ScalarPositiveZeroLeafFixture", "methods": 5},
    "narrow-array": {"assembly": "NarrowArrayFixture", "source": VALIDATION / "NarrowArrayFixture", "methods": 4},
    "nested-boolean-store": {"assembly": "NestedBooleanStoreFixture", "source": VALIDATION / "NestedBooleanStoreFixture", "methods": 6},
    "nested-boolean-getter": {"assembly": "NestedBooleanGetterFixture", "source": VALIDATION / "NestedBooleanGetterFixture", "methods": 4},
    "float-forward-store": {"assembly": "FloatForwardStoreFixture", "source": VALIDATION / "FloatForwardStoreFixture", "methods": 5},
    "nested-single-getter": {"assembly": "NestedSingleGetterFixture", "source": VALIDATION / "NestedSingleGetterFixture", "methods": 3},
    "fixed-reference-array": {"assembly": "FixedReferenceArrayFixture", "source": VALIDATION / "FixedReferenceArrayFixture", "methods": 5},
    "folded-reference-array": {"assembly": "FoldedReferenceArrayFixture", "source": VALIDATION / "FoldedReferenceArrayFixture", "methods": 7},
    "owner-indexed-enum-array": {"assembly": "OwnerIndexedEnumArrayFixture",
                                  "source": VALIDATION / "OwnerIndexedEnumArrayFixture", "methods": 4},
    "nested-array-call": {"assembly": "NestedArrayCallFixture",
                          "source": VALIDATION / "NestedArrayCallFixture", "methods": 14},
    "array-element-scalar-field": {"assembly": "ArrayElementScalarFieldFixture",
                                   "source": VALIDATION / "ArrayElementScalarFieldFixture", "methods": 5},
    "inherited-reference-array-read": {"assembly": "InheritedReferenceArrayReadFixture",
                                       "source": VALIDATION / "InheritedReferenceArrayReadFixture", "methods": 6},
    "fixed-scalar-array": {"assembly": "FixedScalarArrayFixture", "source": VALIDATION / "FixedScalarArrayFixture", "methods": 3},
    "shared-abi": {"assembly": "SharedAbiFixture", "source": VALIDATION / "SharedAbiFixture", "methods": 15},
    "array-element-store": {"assembly": "ArrayElementStoreFixture", "source": VALIDATION / "ArrayElementStoreFixture", "methods": 4},
    "unused-reference-nested-store": {"assembly": "NestedFlagSetterFixture", "source": VALIDATION / "NestedFlagSetterFixture", "methods": 4},
    "float-array": {"assembly": "FloatArrayFixture", "source": VALIDATION / "FloatArrayFixture", "methods": 2},
    "word-array": {"assembly": "WordArrayFixture", "source": VALIDATION / "WordArrayFixture", "methods": 2},
    "reference-array": {"assembly": "ReferenceArrayFixture", "source": VALIDATION / "ReferenceArrayFixture", "methods": 3},
    "boolean-literal-store": {"assembly": "BooleanLiteralStoreFixture", "source": VALIDATION / "BooleanLiteralStoreFixture", "methods": 6},
    "aggregate-scalar-compare": {"assembly": "AggregateScalarCompareFixture", "source": VALIDATION / "AggregateScalarCompareFixture", "methods": 7},
    "integer-literal-store": {"assembly": "IntegerLiteralStoreFixture", "source": VALIDATION / "IntegerLiteralStoreFixture", "methods": 10},
    "scalar-field-comparison": {"assembly": "ScalarFieldComparisonFixture", "source": VALIDATION / "ScalarFieldComparisonFixture", "methods": 3},
    "parameter-boolean-array-store": {"assembly": "ParameterBooleanArrayStoreFixture", "source": VALIDATION / "ParameterBooleanArrayStoreFixture", "methods": 10},
    "boolean-composition": {"assembly": "BooleanCompositionFixture", "source": VALIDATION / "BooleanCompositionFixture", "methods": 3},
    "conditional-boolean-store": {"assembly": "ConditionalBooleanStoreFixture",
                                  "source": VALIDATION / "ConditionalBooleanStoreFixture", "methods": 3},
    "conditional-generic-boolean-store": {"assembly": "ConditionalGenericBooleanStoreFixture",
                                          "source": VALIDATION / "ConditionalGenericBooleanStoreFixture",
                                          "methods": 4},
    "conditional-generic-terminal-store": {"assembly": "ConditionalGenericTerminalStoreFixture",
                                           "source": VALIDATION / "ConditionalGenericTerminalStoreFixture",
                                           "methods": 6},
    "instance-parameter-reference-read": {"assembly": "InstanceParameterReferenceReadFixture",
                                          "source": VALIDATION / "InstanceParameterReferenceReadFixture",
                                          "methods": 5},
    "boolean-getter": {"assembly": "BooleanGetterFixture", "source": VALIDATION / "BooleanGetterFixture", "methods": 5},
    "boolean-getter-metadata": {"assembly": "BooleanGetterMetadataFixture", "source": VALIDATION / "BooleanGetterMetadataFixture", "methods": 11},
    "virtual-string-call": {"assembly": "VirtualStringCallFixture", "source": VALIDATION / "VirtualStringCallFixture", "methods": 5},
    "generic-dispatch": {"assembly": "GenericDispatchFixture", "source": VALIDATION / "GenericDispatchFixture", "methods": 4,
                         "noManagedBody": (("GenericDispatchFixture.IRead`1", "Read"),)},
    "final-interface-boolean-getter": {
        "assembly": "FinalInterfaceBooleanGetterFixture",
        "source": VALIDATION / "FinalInterfaceBooleanGetterFixture", "methods": 11,
        "noManagedBody": (("FinalInterfaceBooleanGetterFixture.IFlag", "get_Value"),
                          ("FinalInterfaceBooleanGetterFixture.IFlag", "Read")),
    },
    "ancestor-cctor-interface-getter": {
        "assembly": "AncestorCctorInterfaceGetterFixture",
        "source": VALIDATION / "AncestorCctorInterfaceGetterFixture", "methods": 3,
        "noManagedBody": (("AncestorCctorInterfaceGetterFixture.IFlagReader", "Read"),),
    },
    "integer-truncation": {"assembly": "IntegerTruncationFixture",
                           "source": VALIDATION / "IntegerTruncationFixture", "methods": 7},
    "small-aggregate-getter": {"assembly": "SmallAggregateGetterFixture",
                               "source": VALIDATION / "SmallAggregateGetterFixture", "methods": 8},
    "wide-field-low32": {"assembly": "WideFieldLow32Fixture",
                         "source": VALIDATION / "WideFieldLow32Fixture", "methods": 6},
    "byref-integer-halves": {"assembly": "ByRefIntegerHalvesFixture",
                             "source": VALIDATION / "ByRefIntegerHalvesFixture", "methods": 7},
    "guarded-scalar-accessor": {"assembly": "GuardedScalarAccessorFixture",
                                "source": VALIDATION / "GuardedScalarAccessorFixture", "methods": 7},
    "ordered-generic-tail-field": {"assembly": "OrderedGenericTailFieldFixture",
                                  "source": VALIDATION / "OrderedGenericTailFieldFixture", "methods": 2},
    "enum-integer-conversion": {"assembly": "EnumIntegerConversionFixture",
                                "source": VALIDATION / "EnumIntegerConversionFixture", "methods": 8},
    "closed-generic-storage": {"assembly": "ClosedGenericStorageFixture",
                               "source": VALIDATION / "ClosedGenericStorageFixture", "methods": 6},
    "closed-generic-guarded-call": {"assembly": "ClosedGenericGuardedCallFixture",
                                    "source": VALIDATION / "ClosedGenericGuardedCallFixture", "methods": 4},
    "dynamic-virtual-dispatch": {"assembly": "DynamicVirtualDispatchFixture",
                                "source": VALIDATION / "DynamicVirtualDispatchFixture", "methods": 10},
    "open-generic-early-field": {"assembly": "OpenGenericEarlyFieldFixture",
                                 "source": VALIDATION / "OpenGenericEarlyFieldFixture", "methods": 4},
    "open-generic-prefix-operations": {"assembly": "OpenGenericPrefixOperationsFixture",
                                       "source": VALIDATION / "OpenGenericPrefixOperationsFixture", "methods": 2},
    "guarded-sink": {"assembly": "GuardedSinkFixture", "source": VALIDATION / "GuardedSinkFixture", "methods": 3},
    "folded-state-constructor": {"assembly": "FoldedStateConstructorFixture", "source": VALIDATION / "FoldedStateConstructorFixture", "methods": 2},
    "folded-literal-constructor": {"assembly": "FoldedLiteralConstructorFixture", "source": VALIDATION / "FoldedLiteralConstructorFixture", "methods": 3},
    "shared-inert-constructor": {"assembly": "SharedInertConstructorFixture", "source": VALIDATION / "SharedInertConstructorFixture", "methods": 2},
    "empty-object-constructor": {"assembly": "EmptyObjectConstructorFixture", "source": VALIDATION / "EmptyObjectConstructorFixture", "methods": 2},
    "float-initializer-constructor": {"assembly": "FloatInitializerConstructorFixture",
                                      "source": VALIDATION / "FloatInitializerConstructorFixture", "methods": 1},
    "scalar-wrapper": {"assembly": "ScalarWrapperFixture",
                        "source": VALIDATION / "ScalarWrapperFixture", "methods": 7},
    "scalar-wrapper-cctor": {"assembly": "ScalarWrapperCctorFixture",
                              "source": VALIDATION / "ScalarWrapperCctorFixture", "methods": 7},
    "constructor-thunk-chain": {"assembly": "ConstructorThunkChainFixture", "source": VALIDATION / "ConstructorThunkChainFixture", "methods": 7},
    "array-call": {"assembly": "ArrayCallFixture", "source": VALIDATION / "ArrayCallFixture", "methods": 10},
    "enum-passthrough": {"assembly": "EnumPassthroughFixture", "source": VALIDATION / "EnumPassthroughFixture", "methods": 4},
    "static-field-getter": {"assembly": "StaticFieldGetterFixture", "source": VALIDATION / "StaticFieldGetterFixture", "methods": 5},
    "static-word-getter": {"assembly": "StaticWordGetterFixture", "source": VALIDATION / "StaticWordGetterFixture", "methods": 2},
    "static-scalar-setter": {"assembly": "StaticScalarSetterFixture", "source": VALIDATION / "StaticScalarSetterFixture", "methods": 1},
    "instance-reference-property": {"assembly": "InstanceReferencePropertyFixture", "source": VALIDATION / "InstanceReferencePropertyFixture", "methods": 5},
    "instance-reference-setter": {"assembly": "InstanceReferenceSetterFixture", "source": VALIDATION / "InstanceReferenceSetterFixture", "methods": 5,
                                  "noManagedBody": {("InstanceReferenceSetterFixture.IIndexedCell", "set_Item")}},
    "reference-field": {"assembly": "ReferenceFieldFixture", "source": VALIDATION / "ReferenceFieldFixture", "methods": 25},
    "native-int-field": {"assembly": "NativeIntFieldFixture", "source": VALIDATION / "NativeIntFieldFixture", "methods": 3},
    "reference-null": {"assembly": "ReferenceNullFixture", "source": VALIDATION / "ReferenceNullFixture", "methods": 3},
    "reference-field-null": {"assembly": "ReferenceFieldNullFixture", "source": VALIDATION / "ReferenceFieldNullFixture", "methods": 12},
    "wide-reference-null": {"assembly": "WideReferenceNullFixture", "source": VALIDATION / "WideReferenceNullFixture", "methods": 4},
    "sequential-null-guards": {"assembly": "SequentialNullGuardFixture", "source": VALIDATION / "SequentialNullGuardFixture", "methods": 3},
    "call-result-null-guards": {"assembly": "CallResultNullGuardFixture", "source": VALIDATION / "CallResultNullGuardFixture", "methods": 13},
    "reference-store": {"assembly": "ReferenceStoreFixture", "source": VALIDATION / "ReferenceStoreFixture", "methods": 2},
    "object-reference-store": {"assembly": "ObjectReferenceStoreFixture",
                               "source": VALIDATION / "ObjectReferenceStoreFixture", "methods": 2},
    "string-reference-store": {"assembly": "StringReferenceStoreFixture",
                               "source": VALIDATION / "StringReferenceStoreFixture", "methods": 2},
    "external-references": {"assembly": "ExternalReferenceFixture", "source": VALIDATION / "ExternalReferenceFixture", "methods": 1},
    "numerics-reference": {"assembly": "NumericsReferenceFixture", "source": VALIDATION / "NumericsReferenceFixture", "methods": 1},
    "iterator-factory": {"assembly": "IteratorFactoryFixture", "source": VALIDATION / "IteratorFactoryFixture", "methods": 8},
    "iterator-factory-manual": {"assembly": "IteratorFactoryManualFixture", "source": VALIDATION / "IteratorFactoryManualFixture", "methods": 7},
    "iterator-factory-variant": {"assembly": "IteratorFactoryVariantFixture", "source": VALIDATION / "IteratorFactoryVariantFixture", "methods": 8},
    "iterator-factory-direct-ctor": {"assembly": "IteratorFactoryDirectCtorFixture", "source": VALIDATION / "IteratorFactoryDirectCtorFixture", "methods": 8},
    "literal-concat": {"assembly": "LiteralConcatFixture", "source": VALIDATION / "LiteralConcatFixture", "methods": 7},
    "class-cast-lookup": {"assembly": "ClassCastLookupFixture", "source": VALIDATION / "ClassCastLookupFixture", "methods": 5},
    "guarded-boxed-cast": {"assembly": "GuardedBoxedCastFixture",
                           "source": VALIDATION / "GuardedBoxedCastFixture", "methods": 1},
    "runtime-cast-concat": {"assembly": "RuntimeCastConcatFixture", "source": VALIDATION / "RuntimeCastConcatFixture", "methods": 21,
                            "noManagedBody": {("RuntimeCastConcatFixture.INodeOwner", "get_Current"),
                                              ("RuntimeCastConcatFixture.INodeOwner", "set_Current")}},
    "static-literal-concat": {"assembly": "StaticLiteralConcatFixture", "source": VALIDATION / "StaticLiteralConcatFixture", "methods": 1},
    "throw-only": {"assembly": "ThrowOnlyFixture", "source": VALIDATION / "ThrowOnlyFixture", "methods": 6},
    "conditional-managed-throw": {"assembly": "ConditionalManagedThrowFixture",
                                  "source": VALIDATION / "ConditionalManagedThrowFixture", "methods": 1},
    "virtual-tail-dispatch": {"assembly": "VirtualTailDispatchFixture",
                              "source": VALIDATION / "VirtualTailDispatchFixture", "methods": 5},
    "layered-virtual-tail-dispatch": {
        "assembly": "LayeredVirtualTailDispatchFixture",
        "source": VALIDATION / "LayeredVirtualTailDispatchFixture", "methods": 8,
        "noManagedBody": (("LayeredVirtualTailDispatchFixture.ITag", "ReadTag"),)},
    "metadata-guard-move": {"assembly": "MetadataGuardMoveFixture", "source": VALIDATION / "MetadataGuardMoveFixture", "methods": 3},
    "metadata-guard-parameter": {"assembly": "MetadataGuardParameterFixture", "source": VALIDATION / "MetadataGuardParameterFixture", "methods": 1},
    "metadata-forwarding": {"assembly": "MetadataForwardingFixture", "source": VALIDATION / "MetadataForwardingFixture", "methods": 5},
    "metadata-accessor": {"assembly": "MetadataAccessorFixture", "source": VALIDATION / "MetadataAccessorFixture", "methods": 6},
    "alias-ambiguity": {"assembly": "AliasAmbiguityFixture", "source": VALIDATION / "AliasAmbiguityFixture", "methods": 3},
    "boolean-parameter-branch": {"assembly": "BooleanParameterBranchFixture", "source": VALIDATION / "BooleanParameterBranchFixture", "methods": 3},
    "narrow-test-arithmetic": {"assembly": "NarrowTestArithmeticFixture", "source": VALIDATION / "NarrowTestArithmeticFixture", "methods": 1},
    "byte-mask-parameter": {"assembly": "ByteMaskParameterFixture", "source": VALIDATION / "ByteMaskParameterFixture", "methods": 6},
    "byte-mask-one": {"assembly": "ByteMaskOneFixture", "source": VALIDATION / "ByteMaskOneFixture", "methods": 1},
    "byte-threshold": {"assembly": "ByteThresholdFixture", "source": VALIDATION / "ByteThresholdFixture", "methods": 2},
    "dense-switch": {"assembly": "DenseSwitchFixture", "source": VALIDATION / "DenseSwitchFixture", "methods": 2},
    "composed-array": {"assembly": "ComposedArrayFixture", "source": VALIDATION / "ComposedArrayFixture", "methods": 6},
    "composed-read": {"assembly": "ComposedReadFixture", "source": VALIDATION / "ComposedReadFixture", "methods": 4},
    "parameter-array": {"assembly": "ParameterArrayFixture", "source": VALIDATION / "ParameterArrayFixture", "methods": 3},
    "field-guard": {"assembly": "FieldGuardFixture", "source": VALIDATION / "FieldGuardFixture", "methods": 19},
    "inherited-field-guard": {"assembly": "InheritedFieldGuardFixture",
                              "source": VALIDATION / "InheritedFieldGuardFixture", "methods": 3},
    "zero-arg-field-call": {"assembly": "ZeroArgFieldCallFixture", "source": VALIDATION / "ZeroArgFieldCallFixture", "methods": 8},
    "reference-tail-call": {"assembly": "ReferenceTailCallFixture", "source": VALIDATION / "ReferenceTailCallFixture", "methods": 2},
    "enum-return-tail": {"assembly": "EnumReturnTailFixture", "source": VALIDATION / "EnumReturnTailFixture", "methods": 4},
    "explicit-class-cast": {"assembly": "ExplicitClassCastFixture", "source": VALIDATION / "ExplicitClassCastFixture", "methods": 1},
    "boolean-parameter-class-test": {"assembly": "BooleanParameterClassTestFixture", "source": VALIDATION / "BooleanParameterClassTestFixture", "methods": 1},
    "sealed-parameter-class-test": {"assembly": "SealedParameterClassTestFixture", "source": VALIDATION / "SealedParameterClassTestFixture", "methods": 2},
    "guarded-array-length": {"assembly": "GuardedArrayLengthFixture", "source": VALIDATION / "GuardedArrayLengthFixture", "methods": 6},
    "narrow-scalar-getter": {"assembly": "NarrowScalarGetterFixture", "source": VALIDATION / "NarrowScalarGetterFixture", "methods": 8},
    "nested-literal-store": {"assembly": "NestedLiteralStoreFixture", "source": VALIDATION / "NestedLiteralStoreFixture", "methods": 9},
    "call-before-capture-boolean-store": {
        "assembly": "CallBeforeCaptureBooleanStoreFixture",
        "source": VALIDATION / "CallBeforeCaptureBooleanStoreFixture", "methods": 5},
    "nullable-delegate-field-tail": {
        "assembly": "NullableDelegateFieldTailFixture",
        "source": VALIDATION / "NullableDelegateFieldTailFixture", "methods": 4},
    "nested-byte-field-read": {
        "assembly": "NestedByteFieldReadFixture",
        "source": VALIDATION / "NestedByteFieldReadFixture", "methods": 3},
    "array-element-argument-tail": {
        "assembly": "ArrayElementArgumentTailFixture",
        "source": VALIDATION / "ArrayElementArgumentTailFixture", "methods": 5},
    "final-override-boolean-getter": {
        "assembly": "FinalOverrideBooleanGetterFixture",
        "source": VALIDATION / "FinalOverrideBooleanGetterFixture", "methods": 6,
        "noManagedBody": (("FinalOverrideBooleanGetterFixture.FlagBase", "get_Value"),)},
    "false-boolean-virtual-tail": {
        "assembly": "FalseBooleanVirtualTailFixture",
        "source": VALIDATION / "FalseBooleanVirtualTailFixture", "methods": 3},
    "call-result-string-tail": {
        "assembly": "CallResultStringTailFixture",
        "source": VALIDATION / "CallResultStringTailFixture", "methods": 5},
    "cctor-boolean-getter": {
        "assembly": "CctorBooleanGetterFixture",
        "source": VALIDATION / "CctorBooleanGetterFixture", "methods": 3},
    "side-effect-class-cctor": {
        "assembly": "SideEffectClassCctorFixture",
        "source": VALIDATION / "SideEffectClassCctorFixture", "methods": 1},
    "field-plus-one-reference-array": {
        "assembly": "FieldPlusOneReferenceArrayFixture",
        "source": VALIDATION / "FieldPlusOneReferenceArrayFixture", "methods": 5},
    "parameter-class-test": {"assembly": "ParameterClassTestFixture", "source": VALIDATION / "ParameterClassTestFixture", "methods": 1},
    "boolean-tail-field-call": {"assembly": "BooleanTailFieldCallFixture",
                                "source": VALIDATION / "BooleanTailFieldCallFixture", "methods": 6},
    "call-result-boolean-store": {"assembly": "CallResultBooleanStoreFixture",
                                  "source": VALIDATION / "CallResultBooleanStoreFixture", "methods": 6},
    "call-result-tail-guard": {"assembly": "CallResultTailGuardFixture",
                               "source": VALIDATION / "CallResultTailGuardFixture", "methods": 5},
    "call-result-false-tail": {"assembly": "CallResultFalseTailFixture",
                               "source": VALIDATION / "CallResultFalseTailFixture", "methods": 7},
    "call-result-engine-false-tail": {
        "assembly": "CallResultEngineFalseTailFixture",
        "source": VALIDATION / "CallResultEngineFalseTailFixture", "methods": 2},
    "engine-component-false-tail": {
        "assembly": "EngineComponentFalseTailFixture",
        "source": VALIDATION / "EngineComponentFalseTailFixture", "methods": 1},
    "internal-call-field": {
        "assembly": "InternalCallFieldFixture",
        "source": VALIDATION / "InternalCallFieldFixture", "methods": 3},
    "dual-result-tail-guard": {"assembly": "DualResultTailGuardFixture",
                               "source": VALIDATION / "DualResultTailGuardFixture", "methods": 12},
    "triple-literal-guard": {"assembly": "TripleLiteralGuardFixture",
                             "source": VALIDATION / "TripleLiteralGuardFixture", "methods": 6},
    "ordered-call-tail-guard": {"assembly": "OrderedCallTailGuardFixture",
                                 "source": VALIDATION / "OrderedCallTailGuardFixture", "methods": 9,
                                 "noManagedBody": {("OrderedCallTailGuardFixture.GuardBase", "Forward")}},
    "ordered-noarg-tail-guard": {"assembly": "OrderedNoArgTailGuardFixture",
                                   "source": VALIDATION / "OrderedNoArgTailGuardFixture", "methods": 9,
                                   "noManagedBody": {("OrderedNoArgTailGuardFixture.GuardBase", "Forward")}},
    "unsealed-zero-store": {"assembly": "UnsealedZeroStoreFixture",
                            "source": VALIDATION / "UnsealedZeroStoreFixture", "methods": 3},
    "forwarded-argument": {"assembly": "ForwardedArgumentFixture", "source": VALIDATION / "ForwardedArgumentFixture", "methods": 4},
    "struct-forward-call": {"assembly": "StructForwardCallFixture", "source": VALIDATION / "StructForwardCallFixture", "methods": 5},
    "struct-static-forward-call": {"assembly": "StructStaticForwardCallFixture", "source": VALIDATION / "StructStaticForwardCallFixture", "methods": 6},
    "scalar-truncation": {"assembly": "ScalarTruncationFixture", "source": VALIDATION / "ScalarTruncationFixture", "methods": 2},
    "scalar-zero-return": {"assembly": "ScalarZeroReturnFixture", "source": VALIDATION / "ScalarZeroReturnFixture", "methods": 2},
    "arithmetic-zero-flag": {"assembly": "ArithmeticZeroFlagFixture",
                             "source": VALIDATION / "ArithmeticZeroFlagFixture", "methods": 4},
    "loop-calls": {"assembly": "LoopCallFixture", "source": VALIDATION / "LoopCallFixture", "methods": 4},
    "word-fields": {"assembly": "WordFieldFixture", "source": VALIDATION / "WordFieldFixture", "methods": 4},
    "integer-extensions": {"assembly": "IntegerExtensionFixture", "source": VALIDATION / "IntegerExtensionFixture", "methods": 12},
    "register-zero-extension": {"assembly": "RegisterZeroExtensionFixture", "source": VALIDATION / "RegisterZeroExtensionFixture", "methods": 4},
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
    "lookup-guard-managed-throw": {
        "name": "com.example.lookup-guard",
        "source": VALIDATION / "LookupGuardManagedThrowDependencies",
        "files": ("package.json", "Runtime/Neutral.LookupGuard.asmdef", "Runtime/LookupService.cs"),
        "assembly": "Neutral.LookupGuard.dll",
    },
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


def owned_snapshot_file(directory, path):
    """Require an ordinary file without links at any level inside the snapshot."""
    try:
        relative = path.relative_to(directory)
    except ValueError:
        return False
    if directory.is_symlink() or ".." in relative.parts:
        return False
    current = directory
    for part in relative.parts:
        current = current / part
        if current.is_symlink():
            return False
    return path.is_file()


def verified_behavior_report(directory, path, stage, profile):
    if not owned_snapshot_file(directory, path):
        raise ValueError("Behavior report is missing or linked")
    before = hashlib.sha256(path.read_bytes()).hexdigest()
    gate = verify_behavior(path, stage, profile)
    if hashlib.sha256(path.read_bytes()).hexdigest() != before:
        raise ValueError("Behavior report changed during independent verification")
    return gate, {"path": path.relative_to(directory).as_posix(), "sha256": before}


def checked_behavior_report_files(directory, inventory, expected_paths):
    """Authenticate the exact retained report set before recording success."""
    if (not isinstance(inventory, list) or len(inventory) != len(expected_paths) or
            any(not isinstance(item, dict) or not isinstance(item.get("path"), str) or
                not isinstance(item.get("sha256"), str) for item in inventory)):
        raise ValueError("Behavior report inventory is missing or malformed")
    if {item["path"] for item in inventory} != set(expected_paths):
        raise ValueError("Behavior report inventory is incomplete or duplicated")
    for item in inventory:
        path = directory / item["path"]
        if (not owned_snapshot_file(directory, path) or
                hashlib.sha256(path.read_bytes()).hexdigest() != item["sha256"]):
            raise ValueError("Behavior report changed since its independently verified gate")


def verify_behavior(path, stage, profile="arithmetic"):
    report = read_report(path)
    if not isinstance(report, dict):
        raise ValueError("Behavior report must contain a JSON object")
    if profile == "alias-ambiguity":
        return alias_ambiguity.verify(path, stage, VERSION)
    if profile == "boolean-parameter-branch":
        return boolean_parameter_branch.verify(path, stage, VERSION)
    if profile == "narrow-test-arithmetic":
        return narrow_test_arithmetic.verify(path, stage, VERSION)
    if profile == "byte-mask-parameter":
        return byte_mask_parameter.verify(path, stage, VERSION)
    if profile == "byte-mask-one":
        return byte_mask_one.verify(path, stage, VERSION)
    if profile == "catch-divide":
        return catch_divide.verify(path, stage, VERSION)
    if profile == "exception-regions":
        return exception_regions.verify(path, stage, VERSION)
    if profile == "array-access":
        return array_access.verify(path, stage, VERSION)
    if profile == "array-read-increment":
        return array_read_increment.verify(path, stage, VERSION)
    if profile == "array-sequence":
        return array_sequence.verify(path, stage, VERSION)
    if profile == "enum-field-array":
        return enum_field_array.verify(path, stage, VERSION)
    if profile == "field-array":
        return field_array.verify(path, stage, VERSION)
    if profile == "field-boolean-array":
        return field_boolean_array.verify(path, stage, VERSION)
    if profile == "field-parameter-boolean-array-store":
        return field_parameter_boolean_array_store.verify(path, stage, VERSION)
    if profile == "constructed-base-boolean-array":
        return constructed_base_boolean_array.verify(path, stage, VERSION)
    if profile == "folded-boolean-array-store":
        return folded_boolean_array_store.verify(path, stage, VERSION)
    if profile == "boolean-array-fill-loop":
        return boolean_array_fill_loop.verify(path, stage, VERSION)
    if profile == "base-effect-boolean-tail":
        return base_effect_boolean_tail.verify(path, stage, VERSION)
    if profile == "call-result-boolean-tail":
        return call_result_boolean_tail.verify(path, stage, VERSION)
    if profile == "conditional-call-result-tail":
        return conditional_call_result_tail.verify(path, stage, VERSION)
    if profile == "lookup-guard-managed-throw":
        return lookup_guard_managed_throw.verify(path, stage, VERSION)
    if profile == "guarded-array-operations":
        return guarded_array_operations.verify(path, stage, VERSION)
    if profile == "array-call-origins":
        return array_call_origins.verify(path, stage, VERSION)
    if profile == "guarded-array-tail-invocation":
        return guarded_array_tail_invocation.verify(path, stage, VERSION)
    if profile == "scalar-float-selection":
        return scalar_float_selection.verify(path, stage, VERSION)
    if profile == "scalar-double-accumulator":
        return scalar_double_accumulator.verify(path, stage, VERSION)
    if profile == "native-boolean-predicate-invocation":
        return native_boolean_predicate_invocation.verify(path, stage, VERSION)
    if profile == "native-boolean-toggle-invocation":
        return native_boolean_toggle_invocation.verify(path, stage, VERSION)
    if profile == "scalar-float-conversion":
        return scalar_float_conversion.verify(path, stage, VERSION)
    if profile == "scalar-int32-single-conversion":
        return scalar_int32_single_conversion.verify(path, stage, VERSION)
    if profile == "scalar-word-wrapper-conversion":
        return scalar_word_wrapper_conversion.verify(path, stage, VERSION)
    if profile == "scalar-float-conversion-composition":
        return scalar_float_conversion_composition.verify(path, stage, VERSION)
    if profile == "native-null-checked-invocation":
        return native_null_checked_invocation.verify(path, stage, VERSION)
    if profile == "native-scalar-pair-invocation":
        return native_scalar_pair_invocation.verify(path, stage, VERSION)
    if profile == "native-scalar-field-invocation":
        return native_scalar_field_invocation.verify(path, stage, VERSION)
    if profile == "native-scalar-producer-invocation":
        return native_scalar_producer_invocation.verify(path, stage, VERSION)
    if profile == "native-reference-producer-invocation":
        return native_reference_producer_invocation.verify(path, stage, VERSION)
    if profile == "native-reference-field-invocation":
        return native_reference_field_invocation.verify(path, stage, VERSION)
    if profile == "signed-field-comparison":
        return signed_field_comparison.verify(path, stage, VERSION)
    if profile == "typed-field-address":
        return typed_field_address.verify(path, stage, VERSION)
    if profile == "reference-array-search":
        return reference_array_search.verify(path, stage, VERSION)
    if profile == "native-scalar-invocation-effects":
        return native_scalar_invocation_effects.verify(path, stage, VERSION)
    if profile == "native-subnormal-field-store":
        return native_subnormal_field_store.verify(path, stage, VERSION)
    if profile == "native-derived-receiver-invocation":
        return native_derived_receiver_invocation.verify(path, stage, VERSION)
    if profile == "scalar-positive-zero-leaf":
        return scalar_positive_zero_leaf.verify(path, stage, VERSION)
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
    if profile == "boolean-literal-store":
        return boolean_literal_store.verify(path, stage, VERSION)
    if profile == "aggregate-scalar-compare":
        return aggregate_scalar_compare.verify(path, stage, VERSION)
    if profile == "integer-literal-store":
        return integer_literal_store.verify(path, stage, VERSION)
    if profile == "scalar-field-comparison":
        return scalar_field_comparison.verify(path, stage, VERSION)
    if profile == "parameter-boolean-array-store":
        return parameter_boolean_array_store.verify(path, stage, VERSION)
    if profile == "boolean-composition":
        return boolean_composition.verify(path, stage, VERSION)
    if profile == "conditional-boolean-store":
        return conditional_boolean_store.verify(path, stage, VERSION)
    if profile == "conditional-generic-boolean-store":
        return conditional_generic_boolean_store.verify(path, stage, VERSION)
    if profile == "conditional-generic-terminal-store":
        return conditional_generic_terminal_store.verify(path, stage, VERSION)
    if profile == "instance-parameter-reference-read":
        return instance_parameter_reference_read.verify(path, stage, VERSION)
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
    if profile == "folded-literal-constructor":
        return folded_literal_constructor.verify(path, stage, VERSION)
    if profile == "shared-inert-constructor":
        return shared_inert_constructor.verify(path, stage, VERSION)
    if profile == "empty-object-constructor":
        return empty_object_constructor.verify(path, stage, VERSION)
    if profile == "float-initializer-constructor":
        return float_initializer_constructor.verify(path, stage, VERSION)
    if profile == "scalar-wrapper":
        return scalar_wrapper.verify(path, stage, VERSION)
    if profile == "scalar-wrapper-cctor":
        return scalar_wrapper_cctor.verify(path, stage, VERSION)
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
    if profile == "shared-abi":
        return shared_abi.verify(path, stage, VERSION)
    if profile == "float-forward-store":
        return float_forward_store.verify(path, stage, VERSION)
    if profile == "nested-single-getter":
        return nested_single_getter.verify(path, stage, VERSION)
    if profile == "fixed-reference-array":
        return fixed_reference_array.verify(path, stage, VERSION)
    if profile == "folded-reference-array":
        return folded_reference_array.verify(path, stage, VERSION)
    if profile == "owner-indexed-enum-array":
        return owner_indexed_enum_array.verify(path, stage, VERSION)
    if profile == "nested-array-call":
        return nested_array_call.verify(path, stage, VERSION)
    if profile == "array-element-scalar-field":
        return array_element_scalar_field.verify(path, stage, VERSION)
    if profile == "inherited-reference-array-read":
        return inherited_reference_array_read.verify(path, stage, VERSION)
    if profile == "field-boolean-array-read":
        return field_boolean_array_read.verify(path, stage, VERSION)
    if profile == "fixed-boolean-conjunction":
        return fixed_boolean_conjunction.verify(path, stage, VERSION)
    if profile == "range-array-read":
        return range_array_read.verify(path, stage, VERSION)
    if profile == "fixed-scalar-array":
        return fixed_scalar_array.verify(path, stage, VERSION)
    if profile == "array-element-store":
        return array_element_store.verify(path, stage, VERSION)
    if profile == "native-int-field":
        return native_int_field.verify(path, stage, VERSION)
    if profile == "reference-null":
        return reference_null.verify(path, stage, VERSION)
    if profile == "reference-field-null":
        return reference_field_null.verify(path, stage, VERSION)
    if profile == "wide-reference-null":
        return wide_reference_null.verify(path, stage, VERSION)
    if profile == "sequential-null-guards":
        return sequential_null_guards.verify(path, stage, VERSION)
    if profile == "call-result-null-guards":
        return call_result_null_guard.verify(path, stage, VERSION)
    if profile == "reference-store":
        return reference_store.verify(path, stage, VERSION)
    if profile == "object-reference-store":
        return object_reference_store.verify(path, stage, VERSION)
    if profile == "string-reference-store":
        return string_reference_store.verify(path, stage, VERSION)
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
    if profile == "guarded-boxed-cast":
        return guarded_boxed_cast.verify(path, stage, VERSION)
    if profile == "runtime-cast-concat":
        return runtime_cast_concat.verify(path, stage, VERSION)
    if profile == "static-literal-concat":
        return static_literal_concat.verify(path, stage, VERSION)
    if profile == "throw-only":
        return throw_only.verify(path, stage, VERSION)
    if profile == "conditional-managed-throw":
        return conditional_managed_throw.verify(path, stage, VERSION)
    if profile == "virtual-tail-dispatch":
        return virtual_tail_dispatch.verify(path, stage, VERSION)
    if profile == "layered-virtual-tail-dispatch":
        return layered_virtual_tail_dispatch.verify(path, stage, VERSION)
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
    if profile == "inherited-field-guard":
        return inherited_field_guard.verify(path, stage, VERSION)
    if profile == "zero-arg-field-call":
        return zero_arg_field_call.verify(path, stage, VERSION)
    if profile == "reference-tail-call":
        return reference_tail_call.verify(path, stage, VERSION)
    if profile == "enum-return-tail":
        return enum_return_tail.verify(path, stage, VERSION)
    if profile == "explicit-class-cast":
        return explicit_class_cast.verify(path, stage, VERSION)
    if profile == "boolean-parameter-class-test":
        return boolean_parameter_class_test.verify(path, stage, VERSION)
    if profile == "sealed-parameter-class-test":
        return sealed_parameter_class_test.verify(path, stage, VERSION)
    if profile == "guarded-array-length":
        return guarded_array_length.verify(path, stage, VERSION)
    if profile == "narrow-scalar-getter":
        return narrow_scalar_getter.verify(path, stage, VERSION)
    if profile == "final-interface-boolean-getter":
        return final_interface_boolean_getter.verify(path, stage, VERSION)
    if profile == "ancestor-cctor-interface-getter":
        return ancestor_cctor_interface_getter.verify(path, stage, VERSION)
    if profile == "integer-truncation":
        return integer_truncation.verify(path, stage, VERSION)
    if profile == "small-aggregate-getter":
        return small_aggregate_getter.verify(path, stage, VERSION)
    if profile == "wide-field-low32":
        return wide_field_low32.verify(path, stage, VERSION)
    if profile == "byref-integer-halves":
        return byref_integer_halves.verify(path, stage, VERSION)
    if profile == "guarded-scalar-accessor":
        return guarded_scalar_accessor.verify(path, stage, VERSION)
    if profile == "enum-integer-conversion":
        return enum_integer_conversion.verify(path, stage, VERSION)
    if profile == "closed-generic-storage":
        return closed_generic_storage.verify(path, stage, VERSION)
    if profile == "closed-generic-guarded-call":
        return closed_generic_guarded_call.verify(path, stage, VERSION)
    if profile == "dynamic-virtual-dispatch":
        return dynamic_virtual_dispatch.verify(path, stage, VERSION)
    if profile == "open-generic-early-field":
        return open_generic_early_field.verify(path, stage, VERSION)
    if profile == "open-generic-prefix-operations":
        return open_generic_prefix_operations.verify(path, stage, VERSION)
    if profile == "ordered-generic-tail-field":
        return ordered_generic_tail_field.verify(path, stage, VERSION)
    if profile == "nested-literal-store":
        return nested_literal_store.verify(path, stage, VERSION)
    if profile == "call-before-capture-boolean-store":
        return call_before_capture_boolean_store.verify(path, stage, VERSION)
    if profile == "nullable-delegate-field-tail":
        return nullable_delegate_field_tail.verify(path, stage, VERSION)
    if profile == "nested-byte-field-read":
        return nested_byte_field_read.verify(path, stage, VERSION)
    if profile == "array-element-argument-tail":
        return array_element_argument_tail.verify(path, stage, VERSION)
    if profile == "final-override-boolean-getter":
        return final_override_boolean_getter.verify(path, stage, VERSION)
    if profile == "false-boolean-virtual-tail":
        return false_boolean_virtual_tail.verify(path, stage, VERSION)
    if profile == "call-result-string-tail":
        return call_result_string_tail.verify(path, stage, VERSION)
    if profile == "cctor-boolean-getter":
        return cctor_boolean_getter.verify(path, stage, VERSION)
    if profile == "side-effect-class-cctor":
        return side_effect_class_cctor.verify(path, stage, VERSION)
    if profile == "field-plus-one-reference-array":
        return field_plus_one_reference_array.verify(path, stage, VERSION)
    if profile == "parameter-class-test":
        return parameter_class_test.verify(path, stage, VERSION)
    if profile == "boolean-tail-field-call":
        return boolean_tail_field_call.verify(path, stage, VERSION)
    if profile == "call-result-boolean-store":
        return call_result_boolean_store.verify(path, stage, VERSION)
    if profile == "call-result-tail-guard":
        return call_result_tail_guard.verify(path, stage, VERSION)
    if profile == "call-result-false-tail":
        return call_result_false_tail.verify(path, stage, VERSION)
    if profile == "call-result-engine-false-tail":
        return call_result_engine_false_tail.verify(path, stage, VERSION)
    if profile == "engine-component-false-tail":
        return engine_component_false_tail.verify(path, stage, VERSION)
    if profile == "internal-call-field":
        return internal_call_field.verify(path, stage, VERSION)
    if profile == "dual-result-tail-guard":
        return dual_result_tail_guard.verify(path, stage, VERSION)
    if profile == "triple-literal-guard":
        return triple_literal_guard.verify(path, stage, VERSION)
    if profile == "ordered-call-tail-guard":
        return ordered_call_tail_guard.verify(path, stage, VERSION)
    if profile == "ordered-noarg-tail-guard":
        return ordered_noarg_tail_guard.verify(path, stage, VERSION)
    if profile == "unsealed-zero-store":
        return unsealed_zero_store.verify(path, stage, VERSION)
    if profile == "forwarded-argument":
        return forwarded_argument.verify(path, stage, VERSION)
    if profile == "struct-forward-call":
        return struct_forward_call.verify(path, stage, VERSION)
    if profile == "struct-static-forward-call":
        return struct_static_forward_call.verify(path, stage, VERSION)
    if profile == "scalar-truncation":
        return scalar_truncation.verify(path, stage, VERSION)
    if profile == "scalar-zero-return":
        return scalar_zero_return.verify(path, stage, VERSION)
    if profile == "arithmetic-zero-flag":
        return arithmetic_zero_flag.verify(path, stage, VERSION)
    if profile == "loop-calls":
        return loop_calls.verify(path, stage, VERSION)
    if profile == "word-fields":
        return word_fields.verify(path, stage, VERSION)
    if profile == "integer-extensions":
        return integer_extensions.verify(path, stage, VERSION)
    if profile == "register-zero-extension":
        return register_zero_extension.verify(path, stage, VERSION)
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


def _portable_file_name(value):
    if (not isinstance(value, str) or not value or value.endswith((".", " ")) or
            any(character in '\\/:*?"<>|' or ord(character) < 32 for character in value)):
        return False
    stem = value.partition(".")[0].upper()
    return stem not in {"CON", "PRN", "AUX", "NUL", *("COM" + str(i) for i in range(1, 10)),
                        *("LPT" + str(i) for i in range(1, 10))}


def source_destination(source, assembly):
    """Retain authenticated exported Assets paths; standalone fixtures keep their layout."""
    source = Path(source).expanduser()
    if ".." in source.parts or not _portable_file_name(assembly):
        raise ValueError("Fixture source path or assembly identity is unsafe")
    source = source.absolute()
    if not source.is_dir():
        raise ValueError("Source directory does not exist")
    for path in (source, *source.parents):
        if path.is_symlink():
            raise ValueError("Fixture source path may not contain symbolic links")
    for root in (source, *source.parents):
        settings, packages = root / "ProjectSettings", root / "Packages"
        if not settings.exists() and not packages.exists():
            continue
        version, manifest = settings / "ProjectVersion.txt", packages / "manifest.json"
        if (any(path.is_symlink() for path in (settings, packages, version, manifest)) or
                not version.is_file() or not manifest.is_file()):
            raise ValueError("Exported Unity project controls are missing or linked")
        versions = [line.partition(":")[2].strip() for line in version.read_text(encoding="utf-8").splitlines()
                    if line.partition(":")[0].strip() == "m_EditorVersion"]
        if versions != [VERSION]:
            raise ValueError("Exported Unity project does not identify the exact required version")
        configuration = read_report(manifest)
        if (not isinstance(configuration, dict) or not isinstance(configuration.get("dependencies"), dict) or
                any(not isinstance(key, str) or not isinstance(value, str)
                    for key, value in configuration["dependencies"].items())):
            raise ValueError("Exported Unity project package manifest is invalid")
        assets = root / "Assets"
        if assets.is_symlink() or source == assets or not source.is_relative_to(assets):
            raise ValueError("Exported fixture source must be a child directory of Assets")
        relative = source.relative_to(root)
        if not all(_portable_file_name(part) for part in relative.parts):
            raise ValueError("Exported fixture Assets path is unsafe for the Windows target")
        firstpass = (assembly == "Assembly-CSharp-firstpass" and
                     relative == Path("Assets/Plugins/Recovered") / assembly)
        if (relative.parts[1].casefold() in {"validation", "batchharnesses"} or
                (relative.parts[1].casefold() == "plugins" and not firstpass)):
            raise ValueError("Exported fixture source overlaps validation infrastructure")
        return relative
    return Path("Assets") / assembly


def verify_source_copy(project, source, assembly, record):
    relative = source_destination(source, assembly)
    if record.get("sourceDestination") != relative.as_posix():
        raise ValueError("Recorded source destination differs from its original layout")
    destination = project / relative
    for path in (destination, *destination.parents):
        if path.is_symlink():
            raise ValueError("Compiled source path may not contain symbolic links")
    files = []
    for path in sorted(destination.rglob("*")):
        if path.is_symlink():
            raise ValueError("Compiled source may not contain symbolic links")
        if path.is_file() and path.suffix != ".meta":
            files.append({"path": path.relative_to(destination).as_posix(),
                          "sha256": hashlib.sha256(path.read_bytes()).hexdigest()})
    if files != record.get("sourceFiles"):
        raise ValueError("Compiled source inventory changed during validation")
    return destination


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
    # Shared infrastructure overlays profile files. Retain one record for the
    # final copied contents rather than duplicating a profile serializer entry.
    final_files = {item["path"]: item for item in copied}
    return sorted(final_files.values(), key=lambda item: item["path"])


def _mapped_wine_path(path, prefix_value):
    if not isinstance(prefix_value, str) or not prefix_value:
        return None
    prefix = Path(prefix_value)
    devices = prefix / "dosdevices"
    if (not prefix.is_absolute() or not prefix.is_dir() or prefix.is_symlink() or
            not devices.is_dir() or devices.is_symlink()):
        return None
    candidates = []
    for letter in "abcdefghijklmnopqrstuvwxyz":
        mapping = devices / (letter + ":")
        if not mapping.is_symlink():
            if mapping.exists():
                return None
            continue
        try:
            root = mapping.resolve(strict=True)
        except (OSError, RuntimeError):
            # Wine's drive discovery also skips a target that cannot be stat'ed.
            continue
        if root.is_dir() and path.is_relative_to(root):
            candidates.append((len(root.parts), letter.upper(), root))
    if not candidates:
        return None
    longest = max(candidate[0] for candidate in candidates)
    matches = [candidate for candidate in candidates if candidate[0] == longest]
    if len(matches) != 1:
        return None
    _, drive, root = matches[0]
    return drive + ":\\" + "\\".join(path.relative_to(root).parts)


def translate_target_path(path, wine, environment, timeout=30):
    """Read existing drive mappings before launching Wine; never change the prefix."""
    if not wine:
        return str(Path(path).resolve())
    if timeout <= 0:
        raise ValueError("Wine path translation timeout must be positive")
    deadline = time.monotonic() + timeout
    path = Path(path).resolve()
    command = [wine, "winepath", "-w", str(path)]

    def remaining():
        seconds = deadline - time.monotonic()
        if seconds <= 0:
            raise subprocess.TimeoutExpired(command, timeout)
        return seconds

    if not all(_portable_file_name(part) for part in path.parts[1:]):
        raise ValueError("Host path cannot be represented safely as a Windows path")
    translated = (_mapped_wine_path(path, environment.get("WINEPREFIX"))
                  if path.exists() or path.parent.is_dir() else None)
    fallback_timeout = remaining()
    if translated is not None:
        return translated
    result = subprocess.run(command, env=environment, capture_output=True, text=True,
                            timeout=fallback_timeout, check=True)
    value = result.stdout.strip()
    windows = PureWindowsPath(value)
    drive = windows.drive
    ordinary_drive = len(drive) == 2 and drive[0] in "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ" and drive[1] == ":"
    unc = drive.startswith("\\\\") and len(drive[2:].split("\\")) == 2 and all(
        _portable_file_name(part) for part in drive[2:].split("\\"))
    if (not windows.is_absolute() or not (ordinary_drive or unc) or
            not all(_portable_file_name(part) for part in windows.parts[1:])):
        raise ValueError("winepath returned an invalid absolute Windows path")
    remaining()
    return value


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


@contextmanager
def wine_editor_slot(enabled, timeout=None):
    if timeout is not None and timeout <= 0:
        raise ValueError("Editor queue timeout must be positive")
    if not enabled or os.name == "nt":
        yield 0.0
        return

    import fcntl

    # The supplied Windows editor shares one Wine prefix across fixture runs.
    # A process lock also releases the slot when a validation process exits early.
    lock_path = ROOT / "Files" / "windows-unity-editor.lock"
    with lock_path.open("a+b") as lock:
        waiting_since = time.monotonic()
        if timeout is None:
            fcntl.flock(lock.fileno(), fcntl.LOCK_EX)
        else:
            while True:
                try:
                    fcntl.flock(lock.fileno(), fcntl.LOCK_EX | fcntl.LOCK_NB)
                    break
                except BlockingIOError:
                    remaining = timeout - (time.monotonic() - waiting_since)
                    if remaining <= 0:
                        raise TimeoutError("Windows editor queue exceeded the validation budget")
                    time.sleep(min(0.1, remaining))
        try:
            yield round(time.monotonic() - waiting_since, 3)
        finally:
            fcntl.flock(lock.fileno(), fcntl.LOCK_UN)


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
        return translate_target_path(path, args.wine, environment)

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
        source_relative = source_destination(args.source_dir, profile["assembly"])
        receipt["sourceDestination"] = source_relative.as_posix()
        receipt["sourceFiles"] = copy_sources(args.source_dir, project / source_relative)
        receipt["harnessFiles"] = copy_harness(args.profile, project / "Assets" / "Validation")
        prefix_command = [args.wine, str(editor)] if args.wine else [str(editor)]
        common = prefix_command + ["-batchmode", "-nographics", "-quit", "-projectPath", target_path(project)]
        if args.stage != "compile":
            common += ["-buildTarget", "Win64"]

        def editor_stage(method, label):
            command = common + ["-executeMethod", method, "-logFile", target_path(run_dir / (label + "-editor.log"))]
            with wine_editor_slot(bool(args.wine)) as queue_seconds:
                outcome = run_process(command, environment, run_dir / (label + "-process.log"), args.timeout)
            outcome["editorQueueSeconds"] = queue_seconds
            receipt["commands"].append(outcome)
            write_json(receipt_path, receipt)
            return outcome

        def behavior_stage(path, label, stage):
            try:
                receipt["stages"][label], report = verified_behavior_report(run_dir, path, stage, args.profile)
                receipt.setdefault("behaviorReports", []).append(report)
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
            expected = {"unityVersion": VERSION, "host": "WindowsEditor", "target": "StandaloneWindows64", "backend": "IL2CPP",
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
        expected_reports = ["project/Reports/editor-behavior.json"]
        if args.stage == "run":
            expected_reports.append("player-behavior.json")
        checked_behavior_report_files(run_dir, receipt["behaviorReports"], expected_reports)
        verify_source_copy(project, args.source_dir, profile["assembly"], receipt)
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
