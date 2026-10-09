#nullable disable
namespace Serverbound
{
	public class Utilities
	{
		public static bool IsDebugBuild()
		{
#if DEBUG
			return true;
#else
			return false;
#endif
		}
	}
}
