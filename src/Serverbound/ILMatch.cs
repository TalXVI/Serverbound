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
		internal static bool Plain(CodeInstruction code) => code.labels.Count == 0 && code.blocks.Count == 0;
		internal static bool Shape(List<CodeInstruction> code, int start, params Func<CodeInstruction, bool>[] pattern) =>
			start >= 0 && start + pattern.Length <= code.Count && Enumerable.Range(0, pattern.Length).All(i => pattern[i](code[start + i]));
		internal static Func<CodeInstruction, bool> Is(params OpCode[] opcodes) => code => opcodes.Contains(code.opcode);
		internal static Func<CodeInstruction, bool> Loads(FieldInfo field) => code => code.LoadsField(field);
		internal static Func<CodeInstruction, bool> Calls(MethodInfo method) => code => code.Calls(method);
		internal static Func<CodeInstruction, bool> Ldarg(int index) => code => code.IsLdarg(index);
	}
}
