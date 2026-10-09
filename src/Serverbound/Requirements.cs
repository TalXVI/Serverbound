#nullable disable
using Serverbound.Patching;
using System;
using Utilities = Serverbound.Utilities;
namespace Serverbound.Requirements
{
	public class PatchRequirement
	{
		public class DebugBuild : IPatchRequirement
		{
			public const string name = "DebugBuild";

			string IPatchRequirement.Name => name;

			Func<bool> IPatchRequirement.Checker => Utilities.IsDebugBuild;
		}
	}
}
