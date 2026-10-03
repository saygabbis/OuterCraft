using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using WildCraft;

namespace Tecno.WildCraft;

/// <summary>
/// DESIGN §9 world layer: Minecraft builds the block meshes (models, tint) and its atlas; Outer Wilds draws them
/// in its own frame, bent onto the planet by <see cref="PlanetFrame"/> and parented to the planet so they ride
/// along with its spin and orbit. Blocks are lit by Outer Wilds' own lights (sun, campfires, and Minecraft's torches
/// turned into real lights); Minecraft's biome tints are kept as per-tint materials.
/// </summary>
public class McWorldRender : MonoBehaviour
{
	// --------------------------------------------------------------------------------------------- state
	sealed class Mesh3
	{
		public GameObject Go;
		public Mesh Mesh;
		public MeshRenderer Renderer;
	}

	readonly Dictionary<long, Mesh3> _sections = new Dictionary<long, Mesh3>();
	readonly Dictionary<uint, Texture2D> _textures = new Dictionary<uint, Texture2D>();
	readonly Dictionary<int, Material> _blockMats = new Dictionary<int, Material>();
	readonly Dictionary<long, Material> _entityMats = new Dictionary<long, Material>();
	Texture2D _atlas;
	bool _atlasDirty;
	GameObject _root;
	Mesh3 _avatar, _scene;
	float _nextLog;
	static McWorldRender _inst;

	public static void Clear() { if (_inst != null) _inst.ClearAll(); }

	public static string Stats() => _inst == null ? "no-render" : "sections=" + _inst._sections.Count + " atlas=" + (_inst._atlas != null ? _inst._atlas.width + "x" + _inst._atlas.height : "none") +
		" lights=" + _inst._lights.Count + " ents=" + _inst._ents.Count;

	void Awake() { _inst = this; }
	void OnDestroy() { if (_inst == this) _inst = null; ClearAll(); }

	void ClearAll()
	{
		foreach (var s in _sections.Values) Dispose(s);
		_sections.Clear();
		_raw.Clear();
		Dispose(_avatar); _avatar = null;
		Dispose(_scene); _scene = null;
		foreach (var t in _textures.Values) if (t != null) Destroy(t);
		_textures.Clear();
		foreach (var m in _blockMats.Values) if (m != null) Destroy(m);
		_blockMats.Clear();
		foreach (var m in _entityMats.Values) if (m != null) Destroy(m);
		_entityMats.Clear();
		if (_atlas != null) { Destroy(_atlas); _atlas = null; }
		if (_root != null) { Destroy(_root); _root = null; }
		_lights.Clear();
		_pool.Clear();
		_entGo = null; _crackGo = null; _lineGo = null;
	}

	static void Dispose(Mesh3 m)
	{
		if (m == null) return;
		if (m.Mesh != null) Destroy(m.Mesh);
		if (m.Go != null) Destroy(m.Go);
	}

	// --------------------------------------------------------------------------------------------- frame
	void Update()
	{
		var link = Bridge.Link;
		if (link == null) return;
		if (!Bridge.Active || !Bridge.DrawBlocks)
		{
			// Keep the ring from filling while we are not engaged; the atlas and sections stay for next time.
			try { link.DrainRender(Bridge.DrawBlocks ? (Action<uint, byte[]>)OnMessage : (_, __) => { }, 16ul << 20); } catch { /* */ }
			SetVisible(false);
			return;
		}
		SetVisible(true);
		try { link.DrainRender(OnMessage, 6ul << 20); } catch (Exception ex) { Debug.Log("WildCraft render ring: " + ex.Message); }
		if (_atlasDirty && _atlas != null) { _atlas.Apply(false, false); _atlasDirty = false; }
		RetryPending();
		UpdateLights();
		UpdateEntities();
	}

	void SetVisible(bool on)
	{
		if (_root != null && _root.activeSelf != on) _root.SetActive(on);
	}

	void EnsureRoot()
	{
		if (_root != null) return;
		_root = new GameObject("WildCraft_McWorld");
		DontDestroyOnLoad(_root);
	}

	void OnMessage(uint type, byte[] p)
	{
		switch (type)
		{
			case Proto.RenClearAll: foreach (var s in _sections.Values) Dispose(s); _sections.Clear(); _raw.Clear(); _lights.Clear(); break;
			case Proto.RenAtlas: ApplyAtlas(p); break;
			case Proto.RenAtlasRegion: ApplyAtlasRegion(p); break;
			case Proto.RenTexture: ApplyTexture(p); break;
			case Proto.RenSection: ApplySection(p); break;
			case Proto.RenLights: ApplyLights(p); break;
			case Proto.RenAvatar: ApplyBatched(p, 0, true); break;
			case Proto.RenScene: ApplyBatched(p, 24, false); break;
		}
	}

	// --------------------------------------------------------------------------------------------- textures
	void ApplyAtlas(byte[] p)
	{
		if (p == null || p.Length < 8) return;
		int w = BitConverter.ToInt32(p, 0), h = BitConverter.ToInt32(p, 4);
		if (w <= 0 || h <= 0 || p.Length < 8 + (long)w * h * 4) return;
		if (_atlas == null || _atlas.width != w || _atlas.height != h)
		{
			if (_atlas != null) Destroy(_atlas);
			_atlas = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
			_atlas.filterMode = FilterMode.Point;
			_atlas.wrapMode = TextureWrapMode.Clamp;
		}
		var px = new byte[w * h * 4];
		Buffer.BlockCopy(p, 8, px, 0, px.Length);
		_atlas.LoadRawTextureData(px);
		_atlas.Apply(false, false);
		foreach (var m in _blockMats.Values) if (m != null) m.mainTexture = _atlas;
		foreach (var kv in _entityMats) if (kv.Value != null && (kv.Key >> 8) == 0) kv.Value.mainTexture = _atlas;
		if (_entMat != null) _entMat.mainTexture = _atlas;
		if (_crackMat != null) _crackMat.mainTexture = _atlas;
	}

	void ApplyAtlasRegion(byte[] p)
	{
		if (_atlas == null || p == null || p.Length < 16) return;
		int x = (int)BitConverter.ToUInt32(p, 0), y = (int)BitConverter.ToUInt32(p, 4);
		int w = (int)BitConverter.ToUInt32(p, 8), h = (int)BitConverter.ToUInt32(p, 12);
		if (w <= 0 || h <= 0 || p.Length < 16 + w * h * 4 || x + w > _atlas.width || y + h > _atlas.height) return;
		var cols = new Color32[w * h];
		int o = 16;
		for (int i = 0; i < cols.Length; i++, o += 4) cols[i] = new Color32(p[o], p[o + 1], p[o + 2], p[o + 3]);
		_atlas.SetPixels32(x, y, w, h, cols);
		_atlasDirty = true;
	}

	void ApplyTexture(byte[] p)
	{
		if (p == null || p.Length < 16) return;
		uint id = BitConverter.ToUInt32(p, 0);
		int w = (int)BitConverter.ToUInt32(p, 4), h = (int)BitConverter.ToUInt32(p, 8);
		if (id == 0 || w <= 0 || h <= 0 || p.Length < 16 + w * h * 4) return;
		var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
		var px = new byte[w * h * 4];
		Buffer.BlockCopy(p, 16, px, 0, px.Length);
		tex.LoadRawTextureData(px);
		tex.Apply(false, false);
		if (_textures.TryGetValue(id, out var old) && old != null) Destroy(old);
		_textures[id] = tex;
		foreach (var kv in _entityMats) if (kv.Value != null && (kv.Key >> 8) == id) kv.Value.mainTexture = tex;
	}

	// --------------------------------------------------------------------------------------------- materials
	// kind 0 solid (lit, writes depth), 1 cutout (alpha-tested), 2 translucent (blended).
	static Shader Pick(int kind)
	{
		Shader s = null;
		if (kind == 0) s = Shader.Find("Legacy Shaders/Diffuse");
		else if (kind == 1) s = Shader.Find("Outer Wilds/Particles/Cutoff");
		else s = Shader.Find("Outer Wilds/Particles/Alpha");
		return s ?? Shader.Find("Standard") ?? Shader.Find("Sprites/Default");
	}

	static void Setup(Material m, int kind, Color tint)
	{
		try
		{
			foreach (var c in new[] { "_Color", "_TintColor", "_BaseColor" }) if (m.HasProperty(c)) m.SetColor(c, tint);
			if (m.HasProperty("_Cutoff")) m.SetFloat("_Cutoff", kind == 1 ? 0.45f : 0.02f);
			if (m.HasProperty("_Glossiness")) m.SetFloat("_Glossiness", 0f);
			if (m.HasProperty("_Metallic")) m.SetFloat("_Metallic", 0f);
			m.renderQueue = kind == 0 ? 2000 : kind == 1 ? 2450 : 3000;
		}
		catch { /* */ }
	}

	Material BlockMaterial(int kind, int tintKey)
	{
		int key = kind << 16 | tintKey;
		if (_blockMats.TryGetValue(key, out var m) && m != null) return m;
		int r = (tintKey >> 8) & 15, g = (tintKey >> 4) & 15, b = tintKey & 15, a = (tintKey >> 12) & 7;
		bool plain = (tintKey & 0xFFF) == 0;
		var tint = new Color(plain ? 1f : (r * 16 + 8) / 255f, plain ? 1f : (g * 16 + 8) / 255f, plain ? 1f : (b * 16 + 8) / 255f, kind == 2 ? (a * 32 + 16) / 255f : 1f);
		m = new Material(Pick(kind)) { hideFlags = HideFlags.HideAndDontSave };
		Setup(m, kind, tint);
		if (_atlas != null) m.mainTexture = _atlas;
		_blockMats[key] = m;
		return m;
	}

	Material EntityMaterial(uint texId, int kind)
	{
		long key = ((long)texId << 8) | (uint)kind;
		if (_entityMats.TryGetValue(key, out var m) && m != null) return m;
		m = new Material(Pick(kind == 2 ? 2 : 1)) { hideFlags = HideFlags.HideAndDontSave };
		Setup(m, kind == 2 ? 2 : 1, Color.white);
		m.mainTexture = texId == 0 ? _atlas : (_textures.TryGetValue(texId, out var t) ? t : null);
		_entityMats[key] = m;
		return m;
	}

	// --------------------------------------------------------------------------------------------- sections
	static long Key(int sx, int sy, int sz) => ((long)sx & 0xfffff) | (((long)sy & 0xfffff) << 20) | (((long)sz & 0xfffff) << 40);

	static int TintKey(byte r, byte g, byte b, byte a, int kind)
	{
		bool white = r >= 232 && g >= 232 && b >= 232;
		int t = white ? 0 : ((r >> 4) << 8) | ((g >> 4) << 4) | (b >> 4);
		if (kind == 2) t |= (a >> 5) << 12;
		else if (kind == 2 && white && a > 250) t = 0;
		return t;
	}

	// Minecraft sends every section once, as soon as it links up, which is usually before the player has stepped onto
	// a planet. Keep the raw sections and build them when their planet is known (RetryPending).
	readonly Dictionary<long, byte[]> _raw = new Dictionary<long, byte[]>();
	int _retryCursor;

	float _nextPurge;

	void RetryPending()
	{
		if (Time.unscaledTime >= _nextPurge)
		{
			// A new time loop rebuilds the planets and destroys what hung on them: build those sections again.
			_nextPurge = Time.unscaledTime + 1f;
			List<long> dead = null;
			foreach (var kv in _sections) if (kv.Value.Go == null) (dead ?? (dead = new List<long>())).Add(kv.Key);
			if (dead != null) foreach (var k in dead) _sections.Remove(k);
		}
		if (_raw.Count == _sections.Count) return;
		int budget = 8;
		foreach (var kv in new List<KeyValuePair<long, byte[]>>(_raw))
		{
			if (_sections.ContainsKey(kv.Key)) continue;
			ApplySection(kv.Value);
			if (--budget <= 0) break;
		}
	}

	void ApplySection(byte[] p)
	{
		if (p == null || p.Length < 16) return;
		int sx = BitConverter.ToInt32(p, 0), sy = BitConverter.ToInt32(p, 4), sz = BitConverter.ToInt32(p, 8);
		int n = BitConverter.ToInt32(p, 12);
		long key = Key(sx, sy, sz);
		if (n <= 0)
		{
			_raw.Remove(key);
			if (_sections.TryGetValue(key, out var dead)) { Dispose(dead); _sections.Remove(key); }
			_lights.Remove(key);
			return;
		}
		_raw[key] = p;
		if (p.Length < 16 + n * Proto.RenVertexBytes) return;
		var frame = FrameStore.ForMcX(sx * 16 + 8);
		if (frame == null || frame.Body == null) return;   // that planet is not in play right now: built later
		EnsureRoot();

		var verts = new Vector3[n];
		var uvs = new Vector2[n];
		var subs = new SortedDictionary<int, List<int>>();
		for (int i = 0; i < n; i++)
		{
			int o = 16 + i * Proto.RenVertexBytes;
			verts[i] = frame.McToLocal(sx * 16.0 + BitConverter.ToSingle(p, o), sy * 16.0 + BitConverter.ToSingle(p, o + 4), sz * 16.0 + BitConverter.ToSingle(p, o + 8));
			uvs[i] = new Vector2(BitConverter.ToSingle(p, o + 12), BitConverter.ToSingle(p, o + 16));
			if (i % 3 != 0) continue;
			uint flags = BitConverter.ToUInt32(p, o + 28);
			int kind = (flags & 2) != 0 ? 2 : (flags & 4) != 0 ? 0 : 1;
			int tk = TintKey(p[o + 20], p[o + 21], p[o + 22], p[o + 23], kind);
			int sub = kind << 16 | tk;
			if (!subs.TryGetValue(sub, out var list)) subs[sub] = list = new List<int>();
			// the mirror in the Minecraft -> Unity mapping flips winding: emit 0,2,1
			list.Add(i); list.Add(i + 2); list.Add(i + 1);
		}

		if (_sections.TryGetValue(key, out var old)) Dispose(old);
		var m = new Mesh3 { Go = new GameObject("MC_" + sx + "_" + sy + "_" + sz) };
		m.Go.transform.SetParent(frame.Body, false);
		var mf = m.Go.AddComponent<MeshFilter>();
		m.Renderer = m.Go.AddComponent<MeshRenderer>();
		m.Renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.On;
		m.Renderer.receiveShadows = true;

		var mesh = new Mesh();
		mesh.indexFormat = n > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
		mesh.vertices = verts;
		mesh.uv = uvs;
		mesh.subMeshCount = subs.Count;
		var mats = new Material[subs.Count];
		int si = 0;
		foreach (var kv in subs)
		{
			mesh.SetTriangles(kv.Value, si, false);
			mats[si++] = BlockMaterial(kv.Key >> 16, kv.Key & 0xffff);
		}
		mesh.RecalculateNormals();
		mesh.RecalculateBounds();
		mf.sharedMesh = mesh;
		m.Mesh = mesh;
		m.Renderer.sharedMaterials = mats;
		_sections[key] = m;
	}

	// --------------------------------------------------------------------------------------------- block lights
	struct BlockLight { public Vector3 Mc; public float Level; public Color Color; public byte Kind; }
	readonly Dictionary<long, List<BlockLight>> _lights = new Dictionary<long, List<BlockLight>>();
	readonly List<Light> _pool = new List<Light>();
	const int MaxLights = 8;
	float _nextLights;

	void ApplyLights(byte[] p)
	{
		if (p == null || p.Length < 16) return;
		int sx = BitConverter.ToInt32(p, 0), sy = BitConverter.ToInt32(p, 4), sz = BitConverter.ToInt32(p, 8);
		int n = BitConverter.ToInt32(p, 12);
		long key = Key(sx, sy, sz);
		if (n <= 0 || p.Length < 16 + n * 8) { _lights.Remove(key); return; }
		var list = new List<BlockLight>(n);
		for (int i = 0; i < n; i++)
		{
			int o = 16 + i * 8;
			uint packed = BitConverter.ToUInt32(p, o + 4);
			list.Add(new BlockLight
			{
				Mc = new Vector3(sx * 16f + p[o] + 0.5f, sy * 16f + p[o + 1] + 0.5f, sz * 16f + p[o + 2] + 0.5f),
				Level = p[o + 3],
				Color = new Color((packed & 0xff) / 255f, ((packed >> 8) & 0xff) / 255f, ((packed >> 16) & 0xff) / 255f),
				Kind = (byte)((packed >> 24) & 0xf),
			});
		}
		_lights[key] = list;
	}

	Light PoolLight(int i)
	{
		while (_pool.Count <= i)
		{
			var go = new GameObject("MC_Light" + _pool.Count);
			go.transform.SetParent(_root.transform, false);
			var l = go.AddComponent<Light>();
			l.type = LightType.Point;
			l.shadows = LightShadows.None;
			l.renderMode = LightRenderMode.ForcePixel;
			l.cullingMask = ~0;
			_pool.Add(l);
		}
		return _pool[i];
	}

	/// <summary>The nearest light-emitting blocks (and the torch in your hand) become real point lights.</summary>
	void UpdateLights()
	{
		if (Time.unscaledTime < _nextLights) return;
		_nextLights = Time.unscaledTime + 0.05f;
		var frame = Bridge.Frame;
		if (frame == null || frame.Body == null || !Bridge.FeetValid) { foreach (var l in _pool) if (l != null) l.enabled = false; return; }
		EnsureRoot();
		Vector3 me = Bridge.FeetMcInterpolated;
		var near = new List<KeyValuePair<float, BlockLight>>();
		foreach (var list in _lights.Values)
			foreach (var bl in list)
			{
				float d = (bl.Mc - me).sqrMagnitude;
				if (d < 40f * 40f) near.Add(new KeyValuePair<float, BlockLight>(d, bl));
			}
		near.Sort((a, b) => a.Key.CompareTo(b.Key));
		float t = Time.unscaledTime;
		int used = 0;
		// A torch (or lantern, glowstone...) in hand lights the way like a flashlight.
		uint held = Bridge.Mc.HeldLight & 0xFF;
		if (held > 0 && Bridge.CameraValid)
		{
			var l = PoolLight(used++);
			l.transform.position = Bridge.CameraPos + Bridge.CameraRot * (Vector3.forward * 0.4f + Vector3.right * 0.25f + Vector3.down * 0.1f);
			float flick = 0.9f + 0.1f * Mathf.PerlinNoise(t * 8f, 0.3f);
			l.color = new Color(1f, 0.72f, 0.38f);
			l.range = Mathf.Max(4f, held * 1.5f) * Bridge.MetersPerBlock;
			l.intensity = Mathf.Clamp(held / 15f * 2.4f, 0.5f, 2.6f) * flick;
			l.enabled = true;
		}
		for (int i = 0; used < MaxLights; i++, used++)
		{
			var light = PoolLight(used);
			if (i >= near.Count) { light.enabled = false; continue; }
			var b = near[i].Value;
			light.transform.position = frame.McToWorld(b.Mc.x, b.Mc.y + 0.1f, b.Mc.z);
			float flicker = b.Kind == 1 ? 0.88f + 0.12f * Mathf.PerlinNoise(t * 7f, i * 3.1f) : b.Kind == 2 ? 0.92f + 0.08f * Mathf.Sin(t * 1.3f + i) : 1f;
			light.color = b.Color;
			light.range = Mathf.Max(4f, b.Level * 1.5f) * Bridge.MetersPerBlock;
			light.intensity = Mathf.Clamp(b.Level / 15f * 2.4f, 0.5f, 2.6f) * flicker;
			light.enabled = true;
		}
	}

	// --------------------------------------------------------------------------------------------- avatar + scene
	/// <summary>RenAvatar (the player) and RenScene (entities, particles): batches + vertices, rebuilt every frame.</summary>
	void ApplyBatched(byte[] p, int originBytes, bool avatar)
	{
		if (p == null || p.Length < originBytes + 8) { HideBatched(avatar); return; }
		double ox = 0, oy = 0, oz = 0;
		if (originBytes == 24) { ox = BitConverter.ToDouble(p, 0); oy = BitConverter.ToDouble(p, 8); oz = BitConverter.ToDouble(p, 16); }
		int batches = (int)BitConverter.ToUInt32(p, originBytes), verts = (int)BitConverter.ToUInt32(p, originBytes + 4);
		if (batches == 0 || verts == 0) { HideBatched(avatar); return; }
		int bOff = originBytes + 8, vOff = bOff + batches * 16;
		if (p.Length < vOff + verts * Proto.RenVertexBytes) return;
		if (!Bridge.FeetValid || Bridge.Frame == null || Bridge.Frame.Body == null) return;
		var frame = Bridge.Frame;
		var feet = Bridge.FeetMcInterpolated;
		double ax = avatar ? feet.x : ox, ay = avatar ? feet.y : oy, az = avatar ? feet.z : oz;
		EnsureRoot();

		var local = new Vector3[verts];
		var uvs = new Vector2[verts];
		for (int i = 0; i < verts; i++)
		{
			int o = vOff + i * Proto.RenVertexBytes;
			local[i] = frame.McToLocal(ax + BitConverter.ToSingle(p, o), ay + BitConverter.ToSingle(p, o + 4), az + BitConverter.ToSingle(p, o + 8));
			uvs[i] = new Vector2(BitConverter.ToSingle(p, o + 12), BitConverter.ToSingle(p, o + 16));
		}
		var tris = new List<int>[batches];
		var tex = new uint[batches]; var kinds = new int[batches];
		for (int b = 0; b < batches; b++)
		{
			int o = bOff + b * 16;
			tex[b] = BitConverter.ToUInt32(p, o);
			kinds[b] = (BitConverter.ToUInt32(p, o + 12) & 1) != 0 ? 2 : 1;
			int first = (int)BitConverter.ToUInt32(p, o + 4), count = (int)BitConverter.ToUInt32(p, o + 8);
			tris[b] = new List<int>(count);
			for (int i = first; i + 2 < first + count && i + 2 < verts; i += 3) { tris[b].Add(i); tris[b].Add(i + 2); tris[b].Add(i + 1); }
		}

		var old = avatar ? _avatar : _scene;
		var mesh = old != null && old.Mesh != null ? old.Mesh : new Mesh();
		mesh.Clear();
		mesh.indexFormat = verts > 65000 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16;
		mesh.vertices = local;
		mesh.uv = uvs;
		mesh.subMeshCount = batches;
		for (int b = 0; b < batches; b++) mesh.SetTriangles(tris[b], b, false);
		mesh.RecalculateNormals();
		mesh.RecalculateBounds();
		mesh.bounds = new Bounds(mesh.bounds.center, mesh.bounds.size + Vector3.one * 4f);

		var m = old ?? new Mesh3();
		m.Mesh = mesh;
		if (m.Go == null)
		{
			m.Go = new GameObject(avatar ? "MC_Steve" : "MC_Scene");
			m.Go.AddComponent<MeshFilter>().sharedMesh = mesh;
			m.Renderer = m.Go.AddComponent<MeshRenderer>();
			m.Renderer.shadowCastingMode = avatar ? UnityEngine.Rendering.ShadowCastingMode.On : UnityEngine.Rendering.ShadowCastingMode.Off;
		}
		m.Go.transform.SetParent(frame.Body, false);
		m.Go.SetActive(true);
		var mats = new Material[batches];
		for (int b = 0; b < batches; b++) mats[b] = EntityMaterial(tex[b], kinds[b]);
		m.Renderer.sharedMaterials = mats;
		if (avatar) _avatar = m; else _scene = m;
	}

	void HideBatched(bool avatar)
	{
		var m = avatar ? _avatar : _scene;
		if (m != null && m.Go != null) m.Go.SetActive(false);
	}

	// --------------------------------------------------------------------------------------------- world entities
	// Arrows, dropped items and blocks, block-breaking cracks and the block outline: Minecraft publishes them as a
	// small table each frame (not as meshes); the host draws them.
	readonly List<HostLink.WorldEnt> _ents = new List<HostLink.WorldEnt>();
	readonly float[] _selMin = new float[3], _selMax = new float[3];
	GameObject _entGo, _crackGo, _lineGo;
	Mesh _entMesh, _crackMesh, _lineMesh;
	Material _entMat, _crackMat, _lineMat;
	readonly List<Vector3> _ev = new List<Vector3>(), _cv = new List<Vector3>(), _lv = new List<Vector3>();
	readonly List<Vector2> _eu = new List<Vector2>(), _cu = new List<Vector2>();
	readonly List<int> _et = new List<int>(), _ct = new List<int>(), _lt = new List<int>();
	readonly List<Color> _lc = new List<Color>();

	GameObject MakeEntObject(string name, out Mesh mesh, Material mat)
	{
		var go = new GameObject(name);
		mesh = new Mesh();
		mesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
		mesh.MarkDynamic();
		go.AddComponent<MeshFilter>().sharedMesh = mesh;
		var r = go.AddComponent<MeshRenderer>();
		r.sharedMaterial = mat;
		r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
		r.receiveShadows = false;
		return go;
	}

	void UpdateEntities()
	{
		var link = Bridge.Link;
		var frame = Bridge.Frame;
		if (link == null || frame == null || frame.Body == null || !Bridge.FeetValid || _atlas == null) { if (_entGo != null) { _entGo.SetActive(false); _crackGo.SetActive(false); _lineGo.SetActive(false); } return; }
		EnsureRoot();
		if (_entMat == null)
		{
			_entMat = new Material(Pick(1)) { hideFlags = HideFlags.HideAndDontSave }; Setup(_entMat, 1, Color.white); _entMat.mainTexture = _atlas;
			_crackMat = new Material(Pick(2)) { hideFlags = HideFlags.HideAndDontSave }; Setup(_crackMat, 2, Color.white); _crackMat.mainTexture = _atlas;
			_lineMat = new Material(Shader.Find("Sprites/Default") ?? Shader.Find("UI/Default")) { hideFlags = HideFlags.HideAndDontSave };
		}
		if (_entGo == null)
		{
			_entGo = MakeEntObject("MC_Entities", out _entMesh, _entMat);
			_crackGo = MakeEntObject("MC_Cracks", out _crackMesh, _crackMat);
			_lineGo = MakeEntObject("MC_Outline", out _lineMesh, _lineMat);
			foreach (var g in new[] { _entGo, _crackGo, _lineGo }) g.transform.SetParent(frame.Body, false);
		}
		foreach (var g in new[] { _entGo, _crackGo, _lineGo }) if (g.transform.parent != frame.Body) g.transform.SetParent(frame.Body, false);

		bool hasSel;
		if (!link.ReadWorldEntities(_ents, out hasSel, _selMin, _selMax)) return;
		_ev.Clear(); _eu.Clear(); _et.Clear(); _cv.Clear(); _cu.Clear(); _ct.Clear(); _lv.Clear(); _lt.Clear(); _lc.Clear();
		foreach (var e in _ents)
		{
			switch (e.Kind)
			{
				case 1: case 3: AddArrow(frame, e); break;                   // arrow, trident
				case 2: AddItem(frame, e); break;                            // dropped item: a turning sprite
				case 4: AddCube(frame, e, _ev, _eu, _et, e.Scale, e.Scale, e.Scale, e.Yaw, 0f, true); break;   // dropped block
				case 5: AddCube(frame, e, _cv, _cu, _ct, e.ExtX, e.ExtY, e.ExtZ, 0f, 0.003f, false); break;    // block cracks
			}
		}
		Fill(_entMesh, _ev, _eu, _et); Fill(_crackMesh, _cv, _cu, _ct);
		if (hasSel) AddOutline(frame);
		_lineMesh.Clear();
		if (_lv.Count > 0)
		{
			_lineMesh.SetVertices(_lv);
			_lineMesh.SetColors(_lc);
			_lineMesh.SetIndices(_lt.ToArray(), MeshTopology.Lines, 0);
			_lineMesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
		}
		_entGo.SetActive(_ev.Count > 0); _crackGo.SetActive(_cv.Count > 0); _lineGo.SetActive(_lv.Count > 0);
	}

	static void Fill(Mesh mesh, List<Vector3> v, List<Vector2> uv, List<int> t)
	{
		mesh.Clear();
		if (v.Count == 0) return;
		mesh.SetVertices(v);
		mesh.SetUVs(0, uv);
		mesh.SetTriangles(t, 0, false);
		mesh.RecalculateNormals();
		mesh.bounds = new Bounds(Vector3.zero, Vector3.one * 100000f);
	}

	static Vector3 P(PlanetFrame f, double x, double y, double z) => f.McToLocal(x, y, z);

	/// <summary>A quad from four Minecraft-space corners (counter-clockwise seen from the front in Minecraft).</summary>
	static void Quad(PlanetFrame f, List<Vector3> v, List<Vector2> uv, List<int> t, Vector3 a, Vector3 b, Vector3 c, Vector3 d, float[] u, int uo)
	{
		int i = v.Count;
		v.Add(P(f, a.x, a.y, a.z)); v.Add(P(f, b.x, b.y, b.z)); v.Add(P(f, c.x, c.y, c.z)); v.Add(P(f, d.x, d.y, d.z));
		// a = top-left, b = top-right, c = bottom-right, d = bottom-left of the sprite
		uv.Add(new Vector2(u[uo], u[uo + 1])); uv.Add(new Vector2(u[uo + 2], u[uo + 1])); uv.Add(new Vector2(u[uo + 2], u[uo + 3])); uv.Add(new Vector2(u[uo], u[uo + 3]));
		// the Minecraft -> Unity mirror flips winding
		t.Add(i); t.Add(i + 2); t.Add(i + 1); t.Add(i); t.Add(i + 3); t.Add(i + 2);
		// double-sided: sprites are seen from both ways
		t.Add(i); t.Add(i + 1); t.Add(i + 2); t.Add(i); t.Add(i + 2); t.Add(i + 3);
	}

	void AddItem(PlanetFrame f, HostLink.WorldEnt e)
	{
		float h = Mathf.Max(0.2f, e.Scale) * 0.5f;
		float yr = e.Yaw * Mathf.Deg2Rad;
		Vector3 right = new Vector3(Mathf.Cos(yr), 0f, Mathf.Sin(yr)) * h;
		Vector3 c = new Vector3(e.X, e.Y + h, e.Z), up = new Vector3(0f, h, 0f);
		Quad(f, _ev, _eu, _et, c - right + up, c + right + up, c + right - up, c - right - up, e.Uv, 0);
	}

	void AddArrow(PlanetFrame f, HostLink.WorldEnt e)
	{
		float yr = e.Yaw * Mathf.Deg2Rad, pr = e.Pitch * Mathf.Deg2Rad;
		var d = new Vector3(-Mathf.Sin(yr) * Mathf.Cos(pr), -Mathf.Sin(pr), Mathf.Cos(yr) * Mathf.Cos(pr)).normalized;
		float len = Mathf.Max(0.6f, e.Scale > 0.01f ? e.Scale : 1f);
		var c = new Vector3(e.X, e.Y, e.Z);
		var side1 = Vector3.Cross(d, Mathf.Abs(d.y) > 0.95f ? Vector3.right : Vector3.up).normalized;
		var side2 = Vector3.Cross(d, side1).normalized;
		foreach (var s in new[] { side1, side2 })
		{
			// item icons point to their top-right corner: turn the sprite so that corner follows the flight direction
			Vector3 r = (d - s) * 0.7071f * len * 0.5f, u = (d + s) * 0.7071f * len * 0.5f;
			Quad(f, _ev, _eu, _et, c - r + u, c + r + u, c + r - u, c - r - u, e.Uv, 0);
		}
	}

	/// <summary>An axis-aligned box (spinning about the vertical for dropped blocks) as six quads.</summary>
	void AddCube(PlanetFrame f, HostLink.WorldEnt e, List<Vector3> v, List<Vector2> uv, List<int> t, float sx, float sy, float sz, float yaw, float grow, bool centered)
	{
		Vector3 min, max;
		if (centered) { min = new Vector3(-sx * 0.5f, 0f, -sz * 0.5f); max = new Vector3(sx * 0.5f, sy, sz * 0.5f); }
		else { min = new Vector3(-grow, -grow, -grow); max = new Vector3(sx + grow, sy + grow, sz + grow); }
		float yr = yaw * Mathf.Deg2Rad, cs = Mathf.Cos(yr), sn = Mathf.Sin(yr);
		Func<float, float, float, Vector3> W = (x, y, z) =>
		{
			if (centered) { float rx = x * cs - z * sn, rz = x * sn + z * cs; return new Vector3(e.X + rx, e.Y + y + 0.15f, e.Z + rz); }
			return new Vector3(e.X + x, e.Y + y, e.Z + z);
		};
		float[] side = new float[] { e.Uv[0], e.Uv[1], e.Uv[2], e.Uv[3] };
		float[] top = centered ? new float[] { e.Uv[4], e.Uv[5], e.Uv[6], e.Uv[7] } : side;
		float[] bot = centered ? new float[] { e.Uv[8], e.Uv[9], e.Uv[10], e.Uv[11] } : side;
		// +Y
		Quad(f, v, uv, t, W(min.x, max.y, min.z), W(max.x, max.y, min.z), W(max.x, max.y, max.z), W(min.x, max.y, max.z), top, 0);
		// -Y
		Quad(f, v, uv, t, W(min.x, min.y, max.z), W(max.x, min.y, max.z), W(max.x, min.y, min.z), W(min.x, min.y, min.z), bot, 0);
		// -Z, +Z, -X, +X
		Quad(f, v, uv, t, W(max.x, max.y, min.z), W(min.x, max.y, min.z), W(min.x, min.y, min.z), W(max.x, min.y, min.z), side, 0);
		Quad(f, v, uv, t, W(min.x, max.y, max.z), W(max.x, max.y, max.z), W(max.x, min.y, max.z), W(min.x, min.y, max.z), side, 0);
		Quad(f, v, uv, t, W(min.x, max.y, min.z), W(min.x, max.y, max.z), W(min.x, min.y, max.z), W(min.x, min.y, min.z), side, 0);
		Quad(f, v, uv, t, W(max.x, max.y, max.z), W(max.x, max.y, min.z), W(max.x, min.y, min.z), W(max.x, min.y, max.z), side, 0);
	}

	void AddOutline(PlanetFrame f)
	{
		const float g = 0.003f;
		Vector3 a = new Vector3(_selMin[0] - g, _selMin[1] - g, _selMin[2] - g), b = new Vector3(_selMax[0] + g, _selMax[1] + g, _selMax[2] + g);
		var c = new Vector3[8];
		for (int i = 0; i < 8; i++)
			c[i] = P(f, (i & 1) == 0 ? a.x : b.x, (i & 2) == 0 ? a.y : b.y, (i & 4) == 0 ? a.z : b.z);
		int[,] edges = { { 0, 1 }, { 2, 3 }, { 4, 5 }, { 6, 7 }, { 0, 2 }, { 1, 3 }, { 4, 6 }, { 5, 7 }, { 0, 4 }, { 1, 5 }, { 2, 6 }, { 3, 7 } };
		for (int e = 0; e < 12; e++)
		{
			_lt.Add(_lv.Count); _lv.Add(c[edges[e, 0]]); _lc.Add(new Color(0f, 0f, 0f, 0.65f));
			_lt.Add(_lv.Count); _lv.Add(c[edges[e, 1]]); _lc.Add(new Color(0f, 0f, 0f, 0.65f));
		}
	}
}
