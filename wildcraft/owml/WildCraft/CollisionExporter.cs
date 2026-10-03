using System;
using System.Collections.Generic;
using UnityEngine;
using WildCraft;

namespace Tecno.WildCraft;

/// <summary>
/// DESIGN §5 WorldExporter (stage A) for Outer Wilds: ray-casts the planet around the player and streams it to
/// Minecraft as exact triangles (for the player's smooth collider) plus 1/8-block voxels (for everything else),
/// in 8x8x8 regions, nearest first. Every column of ground is described in Minecraft coordinates, so Minecraft's
/// own physics then run on Outer Wilds' terrain: walking, jumping, stairs, slopes, walls.
/// </summary>
public sealed class CollisionExporter
{
	const int Region = 8;
	const int Cells = 16;                 // cells per region side (0.5 block each)
	const int Samples = Cells + 1;
	const int MaxLayers = 6;
	const float Step = Region / (float)Cells;
	const float MaxStep = 1.25f;          // heights this close are one surface; further apart are a wall
	const float RayTop = 40f;             // start of each ray above the player's feet
	const float RayLength = 320f;         // the ground can be far below: a player gliding on an elytra
	const int ViewRadius = 3;             // regions around the player kept described
	const float NearRefresh = 2.5f;       // seconds before a nearby column is re-sampled (ships, doors, ...)
	const float FarRefresh = 12f;

	readonly HostLink _link;
	PlanetFrame _frame;
	uint _epoch;
	Transform _player;
	int _mask;
	readonly Dictionary<long, float> _sent = new Dictionary<long, float>();
	readonly HashSet<long> _empty = new HashSet<long>();
	static readonly RaycastHit[] Hits = new RaycastHit[40];

	// Per column scratch.
	readonly float[] _h = new float[Samples * Samples * MaxLayers];
	readonly int[] _n = new int[Samples * Samples];

	public int ColumnsSent { get; private set; }
	public long RaysCast { get; private set; }
	public bool CenterReady { get; private set; }

	public CollisionExporter(HostLink link) { _link = link; }

	public void Begin(PlanetFrame frame, uint epoch, Transform playerRoot)
	{
		_frame = frame;
		_epoch = epoch;
		_player = playerRoot;
		_sent.Clear();
		_empty.Clear();
		CenterReady = false;
		try { _mask = OWLayerMask.physicalMask; }
		catch { _mask = ~0; }
		if (_mask == 0) _mask = ~0;
		_link.WriteCollision(Proto.ColClear, CollisionMsg.Clear(epoch));
	}

	static long Key(int rx, int rz) => ((long)rx << 32) ^ (uint)rz;

	/// <summary>Sends a few more regions each frame, most important first.</summary>
	public void Tick(double mcX, double mcY, double mcZ, float now, int budget)
	{
		if (_frame == null) return;
		int prx = Mathf.FloorToInt((float)mcX / Region), prz = Mathf.FloorToInt((float)mcZ / Region);
		for (int b = 0; b < budget; b++)
		{
			long bestKey = 0; int bestX = 0, bestZ = 0; float bestScore = float.MaxValue; bool found = false;
			for (int dz = -ViewRadius; dz <= ViewRadius; dz++)
			for (int dx = -ViewRadius; dx <= ViewRadius; dx++)
			{
				int rx = prx + dx, rz = prz + dz;
				long k = Key(rx, rz);
				float dist = Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) + 0.01f * (dx * dx + dz * dz);
				float score;
				if (!_sent.TryGetValue(k, out var t)) score = dist - 1000f;                     // never described: first
				else
				{
					float age = now - t;
					float need = _empty.Contains(k) ? 0.25f : Mathf.Max(Mathf.Abs(dx), Mathf.Abs(dz)) <= 1 ? NearRefresh : FarRefresh;   // no ground found yet: look again soon
					if (age < need) continue;
					score = dist - age;                                                           // stale: oldest/nearest
				}
				if (score < bestScore) { bestScore = score; bestKey = k; bestX = rx; bestZ = rz; found = true; }
			}
			if (!found) return;
			try
			{
				if (ExportColumn(bestX, bestZ, (float)mcY)) _empty.Remove(bestKey); else _empty.Add(bestKey);
				_sent[bestKey] = now;
				ColumnsSent++;
				if (bestX == prx && bestZ == prz) CenterReady = true;
			}
			catch (InvalidOperationException) { return; }   // ring full: Minecraft will catch up
		}
	}

	bool IsPlayer(Collider c)
	{
		if (c == null) return true;
		if (c.isTrigger) return true;
		var t = c.transform;
		return _player != null && t.IsChildOf(_player);
	}

	void Sample(int rx, int rz, float refY)
	{
		Vector3 center = _frame.Body.position;
		for (int j = 0; j < Samples; j++)
		for (int i = 0; i < Samples; i++)
		{
			double x = rx * Region + i * Step, z = rz * Region + j * Step;
			Vector3 start = _frame.McToWorld(x, refY + RayTop, z);
			Vector3 dir = (center - start).normalized;
			int n = Physics.RaycastNonAlloc(new Ray(start, dir), Hits, RayLength, _mask, QueryTriggerInteraction.Ignore);
			RaysCast++;
			int idx = j * Samples + i;
			int cnt = 0;
			for (int k = 0; k < n && cnt < MaxLayers; k++)
			{
				if (IsPlayer(Hits[k].collider)) continue;
				Vector3 local = _frame.Body.InverseTransformPoint(Hits[k].point);
				float y = PlanetFrame.OriginY + (local.magnitude - _frame.R0) / _frame.MetersPerBlock;
				// keep layers sorted high to low, drop near-duplicates
				int pos = cnt;
				while (pos > 0 && _h[idx * MaxLayers + pos - 1] < y) { _h[idx * MaxLayers + pos] = _h[idx * MaxLayers + pos - 1]; pos--; }
				if (pos > 0 && Mathf.Abs(_h[idx * MaxLayers + pos - 1] - y) < 0.04f) { for (int m = pos; m < cnt; m++) _h[idx * MaxLayers + m] = _h[idx * MaxLayers + m + 1]; continue; }
				if (pos < cnt && Mathf.Abs(_h[idx * MaxLayers + pos] - y) < 0.04f) continue;
				_h[idx * MaxLayers + pos] = y;
				cnt++;
			}
			_n[idx] = cnt;
		}
	}

	int Match(int idx, float h)
	{
		int best = -1; float bd = MaxStep;
		for (int k = 0; k < _n[idx]; k++)
		{
			float d = Mathf.Abs(_h[idx * MaxLayers + k] - h);
			if (d <= bd) { bd = d; best = k; }
		}
		return best;
	}

	int NextHigher(int idx, float h)
	{
		int best = -1; float bd = float.MaxValue;
		for (int k = 0; k < _n[idx]; k++)
		{
			float v = _h[idx * MaxLayers + k];
			if (v > h + MaxStep * 0.999f && v - h < bd) { bd = v - h; best = k; }
		}
		return best;
	}

	sealed class RegionBuild
	{
		public List<float[]> Tris = new List<float[]>();
		public Dictionary<long, ulong[]> Blocks = new Dictionary<long, ulong[]>();
	}

	static long BlockKey(int x, int y, int z) => ((long)(x & 0x1FFFFF)) | (((long)(y & 0x1FFFFF)) << 21) | (((long)(z & 0x1FFFFF)) << 42);

	bool ExportColumn(int rx, int rz, float playerY)
	{
		Sample(rx, rz, playerY);
		var regions = new Dictionary<int, RegionBuild>();
		float minH = float.MaxValue, maxH = float.MinValue;
		for (int i = 0; i < _n.Length; i++)
			for (int k = 0; k < _n[i]; k++)
			{
				float v = _h[i * MaxLayers + k];
				if (v < minH) minH = v;
				if (v > maxH) maxH = v;
			}
		bool anyGround = minH <= maxH;
		if (minH > maxH) { minH = maxH = playerY - 8f; }   // nothing hit: still describe the (empty) space

		float x0 = rx * Region, z0 = rz * Region;

		// Floors: connect matching layers of neighbouring samples.
		for (int j = 0; j < Cells; j++)
		for (int i = 0; i < Cells; i++)
		{
			int a = j * Samples + i, b = a + 1, c = a + Samples + 1, d = a + Samples;
			for (int ka = 0; ka < _n[a]; ka++)
			{
				float ha = _h[a * MaxLayers + ka];
				int kb = Match(b, ha), kc = Match(c, ha), kd = Match(d, ha);
				if (kb < 0 || kc < 0 || kd < 0) continue;
				float hb = _h[b * MaxLayers + kb], hc = _h[c * MaxLayers + kc], hd = _h[d * MaxLayers + kd];
				float xa = x0 + i * Step, za = z0 + j * Step, xb = xa + Step, zd = za + Step;
				AddTri(regions, xa, ha, za, xb, hb, za, xb, hc, zd, true);
				AddTri(regions, xa, ha, za, xb, hc, zd, xa, hd, zd, true);
			}
		}

		// Cliffs: where a surface ends against a higher one, stand a wall between them.
		for (int j = 0; j < Samples; j++)
		for (int i = 0; i < Samples; i++)
		{
			int p = j * Samples + i;
			if (i + 1 < Samples) Walls(regions, p, p + 1, x0 + (i + 0.5f) * Step, z0 + j * Step, true);
			if (j + 1 < Samples) Walls(regions, p, p + Samples, x0 + i * Step, z0 + (j + 0.5f) * Step, false);
		}

		int ry0 = Mathf.FloorToInt(minH / Region) - 2, ry1 = Mathf.FloorToInt(maxH / Region) + 2;
		for (int ry = ry0; ry <= ry1; ry++)
		{
			regions.TryGetValue(ry, out var rb);
			var tris = rb != null ? rb.Tris : new List<float[]>();
			var blocks = new List<KeyValuePair<long[], ulong[]>>();
			if (rb != null)
				foreach (var kv in rb.Blocks)
				{
					int bx = (int)(kv.Key & 0x1FFFFF), by = (int)((kv.Key >> 21) & 0x1FFFFF), bz = (int)((kv.Key >> 42) & 0x1FFFFF);
					bx = (bx << 11) >> 11; by = (by << 11) >> 11; bz = (bz << 11) >> 11;     // sign-extend 21 bits
					blocks.Add(new KeyValuePair<long[], ulong[]>(new long[] { bx, by, bz }, kv.Value));
				}
			int mx = rx * Region, my = ry * Region, mz = rz * Region;
			_link.WriteCollision(Proto.ColTris, CollisionMsg.Tris(mx, my, mz, mx + Region - 1, my + Region - 1, mz + Region - 1, _epoch, tris));
			_link.WriteCollision(Proto.ColRegion, CollisionMsg.Region(mx, my, mz, mx + Region - 1, my + Region - 1, mz + Region - 1, _epoch, blocks));
		}
		return anyGround;
	}

	void Walls(Dictionary<int, RegionBuild> regions, int p, int q, float xm, float zm, bool alongX)
	{
		for (int side = 0; side < 2; side++)
		{
			int lowIdx = side == 0 ? p : q, highIdx = side == 0 ? q : p;
			for (int k = 0; k < _n[lowIdx]; k++)
			{
				float hl = _h[lowIdx * MaxLayers + k];
				if (Match(highIdx, hl) >= 0) continue;           // continuous: no wall
				int kh = NextHigher(highIdx, hl);
				if (kh < 0) continue;                            // the other side drops away: an edge, not a wall
				float hh = _h[highIdx * MaxLayers + kh];
				if (hh - hl > 60f) hh = hl + 60f;
				// a vertical quad across the gap, half a cell wide either way
				float half = Step * 0.5f;
				if (alongX)
				{
					AddTri(regions, xm, hl, zm - half, xm, hl, zm + half, xm, hh, zm + half, false);
					AddTri(regions, xm, hl, zm - half, xm, hh, zm + half, xm, hh, zm - half, false);
				}
				else
				{
					AddTri(regions, xm - half, hl, zm, xm + half, hl, zm, xm + half, hh, zm, false);
					AddTri(regions, xm - half, hl, zm, xm + half, hh, zm, xm - half, hh, zm, false);
				}
			}
		}
	}

	void AddTri(Dictionary<int, RegionBuild> regions, float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz, bool floor)
	{
		float lo = Mathf.Min(ay, Mathf.Min(by, cy)), hi = Mathf.Max(ay, Mathf.Max(by, cy));
		int r0 = Mathf.FloorToInt(lo / Region), r1 = Mathf.FloorToInt(hi / Region);
		var tri = new[] { ax, ay, az, bx, by, bz, cx, cy, cz };
		for (int ry = r0; ry <= r1; ry++)
		{
			if (!regions.TryGetValue(ry, out var rb)) regions[ry] = rb = new RegionBuild();
			rb.Tris.Add(tri);
		}
		Voxelize(regions, ax, ay, az, bx, by, bz, cx, cy, cz, floor);
	}

	/// <summary>Marks the 1/8-block voxels a triangle passes through (floors get half a block of depth).</summary>
	void Voxelize(Dictionary<int, RegionBuild> regions, float ax, float ay, float az, float bx, float by, float bz, float cx, float cy, float cz, bool floor)
	{
		float minX = Mathf.Min(ax, Mathf.Min(bx, cx)), maxX = Mathf.Max(ax, Mathf.Max(bx, cx));
		float minZ = Mathf.Min(az, Mathf.Min(bz, cz)), maxZ = Mathf.Max(az, Mathf.Max(bz, cz));
		int sx0 = Mathf.FloorToInt(minX * 8f), sx1 = Mathf.FloorToInt(maxX * 8f);
		int sz0 = Mathf.FloorToInt(minZ * 8f), sz1 = Mathf.FloorToInt(maxZ * 8f);
		float det = (bz - cz) * (ax - cx) + (cx - bx) * (az - cz);
		if (Mathf.Abs(det) < 1e-9f)
		{
			// a vertical sliver: fill the span straight down its footprint
			int gy0 = Mathf.FloorToInt(Mathf.Min(ay, Mathf.Min(by, cy)) * 8f), gy1 = Mathf.FloorToInt(Mathf.Max(ay, Mathf.Max(by, cy)) * 8f);
			for (int sx = sx0; sx <= sx1; sx++)
			for (int sz = sz0; sz <= sz1; sz++)
				for (int gy = gy0; gy <= gy1; gy++) Fill(regions, sx, gy, sz);
			return;
		}
		for (int sx = sx0; sx <= sx1; sx++)
		for (int sz = sz0; sz <= sz1; sz++)
		{
			float px = (sx + 0.5f) / 8f, pz = (sz + 0.5f) / 8f;
			float l1 = ((bz - cz) * (px - cx) + (cx - bx) * (pz - cz)) / det;
			float l2 = ((cz - az) * (px - cx) + (ax - cx) * (pz - cz)) / det;
			float l3 = 1f - l1 - l2;
			const float e = -0.12f;     // a little generous, so no voxel column is skipped between neighbouring triangles
			if (l1 < e || l2 < e || l3 < e) continue;
			float h = l1 * ay + l2 * by + l3 * cy;
			int top = Mathf.FloorToInt(h * 8f);
			int bottom = floor ? top - 3 : top - 1;
			for (int gy = bottom; gy <= top; gy++) Fill(regions, sx, gy, sz);
		}
	}

	void Fill(Dictionary<int, RegionBuild> regions, int sx, int gy, int sz)
	{
		int bx = Mathf.FloorToInt(sx / 8f), by = Mathf.FloorToInt(gy / 8f), bz = Mathf.FloorToInt(sz / 8f);
		int ry = Mathf.FloorToInt(by / (float)Region);
		if (!regions.TryGetValue(ry, out var rb)) regions[ry] = rb = new RegionBuild();
		long key = BlockKey(bx, by, bz);
		if (!rb.Blocks.TryGetValue(key, out var bits)) rb.Blocks[key] = bits = new ulong[8];
		int lx = sx - bx * 8, ly = gy - by * 8, lz = sz - bz * 8;
		bits[ly] |= 1UL << (lz * 8 + lx);
	}
}
