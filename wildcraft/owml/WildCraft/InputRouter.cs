using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;
using WildCraft;

namespace Tecno.WildCraft;

/// <summary>
/// DESIGN §7 InputBridge for Outer Wilds. Reads the game's own Input System devices and forwards keys, buttons,
/// scroll, cursor and text to Minecraft as SDL scancodes. Outer Wilds' gameplay actions are blocked by
/// <see cref="GamePatches"/> while Minecraft drives, except Esc (OW pause), and G, which stands in for OW's
/// "interact" (talk, sit, board) the way G is Skyrim's activate in SkyCraft.
/// </summary>
public static class InputRouter
{
	static readonly List<(Key key, ushort sdl)> Keys = BuildKeys();
	static readonly bool[] Down = new bool[512];
	static readonly bool[] MouseDown = new bool[8];
	static bool _eForOw;
	static bool _textHooked;
	static Keyboard _hookedKeyboard;

	/// <summary>G held / pressed this frame: Outer Wilds' interact while Minecraft has the controls.</summary>
	public static bool InteractHeld, InteractPressed, InteractReleased;
	public static float MouseDx, MouseDy;
	public static bool EscPressed;

	static List<(Key, ushort)> BuildKeys()
	{
		var l = new List<(Key, ushort)>();
		for (int i = 0; i < 26; i++) l.Add(((Key)((int)Key.A + i), (ushort)(4 + i)));
		for (int i = 0; i < 9; i++) l.Add(((Key)((int)Key.Digit1 + i), (ushort)(30 + i)));
		l.Add((Key.Digit0, 39));
		l.Add((Key.Enter, 40)); l.Add((Key.Escape, 41)); l.Add((Key.Backspace, 42)); l.Add((Key.Tab, 43)); l.Add((Key.Space, 44));
		l.Add((Key.Minus, 45)); l.Add((Key.Equals, 46)); l.Add((Key.LeftBracket, 47)); l.Add((Key.RightBracket, 48));
		l.Add((Key.Backslash, 49)); l.Add((Key.Semicolon, 51)); l.Add((Key.Quote, 52)); l.Add((Key.Backquote, 53));
		l.Add((Key.Comma, 54)); l.Add((Key.Period, 55)); l.Add((Key.Slash, 56));
		for (int i = 0; i < 12; i++) if (i != 10 && i != 7) l.Add(((Key)((int)Key.F1 + i), (ushort)(58 + i)));   // F11 would take the hidden MC window fullscreen; F8 is the host hand-over key
		l.Add((Key.Insert, 73)); l.Add((Key.Home, 74)); l.Add((Key.PageUp, 75)); l.Add((Key.Delete, 76));
		l.Add((Key.End, 77)); l.Add((Key.PageDown, 78));
		l.Add((Key.RightArrow, 79)); l.Add((Key.LeftArrow, 80)); l.Add((Key.DownArrow, 81)); l.Add((Key.UpArrow, 82));
		// Ctrl sneaks and Shift sprints (Minecraft's defaults are the other way round): swap what Minecraft is told.
		l.Add((Key.LeftCtrl, 225)); l.Add((Key.LeftShift, 224)); l.Add((Key.LeftAlt, 226));
		l.Add((Key.RightCtrl, 229)); l.Add((Key.RightShift, 228)); l.Add((Key.RightAlt, 230));
		return l;
	}

	public static void Reset(HostLink link)
	{
		for (int i = 0; i < Down.Length; i++) Down[i] = false;
		for (int i = 0; i < MouseDown.Length; i++) MouseDown[i] = false;
		InteractHeld = InteractPressed = InteractReleased = false;
		EscPressed = false;
		try { link?.PushInput(Proto.InReleaseAll, 0); } catch { /* */ }
	}

	static void HookText(HostLink link)
	{
		var kb = Keyboard.current;
		if (kb == null || (_textHooked && ReferenceEquals(_hookedKeyboard, kb))) return;
		_textHooked = true;
		_hookedKeyboard = kb;
		kb.onTextInput += c =>
		{
			if (Bridge.Active && Bridge.McScreenOpen && !Bridge.OwPauseOpen && Bridge.Link != null && c >= ' ' && c != 127)
				Bridge.Link.PushInput(Proto.InText, 0, c);
		};
	}

	/// <summary>Called once per rendered frame while Minecraft is in charge.</summary>
	public static void Tick(HostLink link, bool owPauseOpen, bool mcScreenOpen, float overlayW, float overlayH)
	{
		InteractPressed = InteractReleased = false;
		EscPressed = false;
		MouseDx = MouseDy = 0f;
		var kb = Keyboard.current;
		var mouse = Mouse.current;
		if (link == null || kb == null) return;
		HookText(link);

		bool focused = Application.isFocused;
		if (!focused || owPauseOpen)
		{
			// Outer Wilds' pause menu (or another window) has the keyboard.
			ReleaseAllHeld(link);
			bool gDown = false;
			InteractReleased = InteractHeld;
			InteractHeld = gDown;
			return;
		}

		// Host-owned keys.
		// E is Minecraft's inventory, but with an Outer Wilds prompt in front of the crosshair (a hatch, the campfire,
		// someone to talk to) it is Outer Wilds' interact instead: the nearer thing wins, like SkyCraft's activate.
		if (kb.eKey.wasPressedThisFrame && !mcScreenOpen && Bridge.OwHasInteractible) _eForOw = true;
		if (!kb.eKey.isPressed) _eForOw = false;
		bool g = kb.gKey.isPressed || _eForOw;
		InteractPressed = g && !InteractHeld;
		InteractReleased = !g && InteractHeld;
		InteractHeld = g;
		if (kb.oKey.wasPressedThisFrame && !mcScreenOpen) link.PushInput(Proto.InOpenMenu, 0);
		if (kb.escapeKey.wasPressedThisFrame) EscPressed = true;

		foreach (var (key, sdl) in Keys)
		{
			if (key == Key.G || key == Key.O || (key == Key.E && _eForOw)) { SetKey(link, sdl, false); continue; }
			if (key == Key.Escape && !mcScreenOpen) { SetKey(link, sdl, false); continue; }
			bool down;
			try { down = kb[key].isPressed; } catch { down = false; }
			SetKey(link, sdl, down);
		}

		if (mouse == null) return;
		Vector2 delta = mouse.delta.ReadValue();
		MouseDx = delta.x;
		MouseDy = -delta.y;                 // Input System: up is positive; Minecraft: down is positive
		if (mcScreenOpen)
		{
			var p = mouse.position.ReadValue();
			float sw = Mathf.Max(1, Screen.width), sh = Mathf.Max(1, Screen.height);
			link.PushInput(Proto.InCursor, 0, Mathf.RoundToInt(p.x * overlayW / sw), Mathf.RoundToInt((sh - p.y) * overlayH / sh));
		}
		SetMouse(link, 1, mouse.leftButton.isPressed);
		SetMouse(link, 3, mouse.rightButton.isPressed);
		SetMouse(link, 2, mouse.middleButton.isPressed);
		float sy = mouse.scroll.ReadValue().y;
		if (sy != 0f)
		{
			// Windows reports 120 per notch, some devices 1.
			int notches = Mathf.Abs(sy) >= 60f ? Mathf.RoundToInt(sy / 120f) : (sy > 0f ? 1 : -1);
			if (notches == 0) notches = sy > 0f ? 1 : -1;
			link.PushInput(Proto.InScroll, 0, notches * 120);
		}
	}

	static void SetKey(HostLink link, ushort sdl, bool down)
	{
		if (Down[sdl] == down) return;
		Down[sdl] = down;
		link.PushInput(Proto.InKey, sdl, down ? 1 : 0);
	}

	static void SetMouse(HostLink link, int button, bool down)
	{
		if (MouseDown[button] == down) return;
		MouseDown[button] = down;
		link.PushInput(Proto.InMouseButton, (ushort)button, down ? 1 : 0);
	}

	static void ReleaseAllHeld(HostLink link)
	{
		bool any = false;
		for (int i = 0; i < Down.Length; i++) if (Down[i]) { Down[i] = false; any = true; }
		for (int i = 0; i < MouseDown.Length; i++) if (MouseDown[i]) { MouseDown[i] = false; any = true; }
		if (any) link.PushInput(Proto.InReleaseAll, 0);
	}
}
