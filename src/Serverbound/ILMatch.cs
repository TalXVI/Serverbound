#nullable disable
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;

namespace Serverbound
{
	// Declarative, fail-closed IL matching shared by the transpilers.
	internal static class ILMatch
	{
		private static readonly OpCode[] localLoads = { OpCodes.Ldloc_0, OpCodes.Ldloc_1, OpCodes.Ldloc_2, OpCodes.Ldloc_3 };

		internal static int Site(List<CodeInstruction> code, Func<int, bool> matches)
		{
			int[] sites = Enumerable.Range(0, code.Count).Where(matches).ToArray();
			return sites.Length == 1 && Plain(code[sites[0]]) ? sites[0] : -1;
		}

		internal static bool Plain(CodeInstruction code) => code.labels.Count == 0 && code.blocks.Count == 0;
		internal static bool Shape(List<CodeInstruction> code, int start, params Func<CodeInstruction, bool>[] pattern) =>
			start >= 0 && start + pattern.Length <= code.Count && Enumerable.Range(0, pattern.Length).All(i => pattern[i](code[start + i]));
		internal static Func<CodeInstruction, bool> Is(params OpCode[] opcodes) => code => opcodes.Contains(code.opcode);
		internal static Func<CodeInstruction, bool> Loads(FieldInfo field) => code => code.LoadsField(field);
		internal static Func<CodeInstruction, bool> Calls(MethodInfo method) => code => code.Calls(method);
		internal static Func<CodeInstruction, bool> Ldarg(int index) => code => code.IsLdarg(index);
		internal static Func<CodeInstruction, bool> IsLoad(int index) => code => index < localLoads.Length && code.opcode == localLoads[index]
			|| (code.opcode == OpCodes.Ldloc || code.opcode == OpCodes.Ldloc_S) && Local(code) == index;
		internal static Func<CodeInstruction, bool> IsAddress(int index) => code =>
			(code.opcode == OpCodes.Ldloca || code.opcode == OpCodes.Ldloca_S) && Local(code) == index;
		private static int Local(CodeInstruction code) => code.operand is LocalVariableInfo local ? local.LocalIndex : Convert.ToInt32(code.operand);

		internal static bool Locals(MethodBase method, int count, params (int Index, Type Type)[] expected)
		{
			IList<LocalVariableInfo> locals = method.GetMethodBody().LocalVariables;
			return locals.Count == count && expected.All(local => locals[local.Index].LocalType == local.Type);
		}

		static internal bool Targets(CodeInstruction branch, CodeInstruction destination)
		{
			return branch.operand is Label label && destination.labels.Contains(label);
		}
	}
}
