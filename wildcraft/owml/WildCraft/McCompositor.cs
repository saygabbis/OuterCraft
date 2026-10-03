using UnityEngine;
using WildCraft;

namespace Tecno.WildCraft;

/// <summary>
/// DESIGN §9 hand + GUI layers: Minecraft renders its hand, hotbar, hearts, crosshair and every open screen onto
/// a transparent frame; this draws that frame over Outer Wilds' (premultiplied alpha, like Minecraft's blending).
/// </summary>
public class McCompositor : MonoBehaviour
{
	public static bool Enabled = true;

	Texture2D _tex;
	Material _mat;
	int _w, _h;
	ulong _lastFrame;
	bool _hasFrame;

	void OnDestroy()
	{
		if (_tex != null) Destroy(_tex);
		if (_mat != null) Destroy(_mat);
	}

	void Update()
	{
		if (!Enabled || !Bridge.HudVisible) { _hasFrame = false; return; }
		var link = Bridge.Link;
		if (link == null) return;
		if (!link.TryAcquireOverlay(out var w, out var h, out var bottomUp, out var rgba, out var frameId)) return;
		if (frameId == 0 || frameId == _lastFrame) return;
		_lastFrame = frameId;
		if (_tex == null || _w != w || _h != h)
		{
			if (_tex != null) Destroy(_tex);
			_tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
			_tex.filterMode = FilterMode.Bilinear;
			_tex.wrapMode = TextureWrapMode.Clamp;
			_w = w; _h = h;
		}
		if (!bottomUp) FlipRows(rgba, w, h);
		_tex.LoadRawTextureData(rgba);
		_tex.Apply(false, false);
		_hasFrame = true;
	}

	static byte[] _tmp;
	static void FlipRows(byte[] px, int w, int h)
	{
		int stride = w * 4;
		if (_tmp == null || _tmp.Length != stride) _tmp = new byte[stride];
		for (int y = 0; y < h / 2; y++)
		{
			System.Buffer.BlockCopy(px, y * stride, _tmp, 0, stride);
			System.Buffer.BlockCopy(px, (h - 1 - y) * stride, px, y * stride, stride);
			System.Buffer.BlockCopy(_tmp, 0, px, (h - 1 - y) * stride, stride);
		}
	}

	void OnGUI()
	{
		if (!Enabled || !_hasFrame || _tex == null || !Bridge.HudVisible) return;
		if (Event.current.type != EventType.Repaint) return;
		var rect = new Rect(0, 0, Screen.width, Screen.height);
		if (_mat == null)
		{
			var sh = Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default");
			if (sh != null) _mat = new Material(sh) { hideFlags = HideFlags.HideAndDontSave };
		}
		if (_mat != null) Graphics.DrawTexture(rect, _tex, new Rect(0f, 0f, 1f, 1f), 0, 0, 0, 0, Color.white, _mat);
		else GUI.DrawTexture(rect, _tex, ScaleMode.StretchToFill, true);
	}
}
