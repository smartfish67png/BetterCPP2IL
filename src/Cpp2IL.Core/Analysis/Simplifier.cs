using System.Collections.Generic;
using Cpp2IL.Core.Graphs;
using Cpp2IL.Core.ISIL;
using Cpp2IL.Core.Model.Contexts;

namespace Cpp2IL.Core.Analysis;

/// <summary>
/// Post-SSA copy/constant cleanup. The bulk of copy and constant propagation is done earlier, in SSA,
/// by <see cref="SsaSimplifier"/>; this pass mops up the copies that <em>destroying</em> SSA leaves
/// behind - each phi is lowered to a <c>Move</c> on every incoming edge, so the phi's result becomes a
/// single local with one definition per predecessor.
///
/// Those multiple definitions mean a value is no longer single-assignment: at the join the definitions
/// merge, and which one reaches a use is path-dependent. So unlike <see cref="SsaSimplifier"/>, this
/// pass cannot blindly forward a definition - it walks the CFG and refuses to carry a multiply-defined
/// local's value across the join its other definitions merge at (where the phi used to be).
/// </summary>
public static class Simplifier
{
    public static void Simplify(MethodAnalysisContext method)
    {
        var cfg = method.ControlFlowGraph!;

        InlineLocals(method);

        // Repeat until no change
        var changed = true;
        while (changed)
            changed = InlineConstantsSinglePass(cfg);

        // More locals can now be inlined
        InlineLocals(method);

        // Strength reduction: convert multiply-by-power-of-2 to shifts, etc.
        StrengthReduction(cfg);

        // Constant folding: evaluate constant expressions
        ConstantFolding(cfg);

        cfg.RemoveNops();
        cfg.RemoveEmptyBlocks();
    }

    private static bool InlineConstantsSinglePass(ISILControlFlowGraph graph)
    {
        var changed = false;
        var definitionCounts = CountDefinitions(graph);

        var visited = new HashSet<Block>();
        var queue = new Queue<Block>();

        queue.Enqueue(graph.EntryBlock);
        visited.Add(graph.EntryBlock);

        while (queue.Count > 0)
        {
            var block = queue.Dequeue();

            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];

                // If it's move and it moves something to local, replace and remove it
                if (instruction.OpCode == OpCode.Move && instruction.Operands[0] is LocalVariable local)
                {
                    if (IsLocalUsedAfterInstruction(block, i + 1, local, out var usedByMemory))
                    {
                        // This can't be inlined into memory operand
                        if (usedByMemory) continue;

                        // A local with several definitions is not in SSA form, so its value at a join
                        // depends on the path taken; don't carry this definition across that join.
                        var stopAtJoins = definitionCounts.TryGetValue(local, out var defs) && defs > 1;

                        // Replace local
                        ReplaceLocalsUntilReassignment(block, i + 1, local, instruction.Operands[1], stopAtJoins);

                        // Only drop the defining move once the local has no remaining uses; if the
                        // replacement stopped at a join, the local is still live past it so the move stays.
                        if (IsLocalUsedAfterInstruction(block, i + 1, local, out _))
                            continue;

                        // Change that move to nop
                        instruction.OpCode = OpCode.Nop;
                        instruction.Operands = [];

                        changed = true;
                    }
                }
            }

            foreach (var successor in block.Successors)
            {
                if (visited.Add(successor))
                    queue.Enqueue(successor);
            }
        }

        return changed;
    }

    private static void InlineLocals(MethodAnalysisContext method)
    {
        var graph = method.ControlFlowGraph;
        var definitionCounts = CountDefinitions(graph!);

        var visited = new HashSet<Block>();
        var queue = new Queue<Block>();

        queue.Enqueue(graph!.EntryBlock);
        visited.Add(graph.EntryBlock);

        while (queue.Count > 0)
        {
            var block = queue.Dequeue();

            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];

                // If it's move and it moves local to local, replace and remove it
                if (instruction.OpCode == OpCode.Move && instruction.Operands[0] is LocalVariable local && instruction.Operands[1] is LocalVariable source)
                {
                    // A local with several definitions is not in SSA form, so its value at a join
                    // depends on the path taken; don't carry this definition across that join.
                    var stopAtJoins = definitionCounts.TryGetValue(local, out var defs) && defs > 1;

                    // Replace local with source
                    ReplaceLocalsUntilReassignment(block, i + 1, local, source, stopAtJoins);

                    // If the replacement stopped at a join merging another definition, the local is
                    // still live there - keep its defining move rather than dropping the value on this path.
                    if (IsLocalUsedAfterInstruction(block, i + 1, local, out _))
                        continue;

                    if (!method.ParameterLocals.Contains(local))
                        method.Locals.Remove(local);

                    // Change that move to nop
                    instruction.OpCode = OpCode.Nop;
                    instruction.Operands = [];
                }
            }

            foreach (var successor in block.Successors)
            {
                if (visited.Add(successor))
                    queue.Enqueue(successor);
            }
        }
    }

    // Counts how many instructions define each local. A local with more than one definition is not in
    // SSA form: at a control-flow join its value depends on which predecessor was taken, so none of its
    // definitions may be propagated across that join - a phi would be needed there instead.
    private static Dictionary<LocalVariable, int> CountDefinitions(ISILControlFlowGraph graph)
    {
        var counts = new Dictionary<LocalVariable, int>();

        foreach (var block in graph.Blocks)
        foreach (var instruction in block.Instructions)
            if (instruction.Destination is LocalVariable local)
                counts[local] = counts.TryGetValue(local, out var count) ? count + 1 : 1;

        return counts;
    }

    private static void ReplaceLocalsUntilReassignment(Block block, int startIndex, LocalVariable local, object replacement, bool stopAtJoins)
    {
        var visited = new HashSet<(Block, int)>();

        void ProcessBlock(Block currentBlock, int index)
        {
            var key = (currentBlock, index);

            if (!visited.Add(key))
                return;

            // Process instructions starting at the given index
            for (var i = index; i < currentBlock.Instructions.Count; i++)
            {
                var instruction = currentBlock.Instructions[i];

                // Stop on this branch when reassigned
                if (instruction.Destination is LocalVariable destLocal && destLocal == local)
                    return;

                // Replace operands
                for (var j = 0; j < instruction.Operands.Count; j++)
                {
                    var operand = instruction.Operands[j];

                    if (operand is LocalVariable usedLocal && usedLocal == local)
                        instruction.Operands[j] = replacement;

                    // A memory operand's base/index holds an address, so only a local replacement may
                    // be substituted there (copy propagation). A constant/value replacement is left in
                    // place - the caller sees the local is still used and keeps its defining move.
                    if (operand is MemoryOperand memory && replacement is LocalVariable)
                    {
                        if (memory.Base is LocalVariable baseLocal && baseLocal == local)
                            memory.Base = replacement;

                        if (memory.Index is LocalVariable indexLocal && indexLocal == local)
                            memory.Index = replacement;

                        instruction.Operands[j] = memory;
                    }
                }
            }

            // Process successors
            foreach (var successor in currentBlock.Successors)
            {
                // A join merges this local's other definitions, so for a non-SSA (multiply-defined)
                // local the replacement must not flow past it - the value there is path-dependent.
                if (stopAtJoins && successor.Predecessors.Count > 1)
                    continue;

                ProcessBlock(successor, 0);
            }
        }

        ProcessBlock(block, startIndex);
    }

    /// <summary>
    /// Strength reduction: converts expensive arithmetic operations into cheaper ones.
    /// E.g., multiply by power of 2 → left shift, multiply by constant → shift-and-add.
    /// </summary>
    private static void StrengthReduction(ISILControlFlowGraph graph)
    {
        foreach (var block in graph.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];

                // x * 2^n → x << n
                if (instruction.OpCode == OpCode.Multiply && instruction.Operands.Count >= 3)
                {
                    if (instruction.Operands[2] is int mulConst)
                    {
                        var shiftAmount = GetShiftAmount(mulConst);
                        if (shiftAmount >= 0)
                        {
                            instruction.OpCode = OpCode.ShiftLeft;
                            instruction.Operands[2] = shiftAmount;
                            continue;
                        }
                    }
                    // Also check the first operand (commutative)
                    if (instruction.Operands[1] is int mulConst2)
                    {
                        var shiftAmount2 = GetShiftAmount(mulConst2);
                        if (shiftAmount2 >= 0)
                        {
                            instruction.OpCode = OpCode.ShiftLeft;
                            instruction.Operands[1] = instruction.Operands[2];
                            instruction.Operands[2] = shiftAmount2;
                        }
                    }
                }

                // x / 2^n → x >> n (unsigned)
                if (instruction.OpCode == OpCode.Divide && instruction.Operands.Count >= 3)
                {
                    if (instruction.Operands[2] is int divConst)
                    {
                        var shiftAmount = GetShiftAmount(divConst);
                        if (shiftAmount >= 0)
                        {
                            instruction.OpCode = OpCode.ShiftRight;
                            instruction.Operands[2] = shiftAmount;
                        }
                    }
                }

                // x * 0 → 0 (dead assignment)
                if (instruction.OpCode == OpCode.Multiply && instruction.Operands.Count >= 3)
                {
                    if (instruction.Operands[1] is int || instruction.Operands[2] is int)
                    {
                        var val = instruction.Operands[1] is int v1 ? v1 : (instruction.Operands[2] is int v2 ? v2 : -1);
                        if (val == 0)
                        {
                            instruction.OpCode = OpCode.Move;
                            instruction.Operands[2] = 0;
                        }
                    }
                }

                // x * 1 → x (copy)
                if (instruction.OpCode == OpCode.Multiply && instruction.Operands.Count >= 3)
                {
                    if (instruction.Operands[1] is int v1 && v1 == 1)
                    {
                        instruction.OpCode = OpCode.Move;
                        instruction.Operands[2] = instruction.Operands[2];
                    }
                    else if (instruction.Operands[2] is int v2 && v2 == 1)
                    {
                        instruction.OpCode = OpCode.Move;
                        instruction.Operands[2] = instruction.Operands[1];
                    }
                }

                // x + 0 → x (copy)
                if (instruction.OpCode == OpCode.Add && instruction.Operands.Count >= 3)
                {
                    if (instruction.Operands[1] is int v1 && v1 == 0)
                    {
                        instruction.OpCode = OpCode.Move;
                        instruction.Operands[2] = instruction.Operands[2];
                    }
                    else if (instruction.Operands[2] is int v2 && v2 == 0)
                    {
                        instruction.OpCode = OpCode.Move;
                        instruction.Operands[2] = instruction.Operands[1];
                    }
                }

                // x - 0 → x (copy)
                if (instruction.OpCode == OpCode.Subtract && instruction.Operands.Count >= 3)
                {
                    if (instruction.Operands[2] is int v2 && v2 == 0)
                    {
                        instruction.OpCode = OpCode.Move;
                        instruction.Operands[2] = instruction.Operands[1];
                    }
                }

                // x & 0 → 0 (dead assignment)
                if (instruction.OpCode == OpCode.And && instruction.Operands.Count >= 3)
                {
                    if (instruction.Operands[1] is int va && va == 0)
                    {
                        instruction.OpCode = OpCode.Move;
                        instruction.Operands[2] = 0;
                    }
                    else if (instruction.Operands[2] is int vb && vb == 0)
                    {
                        instruction.OpCode = OpCode.Move;
                        instruction.Operands[2] = 0;
                    }
                }

                // x | 0 → x (copy)
                if (instruction.OpCode == OpCode.Or && instruction.Operands.Count >= 3)
                {
                    if (instruction.Operands[1] is int vo1 && vo1 == 0)
                    {
                        instruction.OpCode = OpCode.Move;
                        instruction.Operands[2] = instruction.Operands[2];
                    }
                    else if (instruction.Operands[2] is int vo2 && vo2 == 0)
                    {
                        instruction.OpCode = OpCode.Move;
                        instruction.Operands[2] = instruction.Operands[1];
                    }
                }

                // x ^ 0 → x (copy)
                if (instruction.OpCode == OpCode.Xor && instruction.Operands.Count >= 3)
                {
                    if (instruction.Operands[1] is int vx1 && vx1 == 0)
                    {
                        instruction.OpCode = OpCode.Move;
                        instruction.Operands[2] = instruction.Operands[2];
                    }
                    else if (instruction.Operands[2] is int vx2 && vx2 == 0)
                    {
                        instruction.OpCode = OpCode.Move;
                        instruction.Operands[2] = instruction.Operands[1];
                    }
                }

                // ~0 → -1 (all bits set)
                if (instruction.OpCode == OpCode.Not && instruction.Operands.Count >= 2)
                {
                    if (instruction.Operands[1] is int notVal && notVal == 0)
                    {
                        instruction.OpCode = OpCode.Move;
                        instruction.Operands[2] = -1;
                    }
                }

                // -(-x) → x (double negation)
                if (instruction.OpCode == OpCode.Negate && instruction.Operands.Count >= 2)
                {
                    if (instruction.Operands[1] is LocalVariable innerLocal)
                    {
                        // Check if the defining instruction is also a Negate
                        var definingInstr = FindDefinition(graph, innerLocal);
                        if (definingInstr is { OpCode: OpCode.Negate, Operands.Count: >= 3 })
                        {
                            var innerSource = definingInstr.Operands[1];
                            instruction.OpCode = OpCode.Move;
                            instruction.Operands[2] = innerSource;
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Constant folding: evaluates constant expressions at compile time.
    /// </summary>
    private static void ConstantFolding(ISILControlFlowGraph graph)
    {
        foreach (var block in graph.Blocks)
        {
            for (var i = 0; i < block.Instructions.Count; i++)
            {
                var instruction = block.Instructions[i];

                if (instruction.OpCode is >= OpCode.Add and <= OpCode.Xor && instruction.Operands.Count >= 3)
                {
                    // Check if both operands are constants
                    if (instruction.Operands[1] is int leftConst && instruction.Operands[2] is int rightConst)
                    {
                        var result = instruction.OpCode switch
                        {
                            OpCode.Add => leftConst + rightConst,
                            OpCode.Subtract => leftConst - rightConst,
                            OpCode.Multiply => leftConst * rightConst,
                            OpCode.Divide when rightConst != 0 => leftConst / rightConst,
                            OpCode.ShiftLeft => leftConst << rightConst,
                            OpCode.ShiftRight => leftConst >> rightConst,
                            OpCode.And => leftConst & rightConst,
                            OpCode.Or => leftConst | rightConst,
                            OpCode.Xor => leftConst ^ rightConst,
                            _ => (int?)null
                        };

                        if (result.HasValue)
                        {
                            instruction.OpCode = OpCode.Move;
                            instruction.Operands[1] = result.Value;
                            instruction.Operands.RemoveAt(2);
                        }
                    }
                }

                // Fold comparisons
                if (instruction.OpCode is >= OpCode.CheckEqual and <= OpCode.CheckLessOrEqual
                    && instruction.Operands.Count >= 3)
                {
                    if (instruction.Operands[1] is int cmpLeft && instruction.Operands[2] is int cmpRight)
                    {
                        var result = instruction.OpCode switch
                        {
                            OpCode.CheckEqual => cmpLeft == cmpRight,
                            OpCode.CheckNotEqual => cmpLeft != cmpRight,
                            OpCode.CheckGreater => cmpLeft > cmpRight,
                            OpCode.CheckGreaterOrEqual => cmpLeft >= cmpRight,
                            OpCode.CheckLess => cmpLeft < cmpRight,
                            OpCode.CheckLessOrEqual => cmpLeft <= cmpRight,
                            _ => (bool?)null
                        };

                        if (result.HasValue)
                        {
                            instruction.OpCode = OpCode.Move;
                            instruction.Operands[1] = result.Value ? 1 : 0;
                            instruction.Operands.RemoveAt(2);
                        }
                    }
                }
            }
        }
    }

    /// <summary>
    /// Returns the shift amount if the value is a power of 2, otherwise -1.
    /// </summary>
    private static int GetShiftAmount(int value)
    {
        if (value <= 0)
            return -1;

        // Check if value is a power of 2
        if ((value & (value - 1)) != 0)
            return -1;

        // Count trailing zeros
        var count = 0;
        var v = value;
        while ((v & 1) == 0)
        {
            v >>= 1;
            count++;
        }
        return count;
    }

    /// <summary>
    /// Finds the instruction that defines the given local variable in the CFG.
    /// </summary>
    private static Instruction? FindDefinition(ISILControlFlowGraph graph, LocalVariable local)
    {
        foreach (var block in graph.Blocks)
        {
            foreach (var instr in block.Instructions)
            {
                if (instr.Destination is LocalVariable dest && dest == local)
                    return instr;
            }
        }
        return null;
    }

    private static bool IsLocalUsedAfterInstruction(Block block, int startIndex, LocalVariable local, out bool usedByMemory)
    {
        var visited = new HashSet<(Block, int)>();

        bool ProcessBlock(Block currentBlock, int index, out bool usedByMemory2)
        {
            usedByMemory2 = false;

            var key = (currentBlock, index);

            if (!visited.Add(key))
                return false;

            // Process instructions
            for (var i = index; i < currentBlock.Instructions.Count; i++)
            {
                var instruction = currentBlock.Instructions[i];

                // Direct usage check
                if (instruction.Sources.Contains(local))
                    return true;

                // Used in memory operand
                foreach (var source in instruction.Sources)
                {
                    if (source is MemoryOperand memory)
                    {
                        if (memory.Base is LocalVariable memLocal && memLocal == local)
                        {
                            usedByMemory2 = true;
                            return true;
                        }

                        if (memory.Index is LocalVariable memLocal2 && memLocal2 == local)
                        {
                            usedByMemory2 = true;
                            return true;
                        }
                    }
                }
            }

            // Process successors
            foreach (var successor in currentBlock.Successors)
            {
                if (ProcessBlock(successor, 0, out usedByMemory2))
                    return true;
            }

            return false;
        }

        return ProcessBlock(block, startIndex, out usedByMemory);
    }
}
