using System.Collections.Generic;
using System.Linq;
using AsmResolver.DotNet;
using AsmResolver.DotNet.Code.Cil;
using AsmResolver.DotNet.Signatures;
using AsmResolver.PE.DotNet.Cil;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;
using Cpp2IL.Core.Utils.AsmResolver;

namespace Cpp2IL.Core;

public static class IlGenerator
{
    public static void GenerateIl(MethodAnalysisContext context, MethodDefinition definition)
    {
        var assembly = context.DeclaringType!.DeclaringAssembly;
        var module = definition.DeclaringModule!;
        var importer = module.DefaultImporter;
        var factory = module.CorLibTypeFactory;

        var writeLine = factory.CorLibScope
            .CreateTypeReference("System", "Console")
            .CreateMemberReference("WriteLine", MethodSignature.CreateStatic(factory.Void, [factory.String]))
            .ImportWith(importer);

        var stringType = factory.CorLibScope.CreateTypeReference("System", "String");
        var stringCtor = stringType
            .CreateMemberReference(".ctor", MethodSignature.CreateStatic(stringType.ToTypeSignature(false), [factory.String]))
            .ImportWith(importer);

        // Change branch targets to instructions
        foreach (var instruction in context.ControlFlowGraph!.Blocks.SelectMany(block => block.Instructions))
        {
            if (instruction.Operands.Count > 0 && instruction.Operands[0] is Block target)
            {
                if (target.Instructions.Count > 0)
                    instruction.Operands[0] = target.Instructions[0];
            }
        }

        var body = new CilMethodBody()
        {
            InitializeLocals = true, // Without this ILSpy does: CompilerServices.Unsafe.SkipInit(out object obj);
            ComputeMaxStackOnBuild = false // There's stack imbalance somewhere, but this works for now
        };

        definition.CilMethodBody = body;

        // Make sure context.Locals actually has all locals (idk why it doesn't sometimes)
        foreach (var operand in context.ControlFlowGraph.Instructions.SelectMany(i => i.Operands))
        {
            LocalVariable? local = null;

            if (operand is FieldReference field)
                local = field.Local;

            if (operand is LocalVariable local2)
                local = local2;

            if (operand is MemoryOperand memory && memory.Base is LocalVariable local3)
                local = local3;

            if (local != null && !context.Locals.Contains(local))
                context.Locals.Add(local);
        }

        // Map ISIL locals to IL
        Dictionary<LocalVariable, CilLocalVariable> locals = [];
        foreach (var local in context.Locals)
        {
            TypeSignature ilType;

            // Use object if type couldn't be determined
            if (local.Type != null)
                ilType = local.Type.ToTypeSignature(module);
            else
                ilType = module.CorLibTypeFactory.Object;

            var ilLocal = new CilLocalVariable(ilType);
            body.LocalVariables.Add(ilLocal);
            locals.Add(local, ilLocal);
        }

        /* foreach (var instruction in context.ControlFlowGraph!.Instructions)
        {
            body.Instructions.Add(CilOpCodes.Ldstr, instruction.ToString());
            body.Instructions.Add(CilOpCodes.Call, _importer!.ImportMethod(_writeLine!));
        }
        body.Instructions.Add(CilOpCodes.Ldstr, "-------------------------------------------------------------------------");
        body.Instructions.Add(CilOpCodes.Call, _importer!.ImportMethod(_writeLine!)); */

        // Generate IL
        Dictionary<Instruction, List<CilInstruction>> instructionMap = [];
        Dictionary<Block, CilInstruction> blockEntryMap = [];
        List<(CilInstruction BranchInstruction, Block TargetBlock)> pendingBlockBranchFixups = [];

        foreach (var block in context.ControlFlowGraph!.Blocks)
        {
            if (block == context.ControlFlowGraph.EntryBlock || block == context.ControlFlowGraph.ExitBlock)
                continue;

            if (block.Instructions.Count == 0)
                continue;

            foreach (var instruction in block.Instructions)
            {
                var generated = GenerateInstructions(instruction, context, definition, locals, writeLine, stringCtor);
                instructionMap.Add(instruction, generated);

                if (!blockEntryMap.ContainsKey(block) && generated.Count > 0)
                    blockEntryMap[block] = generated[0];
            }

            var lastInstruction = block.Instructions.Last();
            
            if (lastInstruction.OpCode == OpCode.ConditionalJump)
            {
                var trueTarget = TryResolveJumpTargetBlock(lastInstruction, context.ControlFlowGraph);
                var falseSuccessor = block.Successors.FirstOrDefault(s => s != trueTarget && s != context.ControlFlowGraph.ExitBlock);
                if (falseSuccessor == null) continue;
                var bridge = new CilInstruction(CilOpCodes.Br, new CilInstructionLabel());
                definition.CilMethodBody!.Instructions.Add(bridge);
                pendingBlockBranchFixups.Add((bridge, falseSuccessor));
            }

            else if (lastInstruction.OpCode != OpCode.Jump && lastInstruction.OpCode != OpCode.Return && lastInstruction.OpCode != OpCode.IndirectJump)
            {
                var successor = block.Successors.FirstOrDefault(s => s != context.ControlFlowGraph.ExitBlock);
                if (successor == null) continue;
                var bridge = new CilInstruction(CilOpCodes.Br, new CilInstructionLabel());
                definition.CilMethodBody!.Instructions.Add(bridge);
                pendingBlockBranchFixups.Add((bridge, successor));
            }
        }
        // Set IL branch targets
        foreach (var kvp in instructionMap)
        {
            var instruction = kvp.Key;
            var il = kvp.Value;

            if (instruction.OpCode == OpCode.Jump || instruction.OpCode == OpCode.ConditionalJump)
            {
                var ilBranch = il.First(i => i.OpCode == CilOpCodes.Br || i.OpCode == CilOpCodes.Brtrue);

                if (instruction.Operands[0] is Block targetBlock)
                {
                    context.AddWarning($"Branch target block not in cfg: {instruction} ({targetBlock})");
                    ilBranch.OpCode = CilOpCodes.Nop;
                    ilBranch.Operand = null;
                    continue;
                }

                var target = (Instruction)instruction.Operands[0];

                if (!instructionMap.ContainsKey(target))
                {
                    context.AddWarning($"Branch target not in ISIL to IL map: {instruction} --- {target}");
                    ilBranch.OpCode = CilOpCodes.Nop;
                    ilBranch.Operand = null;
                    continue;
                }

                ilBranch.Operand = new CilInstructionLabel(instructionMap[target][0]);
            }
        }
        
        foreach (var (branchInstruction, targetBlock) in pendingBlockBranchFixups)
        {
            var target = ResolveBlockEntryInstruction(targetBlock, blockEntryMap);
            if (target == null)
            {
                context.AddWarning($"Unable to resolve branch target block: {targetBlock}");
                branchInstruction.OpCode = CilOpCodes.Nop;
                branchInstruction.Operand = null;
                continue;
            }

            branchInstruction.Operand = new CilInstructionLabel(target);
        }

        // Add analysis warnings
        var instructions = body.Instructions;
        foreach (var warning in context.AnalysisWarnings)
        {
            instructions.Add(CilOpCodes.Ldstr, "Warning: " + warning);
            instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
        }
    }
    
    private static Block? TryResolveJumpTargetBlock(Instruction jumpInstruction, ISILControlFlowGraph cfg)
    {
        if (jumpInstruction.Operands.Count == 0)
            return null;

        if (jumpInstruction.Operands[0] is Block targetBlock)
            return targetBlock;

        if (jumpInstruction.Operands[0] is Instruction targetInstruction)
            return cfg.FindBlockByInstruction(targetInstruction);

        return null;
    }

    private static CilInstruction? ResolveBlockEntryInstruction(Block block,
        Dictionary<Block, CilInstruction> blockEntryMap, HashSet<Block>? visited = null)
    {
        if (blockEntryMap.TryGetValue(block, out var target))
            return target;

        visited ??= [];
        if (!visited.Add(block))
            return null;

        foreach (var successor in block.Successors)
        {
            var resolved = ResolveBlockEntryInstruction(successor, blockEntryMap, visited);
            if (resolved != null)
                return resolved;
        }
        return null;
    }

    private static List<CilInstruction> GenerateInstructions(Instruction instruction, MethodAnalysisContext context,
        MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine, MemberReference stringCtor)
    {
        var body = method.CilMethodBody!;
        var instructions = body.Instructions;
        var currentCount = instructions.Count;
        var startIndex = instructions.Count;

        var module = method.DeclaringModule!;
        var importer = module.DefaultImporter!;

        switch (instruction.OpCode)
        {
            case OpCode.Invalid:
                instructions.Add(CilOpCodes.Ldstr, $"Invalid instruction: {instruction}");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.NotImplemented:
                instructions.Add(CilOpCodes.Ldstr, $"Not implemented instruction: {instruction.Operands[0]}");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.Interrupt:
            case OpCode.Nop:
                instructions.Add(CilOpCodes.Nop);
                break;

            case OpCode.Move:
                if (instruction.Operands[0] is FieldReference field) // stfld takes instance before value so LoadOperand StoreToOperand doesn't work
                {
                    var param = method.Parameters.FirstOrDefault(p => p.Name == field.Local.Name);
                    if (param != null)
                        instructions.Add(CilOpCodes.Ldarg, param);
                    else if (field.Local.IsThis)
                        instructions.Add(CilOpCodes.Ldarg_0);
                    else
                        instructions.Add(CilOpCodes.Ldloc, locals[field.Local]);

                    LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);
                    instructions.Add(CilOpCodes.Stfld, field.Field.ToFieldDescriptor(module));
                    break;
                }

                LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);
                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.Newobj:
                // Try and fuse our Newobj + the follow up constructor CallVoid into one IL newobj. 
                // If we can't, just fall back to an Ldnull.
                if (FindConstructorCall(context, instruction) is { Operands: [MethodAnalysisContext constructor, _, ..] } constructorCall)
                {
                    foreach (var argument in constructorCall.Operands.Skip(2))
                        LoadOperand(argument, method, locals, writeLine, stringCtor);

                    instructions.Add(CilOpCodes.Newobj, importer.ImportMethod(constructor.ToMethodDescriptor(module)));
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);

                    constructorCall.OpCode = OpCode.Nop;
                    constructorCall.Operands = [];
                }
                else
                {
                    instructions.Add(CilOpCodes.Ldnull);
                    StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                }
                break;

            case OpCode.Phi:
                instructions.Add(CilOpCodes.Ldstr, $"Phi opcodes should not exist at this point in decompilation ({instruction})");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.Call:
            case OpCode.CallVoid:
                if (instruction.Operands[0] is not MethodAnalysisContext targetMethod)
                {
                    if (instruction.Operands[0] is ulong targetAddress)
                        instructions.Add(CilOpCodes.Ldstr, $"Method not found @{targetAddress:X}");
                    else if (instruction.Operands[0] is string specialFunc)
                    {
                        // Handle special key functions marked by KeyFunctionRecovery
                        if (HandleSpecialKeyFunction(specialFunc, instruction, context, method, locals, writeLine, stringCtor, importer, module))
                            break;

                        instructions.Add(CilOpCodes.Ldstr, $"Unknown call target: {specialFunc}");
                    }
                    else
                        instructions.Add(CilOpCodes.Ldstr, $"Unknown call target operand: {instruction}");

                    instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                    break;
                }

                var importedMethod = importer.ImportMethod(targetMethod.ToMethodDescriptor(module));

                var thisParamIndex = instruction.OpCode == OpCode.Call ? 2 : 1;

                if (!targetMethod.IsStatic) // Load 'this' param
                {
                    if ((instruction.Operands.Count - 1) >= thisParamIndex)
                        LoadOperand(instruction.Operands[thisParamIndex], method, locals, writeLine, stringCtor);
                    else
                    {
                        instructions.Add(CilOpCodes.Ldstr, $"Non static method called without 'this' param ({instruction})");
                        instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                    }
                }

                // Load normal params
                var callParamIndex = instruction.OpCode == OpCode.Call ? (targetMethod.IsStatic ? 2 : 3) : (targetMethod.IsStatic ? 1 : 2);
                var callParams = instruction.Operands.Skip(callParamIndex).Take(instruction.Operands.Count - 1 - thisParamIndex);
                foreach (var param in callParams)
                    LoadOperand(param, method, locals, writeLine, stringCtor);

                instructions.Add(CilOpCodes.Call, importedMethod);

                if (instruction.OpCode == OpCode.Call) // Store return value
                    StoreToOperand(instruction.Operands[1], method, locals, writeLine);

                break;

            case OpCode.IndirectCall:
                // Try to emit the indirect call target
                if (instruction.Operands.Count >= 1 && instruction.Operands[0] is string keyFunc)
                {
                    // Handle special key functions that were not fully resolved
                    instructions.Add(CilOpCodes.Ldstr, $"Indirect call to {keyFunc}: {instruction}");
                    instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                }
                else
                {
                    instructions.Add(CilOpCodes.Ldstr, $"Indirect call: {instruction} (should have been resolved before IL gen)");
                    instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                }
                break;

            case OpCode.Return:
                if (!context.IsVoid && instruction.Operands.Count == 1)
                    LoadOperand(instruction.Operands[0], method, locals, writeLine, stringCtor);
                instructions.Add(CilOpCodes.Ret);
                break;

            case OpCode.Jump:
                instructions.Add(CilOpCodes.Br, new CilInstructionLabel());
                break;

            case OpCode.ConditionalJump:
                LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);
                instructions.Add(CilOpCodes.Brtrue, new CilInstructionLabel());
                break;

            case OpCode.IndirectJump:
                instructions.Add(CilOpCodes.Ldstr, $"Indirect jump: {instruction} (should have been resolved before IL gen)");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.ShiftStack:
                instructions.Add(CilOpCodes.Ldstr, $"Stack shift: {instruction} (stack analysis should have removed these)");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;

            case OpCode.CheckEqual:
            case OpCode.CheckGreater:
            case OpCode.CheckLess:
            case OpCode.CheckNotEqual:
            case OpCode.CheckGreaterOrEqual:
            case OpCode.CheckLessOrEqual:

            case OpCode.Add:
            case OpCode.Subtract:
            case OpCode.Multiply:
            case OpCode.Divide:

            case OpCode.ShiftLeft:
            case OpCode.ShiftRight:

            case OpCode.And:
            case OpCode.Or:
            case OpCode.Xor:
                LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);
                LoadOperand(instruction.Operands[2], method, locals, writeLine, stringCtor);

                switch (instruction.OpCode)
                {
                    case OpCode.CheckEqual: instructions.Add(CilOpCodes.Ceq); break;
                    case OpCode.CheckGreater: instructions.Add(CilOpCodes.Cgt); break;
                    case OpCode.CheckLess: instructions.Add(CilOpCodes.Clt); break;

                    // a != b  ==  (a == b) == 0
                    case OpCode.CheckNotEqual:
                        instructions.Add(CilOpCodes.Ceq);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;
                    // a >= b  ==  !(a < b)
                    case OpCode.CheckGreaterOrEqual:
                        instructions.Add(CilOpCodes.Clt);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;
                    // a <= b  ==  !(a > b)
                    case OpCode.CheckLessOrEqual:
                        instructions.Add(CilOpCodes.Cgt);
                        instructions.Add(CilOpCodes.Ldc_I4_0);
                        instructions.Add(CilOpCodes.Ceq);
                        break;

                    case OpCode.Add: instructions.Add(CilOpCodes.Add); break;
                    case OpCode.Subtract: instructions.Add(CilOpCodes.Sub); break;
                    case OpCode.Multiply: instructions.Add(CilOpCodes.Mul); break;
                    case OpCode.Divide: instructions.Add(CilOpCodes.Div); break;

                    case OpCode.ShiftLeft: instructions.Add(CilOpCodes.Shl); break;
                    case OpCode.ShiftRight: instructions.Add(CilOpCodes.Shr); break;

                    case OpCode.And: instructions.Add(CilOpCodes.And); break;
                    case OpCode.Or: instructions.Add(CilOpCodes.Or); break;
                    case OpCode.Xor: instructions.Add(CilOpCodes.Xor); break;
                }

                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            case OpCode.Not:
            case OpCode.Negate:
                LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);

                switch (instruction.OpCode)
                {
                    case OpCode.Not: instructions.Add(CilOpCodes.Not); break;
                    case OpCode.Negate: instructions.Add(CilOpCodes.Neg); break;
                }

                StoreToOperand(instruction.Operands[0], method, locals, writeLine);
                break;

            default:
                instructions.Add(CilOpCodes.Ldstr, $"Unknown instruction: {instruction}");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;
        }

        return instructions.ToList().GetRange(startIndex, instructions.Count - startIndex); // Return added IL
    }

    /// <summary>
    /// Handles special key functions that were marked by KeyFunctionRecovery but not fully resolved.
    /// Returns true if the instruction was handled and the caller should break.
    /// </summary>
    private static bool HandleSpecialKeyFunction(string keyFunction, Instruction instruction, MethodAnalysisContext context,
        MethodDefinition method, Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine, MemberReference stringCtor,
        ReferenceImporter importer, ModuleDefinition module)
    {
        var instructions = method.CilMethodBody!.Instructions;

        switch (keyFunction)
        {
            case "__throw__":
                // throw exceptionObj
                if (instruction.Operands.Count >= 2)
                    LoadOperand(instruction.Operands[1], method, locals, writeLine, stringCtor);
                instructions.Add(CilOpCodes.Throw);
                return true;

            case "__rethrow__":
                instructions.Add(CilOpCodes.Rethrow);
                return true;

            case "__box__":
                if (instruction.Operands.Count >= 4)
                {
                    LoadOperand(instruction.Operands[2], method, locals, writeLine, stringCtor);
                    var klass = instruction.Operands[3];
                    var boxType = ResolveITypeDefOrRef(klass, module, importer);
                    if (boxType != null)
                        instructions.Add(CilOpCodes.Box, boxType);
                    else
                        LoadOperand(klass, method, locals, writeLine, stringCtor);
                    StoreToOperand(instruction.Operands[1], method, locals, writeLine);
                }
                return true;

            case "__unbox__":
                if (instruction.Operands.Count >= 4)
                {
                    LoadOperand(instruction.Operands[2], method, locals, writeLine, stringCtor);
                    var unboxTypeRef = ResolveITypeDefOrRef(instruction.Operands[3], module, importer);
                    if (unboxTypeRef != null)
                        instructions.Add(CilOpCodes.Unbox_Any, unboxTypeRef);
                    else
                        LoadOperand(instruction.Operands[3], method, locals, writeLine, stringCtor);
                    StoreToOperand(instruction.Operands[1], method, locals, writeLine);
                }
                return true;

            case "__unbox_any__":
                if (instruction.Operands.Count >= 4)
                {
                    LoadOperand(instruction.Operands[2], method, locals, writeLine, stringCtor);
                    var uaTypeRef = ResolveITypeDefOrRef(instruction.Operands[3], module, importer);
                    if (uaTypeRef != null)
                        instructions.Add(CilOpCodes.Unbox_Any, uaTypeRef);
                    else
                        LoadOperand(instruction.Operands[3], method, locals, writeLine, stringCtor);
                    StoreToOperand(instruction.Operands[1], method, locals, writeLine);
                }
                return true;

            case "__isinst__":
                if (instruction.Operands.Count >= 3)
                {
                    LoadOperand(instruction.Operands[2], method, locals, writeLine, stringCtor);
                    var isInstTypeRef = instruction.Operands.Count >= 4 ? ResolveITypeDefOrRef(instruction.Operands[3], module, importer) : null;
                    if (isInstTypeRef != null)
                        instructions.Add(CilOpCodes.Isinst, isInstTypeRef);
                    else
                        LoadOperand(instruction.Operands[2], method, locals, writeLine, stringCtor);
                    StoreToOperand(instruction.Operands[1], method, locals, writeLine);
                }
                return true;

            case "__castclass__":
                if (instruction.Operands.Count >= 3)
                {
                    LoadOperand(instruction.Operands[2], method, locals, writeLine, stringCtor);
                    var castTypeRef = instruction.Operands.Count >= 4 ? ResolveITypeDefOrRef(instruction.Operands[3], module, importer) : null;
                    if (castTypeRef != null)
                        instructions.Add(CilOpCodes.Castclass, castTypeRef);
                    else
                        LoadOperand(instruction.Operands[2], method, locals, writeLine, stringCtor);
                    StoreToOperand(instruction.Operands[1], method, locals, writeLine);
                }
                return true;

            case "__string_new__":
                // String creation - typically the result is already the string from a metadata usage
                if (instruction.Operands.Count >= 2)
                {
                    // The string literal should already be resolved as a metadata usage
                    StoreToOperand(instruction.Operands[1], method, locals, writeLine);
                }
                return true;

            case "__string_concat__":
                if (instruction.Operands.Count >= 2)
                {
                    StoreToOperand(instruction.Operands[1], method, locals, writeLine);
                }
                return true;

            default:
                return false;
        }
    }

    private static ITypeDefOrRef? ResolveITypeDefOrRef(object operand, ModuleDefinition module, ReferenceImporter importer)
    {
        if (operand is TypeAnalysisContext typeContext)
        {
            var asmType = typeContext.GetExtraData<AsmResolver.DotNet.TypeDefinition>("AsmResolverType");
            if (asmType != null)
                return importer.ImportType(asmType);
        }
        return null;
    }

    // Try find the follow up CallVoid for a constructor, after a Newobj.
    private static Instruction? FindConstructorCall(MethodAnalysisContext context, Instruction newobj)
    {
        var newObject = newobj.Operands[0];

        foreach (var block in context.ControlFlowGraph!.Blocks)
        {
            var index = block.Instructions.IndexOf(newobj);
            if (index < 0)
                continue;

            for (var i = index + 1; i < block.Instructions.Count; i++)
            {
                var candidate = block.Instructions[i];
                if (candidate is { OpCode: OpCode.CallVoid, Operands: [MethodAnalysisContext { Name: ".ctor" }, _, ..] }
                    && ReferenceEquals(candidate.Operands[1], newObject))
                    return candidate;
            }

            return null;
        }

        return null;
    }

    private static void LoadOperand(object operand, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine, MemberReference stringCtor)
    {
        var instructions = method.CilMethodBody!.Instructions;

        var module = method.DeclaringModule!;
        var importer = module.DefaultImporter!;

        switch (operand)
        {
            case int i:
                instructions.Add(CilOpCodes.Ldc_I4, i);
                break;
            case uint ui:
                instructions.Add(CilOpCodes.Ldc_I4, unchecked((int)ui));
                break;
            case short s:
                instructions.Add(CilOpCodes.Ldc_I4, s);
                break;
            case ushort us:
                instructions.Add(CilOpCodes.Ldc_I4, us);
                break;
            case byte b8:
                instructions.Add(CilOpCodes.Ldc_I4, b8);
                break;
            case sbyte sb8:
                instructions.Add(CilOpCodes.Ldc_I4, sb8);
                break;
            case long l:
                instructions.Add(CilOpCodes.Ldc_I8, l);
                break;
            case ulong ul:
                instructions.Add(CilOpCodes.Ldc_I8, unchecked((long)ul));
                break;
            case float f:
                instructions.Add(CilOpCodes.Ldc_R4, f);
                break;
            case double d:
                instructions.Add(CilOpCodes.Ldc_R8, d);
                break;
            case bool b:
                instructions.Add(CilOpCodes.Ldc_I4, b ? 1 : 0);
                break;
            case string s:
                instructions.Add(CilOpCodes.Ldstr, s);
                break;
            case LocalVariable local:
                var param = method.Parameters.FirstOrDefault(p => p.Name == local.Name);
                if (param != null)
                    instructions.Add(CilOpCodes.Ldarg, param);
                else
                    instructions.Add(CilOpCodes.Ldloc, locals[local]);
                break;
            case FieldReference field:
                // Load the correct local/parameter for the field's owner, not always 'this'.
                if (field.Local.IsThis)
                    instructions.Add(CilOpCodes.Ldarg_0);
                else
                {
                    var fieldParam = method.Parameters.FirstOrDefault(p => p.Name == field.Local.Name);
                    if (fieldParam != null)
                        instructions.Add(CilOpCodes.Ldarg, fieldParam);
                    else if (locals.TryGetValue(field.Local, out var fieldLocalVar))
                        instructions.Add(CilOpCodes.Ldloc, fieldLocalVar);
                    else
                        instructions.Add(CilOpCodes.Ldarg_0); // fallback
                }
                instructions.Add(CilOpCodes.Ldfld, field.Field.ToFieldDescriptor(module));
                break;
            case MemoryOperand memory:
                if (memory.Index == null && memory.Addend == 0 && memory.Scale == 0
                    && memory.Base is LocalVariable local2)
                {
                    var param2 = method.Parameters.FirstOrDefault(p => p.Name == local2.Name);
                    if (param2 != null)
                        instructions.Add(CilOpCodes.Ldarg, param2);
                    else if (locals.TryGetValue(local2, out var memLocalVar))
                        instructions.Add(CilOpCodes.Ldloc, memLocalVar);
                    else
                        instructions.Add(CilOpCodes.Ldarg_0);
                    break;
                }
                else if (memory.Index == null && memory.Scale == 0
                    && memory.Base is LocalVariable local3)
                {
                    // Load base + addend as pointer offset (simplified pattern for [base+offset])
                    var param3 = method.Parameters.FirstOrDefault(p => p.Name == local3.Name);
                    if (param3 != null)
                        instructions.Add(CilOpCodes.Ldarg, param3);
                    else if (locals.TryGetValue(local3, out var memLocalVar2))
                        instructions.Add(CilOpCodes.Ldloc, memLocalVar2);
                    else
                        instructions.Add(CilOpCodes.Ldarg_0);

                    if (memory.Addend != 0)
                    {
                        // Try to add the offset using add instruction
                        instructions.Add(CilOpCodes.Ldc_I4, (int)memory.Addend);
                        instructions.Add(CilOpCodes.Add);
                        instructions.Add(CilOpCodes.Ldind_Ref); // Dereference pointer
                    }
                    break;
                }
                instructions.Add(CilOpCodes.Ldstr, "Unmanaged memory load: " + operand.ToString());
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;
            case RuntimeMethodInfoAnalysisContext:
                //Not fully implemented, these basically shouldn't actually ever exist in the final IL.
                instructions.Add(CilOpCodes.Ldc_I4_0);
                instructions.Add(CilOpCodes.Conv_I);
                break;
            case TypeAnalysisContext type:
                if (type.Name == "T")
                {
                    // idk what to do here
                    instructions.Add(CilOpCodes.Ldstr, "<T>");
                    instructions.Add(CilOpCodes.Newobj, importer.ImportMethod(stringCtor));
                    break;
                }

                // Try to first get constructor without params
                var constructor = type.Methods.FirstOrDefault(m => m.Parameters.Count == 0 && m.Name == ".ctor" || m.Name == ".cctor");
                constructor ??= type.Methods.FirstOrDefault(m => m.Name == ".ctor" || m.Name == ".cctor");

                if (constructor == null)
                {
                    instructions.Add(CilOpCodes.Ldstr, $"Constructor not found for: {operand} (probably static type)");
                    instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                    break;
                }

                foreach (var param2 in constructor.Parameters)
                    instructions.Add(CilOpCodes.Ldstr, "Constructor param: " + param2);
                instructions.Add(CilOpCodes.Newobj, importer.ImportMethod(constructor.ToMethodDescriptor(module)));
                break;
            default:
                instructions.Add(CilOpCodes.Ldstr, "Unknown operand: " + operand.ToString());
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;
        }
    }

    private static void StoreToOperand(object operand, MethodDefinition method,
        Dictionary<LocalVariable, CilLocalVariable> locals, MemberReference writeLine)
    {
        var instructions = method.CilMethodBody!.Instructions;

        var module = method.DeclaringModule!;
        var importer = module.DefaultImporter!;

        switch (operand)
        {
            case LocalVariable local:
                instructions.Add(CilOpCodes.Stloc, locals[local]);
                break;

            case FieldReference field:
                // Load the correct local/parameter for the field's owner
                if (field.Local.IsThis)
                    instructions.Add(CilOpCodes.Ldarg_0);
                else
                {
                    var storeParam = method.Parameters.FirstOrDefault(p => p.Name == field.Local.Name);
                    if (storeParam != null)
                        instructions.Add(CilOpCodes.Ldarg, storeParam);
                    else if (locals.TryGetValue(field.Local, out var storeLocalVar))
                        instructions.Add(CilOpCodes.Ldloc, storeLocalVar);
                    else
                        instructions.Add(CilOpCodes.Ldarg_0); // fallback
                }
                instructions.Add(CilOpCodes.Stfld, field.Field.ToFieldDescriptor(module));
                break;

            case MemoryOperand memory:
                if (memory.Index == null && memory.Addend == 0 && memory.Scale == 0
                    && memory.Base is LocalVariable local2)
                {
                    if (locals.TryGetValue(local2, out var storeMemLocal))
                        instructions.Add(CilOpCodes.Stloc, storeMemLocal);
                    else
                    {
                        var storeParam2 = method.Parameters.FirstOrDefault(p => p.Name == local2.Name);
                        if (storeParam2 != null)
                            instructions.Add(CilOpCodes.Ldarg, storeParam2);
                        else
                            instructions.Add(CilOpCodes.Stloc, locals[local2]);
                    }
                }
                break;

            default:
                instructions.Add(CilOpCodes.Ldstr, $"Store into unknown operand: {operand}");
                instructions.Add(CilOpCodes.Call, importer.ImportMethod(writeLine));
                break;
        }
    }
}
