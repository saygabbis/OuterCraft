using System;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using OWML.Common;
using UnityEngine;

namespace Tecno.WildCraft;

/// <summary>
/// DESIGN §7: while Minecraft drives, Outer Wilds must not also react to the keyboard and mouse.
/// One gate on the game's input reads blocks every gameplay action (move, look, jump, tools, jetpack...) at once;
/// Esc (pause) always passes, and G is fed in as OW's "interact" so you can still talk, sit and board.
/// </summary>
public static class GamePatches
{
	public static int Applied;

	public static void Apply(string id, IModConsole log)
	{
		var h = new Harmony(id);
		var gate = typeof(GamePatches);

		foreach (var m in typeof(OWInput).GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
		{
			string n = m.Name;
			if (n != "GetAxisValue" && n != "GetValue" && n != "IsPressed" && n != "IsNewlyPressed" && n != "IsNewlyReleased") continue;
			string pre = m.ReturnType == typeof(bool) ? nameof(PreBool) : m.ReturnType == typeof(float) ? nameof(PreFloat) : m.ReturnType == typeof(Vector2) ? nameof(PreVector2) : null;
			if (pre == null) continue;
			Patch(h, m, gate.GetMethod(pre, BindingFlags.Static | BindingFlags.NonPublic), log);
		}

		Patch(h, AccessTools.Method(typeof(PauseMenuManager), "TryOpenPauseMenu"), gate.GetMethod(nameof(PreOpenPause), BindingFlags.Static | BindingFlags.NonPublic), log);
		Patch(h, AccessTools.Method(typeof(PlayerResources), "OnImpact"), gate.GetMethod(nameof(PreSkipWhileActive), BindingFlags.Static | BindingFlags.NonPublic), log);
		Patch(h, AccessTools.Method(typeof(AlignPlayerWithForce), "ManagedFixedUpdate"), gate.GetMethod(nameof(PreSkipWhileRunning), BindingFlags.Static | BindingFlags.NonPublic), log);
		Patch(h, AccessTools.Method(typeof(PlayerCameraController), "UpdateRotation"), gate.GetMethod(nameof(PreSkipWhileRunning), BindingFlags.Static | BindingFlags.NonPublic), log);
		log.WriteLine("WildCraft: " + Applied + " game patches applied", MessageType.Info);
	}

	static void Patch(Harmony h, MethodBase target, MethodInfo prefix, IModConsole log)
	{
		if (target == null || prefix == null) { log.WriteLine("WildCraft: patch target missing for " + (prefix?.Name ?? "?"), MessageType.Warning); return; }
		try { h.Patch(target, prefix: new HarmonyMethod(prefix)); Applied++; }
		catch (Exception ex) { log.WriteLine("WildCraft: patch failed " + target.Name + ": " + ex.Message, MessageType.Warning); }
	}

	static bool Pass(object[] args, MethodBase original, out int interact)
	{
		interact = -1;
		if (!Bridge.InputGate) return true;
		object cmd = args != null && args.Length > 0 ? args[0] : null;
		try
		{
			if (ReferenceEquals(cmd, InputLibrary.pause) || ReferenceEquals(cmd, InputLibrary.escape)) return true;
			if (ReferenceEquals(cmd, InputLibrary.interact))
			{
				switch (original.Name)
				{
					case "IsNewlyPressed": interact = InputRouter.InteractPressed ? 1 : 0; break;
					case "IsNewlyReleased": interact = InputRouter.InteractReleased ? 1 : 0; break;
					default: interact = InputRouter.InteractHeld ? 1 : 0; break;
				}
			}
		}
		catch { return true; }
		return false;
	}

	static bool PreBool(object[] __args, MethodBase __originalMethod, ref bool __result)
	{
		if (Pass(__args, __originalMethod, out int interact)) return true;
		__result = interact == 1;
		return false;
	}

	static bool PreFloat(object[] __args, MethodBase __originalMethod, ref float __result)
	{
		if (Pass(__args, __originalMethod, out int interact)) return true;
		__result = interact == 1 ? 1f : 0f;
		return false;
	}

	static bool PreVector2(object[] __args, MethodBase __originalMethod, ref Vector2 __result)
	{
		if (Pass(__args, __originalMethod, out _)) return true;
		__result = Vector2.zero;
		return false;
	}

	/// <summary>Esc closes a Minecraft screen first; only then does it open Outer Wilds' menu.</summary>
	static bool PreOpenPause(ref bool __result)
	{
		if (Bridge.Active && Bridge.McScreenOpen) { __result = false; return false; }
		return true;
	}

	static bool PreSkipWhileActive() => !Bridge.Active;
	static bool PreSkipWhileRunning() => !Bridge.Running;
}
