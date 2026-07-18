using System.Collections.Generic;
using System.Linq;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Maps calls to KeyFunctionAddresses to their underlying IL opcodes.
/// E.g. il2cpp_codegen_object_new => newobj, il2cpp_codegen_throw => throw, etc.
/// </summary>
public static class KeyFunctionRecovery
{
    //All of these have the same params in the same order so we treat them as equal.
    private static readonly HashSet<string> ObjectNewFunctions =
    [
        "il2cpp_object_new",
        "il2cpp_vm_object_new",
        "il2cpp_codegen_object_new",
    ];

    private static readonly HashSet<string> ThrowFunctions =
    [
        "il2cpp_codegen_throw",
        "il2cpp_raise_exception",
        "il2cpp_codegen_raise_exception",
        "il2cpp_vm_raise_exception",
    ];

    private static readonly HashSet<string> RethrowFunctions =
    [
        "il2cpp_codegen_rethrow",
        "il2cpp_vm_rethrow",
    ];

    private static readonly HashSet<string> BoxFunctions =
    [
        "il2cpp_codegen_box",
        "il2cpp_vm_box",
        "il2cpp_object_box",
    ];

    private static readonly HashSet<string> UnboxFunctions =
    [
        "il2cpp_codegen_unbox",
        "il2cpp_vm_unbox",
        "il2cpp_object_unbox",
    ];

    private static readonly HashSet<string> UnboxAnyFunctions =
    [
        "il2cpp_codegen_unbox_any",
        "il2cpp_vm_unbox_any",
    ];

    private static readonly HashSet<string> ArrayNewFunctions =
    [
        "il2cpp_codegen_array_new",
        "il2cpp_array_new",
        "il2cpp_vm_array_new",
        "il2cpp_codegen_array_new_specific",
        "il2cpp_array_new_specific",
    ];

    private static readonly HashSet<string> ArrayNewBoundsCheckFunctions =
    [
        "il2cpp_codegen_array_new_mixed",
        "il2cpp_array_new_mixed",
    ];

    private static readonly HashSet<string> CastClassFunctions =
    [
        "il2cpp_codegen_castclass",
        "il2cpp_vm_castclass",
        "il2cpp_object_is_inst",
        "il2cpp_object_is_inst_castclass",
    ];

    private static readonly HashSet<string> IsInstFunctions =
    [
        "il2cpp_codegen_isinst",
        "il2cpp_vm_isinst",
        "il2cpp_object_is_inst",
    ];

    private static readonly HashSet<string> StringNewFunctions =
    [
        "il2cpp_codegen_string_new_wrapper",
        "il2cpp_string_new_wrapper",
        "il2cpp_string_new",
    ];

    private static readonly HashSet<string> StringConcatFunctions =
    [
        "il2cpp_codegen_string_concat",
    ];

    private static readonly HashSet<string> MemsetFunctions =
    [
        "il2cpp_codegen_memset",
        "il2cpp_memset",
    ];

    private static readonly HashSet<string> BzeroFunctions =
    [
        "il2cpp_codegen_bzero",
        "il2cpp_memset",
    ];

    private static readonly HashSet<string> GCHandleFunctions =
    [
        "il2cpp_gchandle_new",
        "il2cpp_gchandle_new_weak",
        "il2cpp_gchandle_get_target",
        "il2cpp_gchandle_free",
    ];

    private static readonly HashSet<string> MonitorFunctions =
    [
        "il2cpp_monitor_enter",
        "il2cpp_monitor_exit",
        "il2cpp_monitor_try_enter",
        "il2cpp_monitor_wait",
        "il2cpp_monitor_pulse",
        "il2cpp_monitor_pulse_all",
    ];

    private static readonly HashSet<string> ThreadingFunctions =
    [
        "il2cpp_thread_create",
        "il2cpp_thread_attach",
        "il2cpp_thread_detach",
        "il2cpp_thread_get_current",
        "il2cpp_thread_get_current_thread_id",
    ];

    private static readonly HashSet<string> TypeGetFunctions =
    [
        "il2cpp_type_get_object",
        "il2cpp_codegen_type_get_object",
    ];

    private static readonly HashSet<string> RuntimeClassFunctions =
    [
        "il2cpp_runtime_class_init",
        "il2cpp_runtime_class_init_specific",
        "il2cpp_class_get_type",
        "il2cpp_class_get_name",
        "il2cpp_class_get_namespace",
        "il2cpp_class_get_parent",
        "il2cpp_class_get_interfaces",
        "il2cpp_class_get_field_count",
        "il2cpp_class_get_method_count",
        "il2cpp_class_is_valuetype",
        "il2cpp_class_is_enum",
        "il2cpp_class_is_abstract",
        "il2cpp_class_is_interface",
        "il2cpp_class_is_generic",
        "il2cpp_class_is_inflated",
        "il2cpp_class_get_generic_type_definition",
        "il2cpp_class_get_declaring_type",
        "il2cpp_class_get_element_class",
    ];

    private static readonly HashSet<string> RuntimeMethodFunctions =
    [
        "il2cpp_class_get_methods",
        "il2cpp_class_get_fields",
        "il2cpp_class_get_nested_types",
        "il2cpp_class_get_properties",
        "il2cpp_class_get_events",
    ];

    private static readonly HashSet<string> MetadataFunctions =
    [
        "il2cpp_codegen_initialize_runtime_metadata",
        "il2cpp_codegen_initialize_method_metadata",
    ];

    public static void Run(MethodAnalysisContext method)
    {
        foreach (var instruction in method.ControlFlowGraph!.Blocks.SelectMany(block => block.Instructions))
        {
            if (instruction.Operands is not [string keyFunction, ..])
                continue;

            if (ObjectNewFunctions.Contains(keyFunction))
                RewriteObjectNew(instruction);
            else if (ThrowFunctions.Contains(keyFunction))
                RewriteThrow(instruction);
            else if (RethrowFunctions.Contains(keyFunction))
                RewriteRethrow(instruction);
            else if (BoxFunctions.Contains(keyFunction))
                RewriteBox(instruction);
            else if (UnboxFunctions.Contains(keyFunction))
                RewriteUnbox(instruction);
            else if (UnboxAnyFunctions.Contains(keyFunction))
                RewriteUnboxAny(instruction);
            else if (ArrayNewFunctions.Contains(keyFunction))
                RewriteArrayNew(instruction);
            else if (CastClassFunctions.Contains(keyFunction))
                RewriteCastClass(instruction);
            else if (IsInstFunctions.Contains(keyFunction))
                RewriteIsInst(instruction);
            else if (StringNewFunctions.Contains(keyFunction))
                RewriteStringNew(instruction);
            else if (StringConcatFunctions.Contains(keyFunction))
                RewriteStringConcat(instruction);
            else if (MemsetFunctions.Contains(keyFunction) || BzeroFunctions.Contains(keyFunction))
                RewriteMemset(instruction);
        }
    }
    
    private static void RewriteObjectNew(Instruction instruction)
    {
        // Needs the function name, the result, and the class argument.
        if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 3)
            return;

        var result = instruction.Operands[1];
        var klass = instruction.Operands[2];

        instruction.OpCode = OpCode.Newobj;
        instruction.Operands = [result, klass];
    }

    /// <summary>
    /// Rewrites il2cpp_codegen_throw(exception) → a CallVoid that the IL generator will emit as 'throw'.
    /// We keep it as a CallVoid with a special marker operand so IlGenerator can emit the throw opcode.
    /// </summary>
    private static void RewriteThrow(Instruction instruction)
    {
        if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 2)
            return;

        // Change to a special opcode pattern: we mark it as a throw by changing the function name.
        // The IL generator will recognize "il2cpp_codegen_throw" as a throw and emit accordingly.
        // Operands: [function_name, exception_object]
        // We keep it as-is but rename the target to signal the IL gen.
        instruction.Operands[0] = "__throw__";
    }

    private static void RewriteRethrow(Instruction instruction)
    {
        if (instruction.OpCode != OpCode.Call)
            return;

        instruction.Operands[0] = "__rethrow__";
    }

    private static void RewriteBox(Instruction instruction)
    {
        // il2cpp_codegen_box(result, value, klass) → Box value into result
        if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 4)
            return;

        var result = instruction.Operands[1];
        var value = instruction.Operands[2];
        var klass = instruction.Operands[3];

        // Mark as box: result = box klass, value
        // We represent this as a special Move with box metadata attached
        instruction.OpCode = OpCode.Call;
        instruction.Operands = ["__box__", result, value, klass];
    }

    private static void RewriteUnbox(Instruction instruction)
    {
        if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 4)
            return;

        var result = instruction.Operands[1];
        var value = instruction.Operands[2];
        var klass = instruction.Operands[3];

        instruction.Operands = ["__unbox__", result, value, klass];
    }

    private static void RewriteUnboxAny(Instruction instruction)
    {
        if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 4)
            return;

        var result = instruction.Operands[1];
        var value = instruction.Operands[2];
        var klass = instruction.Operands[3];

        instruction.Operands = ["__unbox_any__", result, value, klass];
    }

    private static void RewriteArrayNew(Instruction instruction)
    {
        // il2cpp_codegen_array_new(klass, rank, lengths...) → newarr
        if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 4)
            return;

        var result = instruction.Operands[1];
        var klass = instruction.Operands[2];
        var length = instruction.Operands[3];

        instruction.OpCode = OpCode.Newobj;
        instruction.Operands = [result, klass, length];
    }

    private static void RewriteCastClass(Instruction instruction)
    {
        // il2cpp_codegen_castclass(obj, klass) → castclass
        if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 3)
            return;

        var result = instruction.Operands[1];
        var obj = instruction.Operands[2];

        instruction.Operands = ["__castclass__", result, obj];
    }

    private static void RewriteIsInst(Instruction instruction)
    {
        if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 3)
            return;

        var result = instruction.Operands[1];
        var obj = instruction.Operands[2];

        instruction.Operands = ["__isinst__", result, obj];
    }

    private static void RewriteStringNew(Instruction instruction)
    {
        if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 3)
            return;

        var result = instruction.Operands[1];

        instruction.OpCode = OpCode.Call;
        instruction.Operands = ["__string_new__", result];
    }

    private static void RewriteStringConcat(Instruction instruction)
    {
        if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 3)
            return;

        var result = instruction.Operands[1];

        instruction.OpCode = OpCode.Call;
        instruction.Operands = ["__string_concat__", result];
    }

    private static void RewriteMemset(Instruction instruction)
    {
        if (instruction.OpCode != OpCode.Call || instruction.Operands.Count < 3)
            return;

        var dest = instruction.Operands.Count > 1 ? instruction.Operands[1] : null;
        instruction.Operands = ["__memset__", dest!];
    }
}
