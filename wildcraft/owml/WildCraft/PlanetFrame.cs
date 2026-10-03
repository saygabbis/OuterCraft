using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;

namespace Tecno.WildCraft;

/// <summary>
/// Where Minecraft's flat, Y-up world sits on a round Outer Wilds body (DESIGN §4, adapted for planets).
///
/// Minecraft x/z become an azimuthal-equidistant map of the planet's surface around an anchor point
/// (exact near the player, so walking, collision and block grids stay Minecraft-sized), and Minecraft y
/// is height above the surface along the radial. "Down" in Minecraft is therefore toward the planet's
/// centre, which is where Outer Wilds' gravity points as well. The mapping is fixed per body and kept
/// in the body's own coordinates, so placed blocks stay put while the planet spins and orbits.
/// </summary>
public sealed class PlanetFrame
{
	public const float OriginY = 100f;      // Minecraft y of the surface at the anchor
	public const float OriginZ = 0f;
	const float MaxAngle = 2.8f;            // stay clear of the antipode, where the map folds

	public string Name;
	public int Slot;                         // gives each body its own stretch of Minecraft x
	public float OriginX;
	public Vector3 AnchorDir;                // unit, in the body's local space
	public float R0;                         // body-local distance from the centre to the anchor
	public float MetersPerBlock = 1f;

	public Transform Body;                   // runtime only
	Vector3 _e1, _e2;                        // tangent basis at the anchor, body-local
	Vector3 _center;                         // body-local centre (0 unless the pivot is off-centre)

	public static float SlotOriginX(int slot) => 200000f + slot * 20000f;

	public PlanetFrame Init(Transform body)
	{
		Body = body;
		AnchorDir.Normalize();
		OriginX = SlotOriginX(Slot);
		// A stable basis for the tangent plane: north is the body's own +Y projected onto it.
		Vector3 north = Vector3.up;
		if (Mathf.Abs(Vector3.Dot(north, AnchorDir)) > 0.97f) north = Vector3.forward;
		Vector3 f = Vector3.ProjectOnPlane(north, AnchorDir).normalized;     // "forward" at the anchor
		Vector3 e = Vector3.Cross(AnchorDir, f);                              // Unity: up x forward = right
		_e1 = e;     // Minecraft +x = east = right
		_e2 = -f;    // Minecraft +z = south = back
		return this;
	}

	/// <summary>Minecraft position to the body's local space.</summary>
	public Vector3 McToLocal(double x, double y, double z)
	{
		float dx = (float)(x - OriginX) * MetersPerBlock;
		float dz = (float)(z - OriginZ) * MetersPerBlock;
		float d = Mathf.Sqrt(dx * dx + dz * dz);
		float alpha = Mathf.Min(d / R0, MaxAngle);
		Vector3 t = d > 1e-6f ? (_e1 * dx + _e2 * dz) / d : _e1;
		Vector3 dir = AnchorDir * Mathf.Cos(alpha) + t * Mathf.Sin(alpha);
		float r = R0 + (float)(y - OriginY) * MetersPerBlock;
		return _center + dir * r;
	}

	public Vector3 McToWorld(double x, double y, double z) => Body.TransformPoint(McToLocal(x, y, z));

	/// <summary>Body-local position to Minecraft coordinates.</summary>
	public Vector3 LocalToMc(Vector3 local)
	{
		Vector3 p = local - _center;
		float r = p.magnitude;
		if (r < 1e-3f) return new Vector3(OriginX, OriginY, OriginZ);
		Vector3 dir = p / r;
		float c = Mathf.Clamp(Vector3.Dot(dir, AnchorDir), -1f, 1f);
		float alpha = Mathf.Acos(c);
		Vector3 t = dir - AnchorDir * c;
		float tl = t.magnitude;
		float d = alpha * R0;
		float ux = 0f, uz = 0f;
		if (tl > 1e-6f)
		{
			t /= tl;
			ux = Vector3.Dot(t, _e1);
			uz = Vector3.Dot(t, _e2);
		}
		return new Vector3(OriginX + d * ux / MetersPerBlock, OriginY + (r - R0) / MetersPerBlock, OriginZ + d * uz / MetersPerBlock);
	}

	public Vector3 WorldToMc(Vector3 world) => LocalToMc(Body.InverseTransformPoint(world));

	/// <summary>Radial "up" in world space at a Minecraft position.</summary>
	public Vector3 UpAt(double x, double z)
	{
		Vector3 a = McToWorld(x, OriginY, z);
		Vector3 b = McToWorld(x, OriginY + 1.0, z);
		return (b - a).normalized;
	}

	/// <summary>World directions of Minecraft's +x (east), +y (up) and +z (south) at a position.</summary>
	public void AxesAt(double x, double y, double z, out Vector3 ex, out Vector3 ey, out Vector3 ez)
	{
		const double e = 0.5;
		ey = (McToWorld(x, y + e, z) - McToWorld(x, y - e, z)).normalized;
		ex = Vector3.ProjectOnPlane(McToWorld(x + e, y, z) - McToWorld(x - e, y, z), ey).normalized;
		// Unity is left-handed: right x up = forward (north). Minecraft +z is south.
		ez = -Vector3.Cross(ex, ey);
	}

	/// <summary>
	/// A world-space look direction for Minecraft yaw/pitch (degrees) at a Minecraft position.
	/// Minecraft yaw 0 faces +z (south), 90 faces -x (west); pitch is positive looking down.
	/// </summary>
	public Quaternion LookRotation(float yaw, float pitch, double x, double y, double z)
	{
		AxesAt(x, y, z, out var ex, out var ey, out var ez);
		float yr = yaw * Mathf.Deg2Rad, pr = pitch * Mathf.Deg2Rad;
		Vector3 horiz = ex * (-Mathf.Sin(yr)) + ez * Mathf.Cos(yr);
		Vector3 look = horiz * Mathf.Cos(pr) - ey * Mathf.Sin(pr);
		// The camera's up is perpendicular to the look direction (tilted with it), so looking straight up or
		// down never degenerates into a flipped view.
		Vector3 up = ey * Mathf.Cos(pr) + horiz * Mathf.Sin(pr);
		return Quaternion.LookRotation(look, up);
	}

	/// <summary>Minecraft yaw/pitch that make the camera face a world direction at a Minecraft position.</summary>
	public void YawPitchFor(Vector3 worldForward, double x, double y, double z, out float yaw, out float pitch)
	{
		AxesAt(x, y, z, out var ex, out var ey, out var ez);
		Vector3 h = Vector3.ProjectOnPlane(worldForward, ey);
		float up = Vector3.Dot(worldForward, ey);
		pitch = -Mathf.Asin(Mathf.Clamp(up, -1f, 1f)) * Mathf.Rad2Deg;
		if (h.sqrMagnitude < 1e-6f) { yaw = 0f; return; }
		h.Normalize();
		float hx = Vector3.Dot(h, ex), hz = Vector3.Dot(h, ez);
		yaw = Mathf.Atan2(-hx, hz) * Mathf.Rad2Deg;
	}
}

/// <summary>
/// A sphere cannot be drawn flat without stretching, and the farther from a chart's anchor, the worse it gets (on a
/// planet only a couple of hundred metres across, quickly). So a planet has several charts: wherever the player gets
/// more than <see cref="ChartRadius"/> blocks from the nearest chart's anchor, a new chart is anchored right there
/// (or an earlier nearby one is reused). Each chart has its own stretch of Minecraft x, is nearly undistorted within
/// its radius, and persists (frames.txt) so what was built stays where it was built.
/// </summary>
public static class FrameStore
{
	public const float ChartRadius = 60f;

	static readonly List<PlanetFrame> All = new List<PlanetFrame>();
	static string _path;
	static bool _loaded;

	public static string Dir;

	static void Load()
	{
		if (_loaded) return;
		_loaded = true;
		_path = Path.Combine(Dir ?? ".", "frames.txt");
		try
		{
			if (!File.Exists(_path)) return;
			foreach (var line in File.ReadAllLines(_path))
			{
				var p = line.Split('|');
				if (p.Length < 7) continue;
				All.Add(new PlanetFrame
				{
					Name = p[0],
					Slot = int.Parse(p[1]),
					AnchorDir = new Vector3(Parse(p[2]), Parse(p[3]), Parse(p[4])),
					R0 = Parse(p[5]),
					MetersPerBlock = Parse(p[6]),
				});
			}
		}
		catch { /* start fresh */ }
	}

	static float Parse(string s) => float.Parse(s, System.Globalization.CultureInfo.InvariantCulture);
	static string Fmt(float f) => f.ToString("R", System.Globalization.CultureInfo.InvariantCulture);

	static void Save()
	{
		try
		{
			var sb = new StringBuilder();
			foreach (var f in All)
				sb.AppendLine(string.Join("|", f.Name, f.Slot, Fmt(f.AnchorDir.x), Fmt(f.AnchorDir.y), Fmt(f.AnchorDir.z), Fmt(f.R0), Fmt(f.MetersPerBlock)));
			File.WriteAllText(_path, sb.ToString());
		}
		catch { /* not fatal */ }
	}

	/// <summary>Connects every stored chart of a planet to its body, so their blocks can be drawn on it.</summary>
	public static void Bind(string name, Transform body, float metersPerBlock)
	{
		Load();
		foreach (var f in All)
			if (f.Name == name) { f.MetersPerBlock = metersPerBlock; f.Init(body); }
	}

	/// <summary>Blocks along the surface between a body-local direction and a chart's anchor.</summary>
	static float Distance(PlanetFrame f, Vector3 localDir, float metersPerBlock)
		=> Mathf.Acos(Mathf.Clamp(Vector3.Dot(localDir.normalized, f.AnchorDir.normalized), -1f, 1f)) * f.R0 / metersPerBlock;

	/// <summary>The chart to use at a place on a body: the nearest existing one within range, otherwise a new one anchored there.</summary>
	public static PlanetFrame For(string name, Transform body, Vector3 groundWorld, float metersPerBlock)
	{
		Load();
		Vector3 local = body.InverseTransformPoint(groundWorld);
		PlanetFrame best = null; float bestD = float.MaxValue;
		foreach (var f in All)
		{
			if (f.Name != name) continue;
			float d = Distance(f, local, metersPerBlock);
			if (d < bestD) { bestD = d; best = f; }
		}
		if (best == null || bestD > ChartRadius)
		{
			int slot = 0;
			var used = new HashSet<int>();
			foreach (var o in All) used.Add(o.Slot);
			while (used.Contains(slot)) slot++;
			best = new PlanetFrame { Name = name, Slot = slot, AnchorDir = local.normalized, R0 = local.magnitude, MetersPerBlock = metersPerBlock };
			All.Add(best);
			Save();
		}
		best.MetersPerBlock = metersPerBlock;
		return best.Init(body);
	}

	/// <summary>How far (blocks) a place is from the chart's anchor, for deciding when to change chart.</summary>
	public static float DistanceFromAnchor(PlanetFrame f, Vector3 worldPoint)
		=> Distance(f, f.Body.InverseTransformPoint(worldPoint), f.MetersPerBlock);

	/// <summary>The chart whose stretch of Minecraft x contains this coordinate (null if unknown).</summary>
	public static PlanetFrame ForMcX(double x)
	{
		Load();
		foreach (var f in All)
			if (x >= f.OriginX - 10000 && x < f.OriginX + 10000 && f.Body != null) return f;
		return null;
	}
}
