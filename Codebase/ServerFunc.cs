using UnityEngine;
using UnityEngine.Rendering;

namespace Codebase;

public static class ServerFunc
{
	public static bool IsDedicatedServer()
	{
		return SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null;
	}
}
