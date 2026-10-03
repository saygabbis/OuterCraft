using System;
using OWML.Common;
using UnityEngine;
using UnityEngine.InputSystem;
using WildCraft;

namespace Tecno.WildCraft;

/// <summary>
/// The Outer Wilds half of SkyCraft (DESIGN §3, §6): while the player is on foot on a planet, Minecraft is
/// authoritative for where the player is and where they look. This class
///  - tells Minecraft where the ground is (CollisionExporter) and where to spawn,
///  - turns the mouse into Minecraft's look,
///  - and puppets the Outer Wilds player body and camera to Minecraft's interpolated pose,
/// on a mapping that wraps Minecraft's flat world around the planet (PlanetFrame).
/// Neither game's logic is replaced; they only translate through the shared memory.
/// </summary>
public static class Bridge
{
	public static bool WantEnabled = true;
	public static bool DrawBlocks = true;
	public static bool DrawLayers = true;
	public static float MetersPerBlock = 1f;

	/// <summary>The puppet is engaged (OW's own movement is off). May still be waiting for Minecraft to arrive.</summary>
	public static bool Active { get; private set; }
	/// <summary>Minecraft has arrived at the spawn point and is driving the player.</summary>
	public static bool Running { get; private set; }
	public static bool OwPauseOpen { get; private set; }
	/// <summary>Minecraft's hand and HUD are on screen (also while Outer Wilds temporarily has the player: campfire, ship...).</summary>
	public static bool HudVisible { get; private set; }
	/// <summary>F8: the player asked to hand themselves back to Outer Wilds (fly the ship, swim...).</summary>
	public static bool ManualOw { get; private set; }
	/// <summary>Outer Wilds has something in front of the crosshair to interact with (a hatch, a campfire, a person...).</summary>
	public static bool OwHasInteractible { get; private set; }
	/// <summary>F1 hides the Outer Wilds HUD as well as Minecraft's.</summary>
	public static bool OwHudHidden { get; private set; }
	static FirstPersonManipulator _manipulator;
	static bool _hudApplied;

	static bool InShipLike()
	{
		try { return PlayerState.IsInsideShip() || PlayerState.AtFlightConsole() || PlayerState.UsingShipComputer(); }
		catch { return false; }
	}
	public static bool McScreenOpen { get; private set; }
	public static HostLink Link { get; private set; }
	public static PlanetFrame Frame { get; private set; }
	public static IModConsole Log;

	/// <summary>While true, Outer Wilds' gameplay input is blocked (see GamePatches).</summary>
	public static bool InputGate => Active && !OwPauseOpen;

	// Camera pose for this frame, consumed by CameraDriver just before the camera renders.
	public static bool CameraValid;
	public static Vector3 CameraPos;
	public static Quaternion CameraRot;
	public static float CameraFov = 70f;
	public static Vector3 FeetWorld;
	public static Vector3 FeetMcInterpolated;
	public static Quaternion FeetRot;
	public static bool FeetValid;
	public static HostLink.McSnapshot Mc;
	public static float Yaw { get; private set; }
	public static float Pitch { get; private set; }
	public static uint ViewW = 1280, ViewH = 720;

	static CollisionExporter _exporter;
	static readonly TickInterp Interp = new TickInterp();
	static uint _teleportSeq = (uint)(HostLink.Qpc() & 0x7FFFFFFF) | 1u;
	static uint _epoch = 1;
	static OWRigidbody _body;
	static OWRigidbody _refBody;
	static int _bodyId = -1;
	static float _bodyOffset = 0.9f;
	static float _eyeOffsetOw = 1.5f;
	static Vector3 _spawnMc;
	static float _engageAt;
	static float _lastSafeY;
	static Vector3 _lastSafeMc;
	static bool _haveSafe;
	static float _nextLog;
	static float _nextTrace;
	static PauseMenuManager _pauseMgr;
	static float _zoom;
	static uint _zoomMode;
	static float _slowSince;
	static float _lastMcFrameTime;
	static ulong _lastMcFrame;
	static int _failedEngage;

	public static void Init(IModConsole log)
	{
		Log = log;
		if (Link != null) return;
		try
		{
			Link = new HostLink(Proto.DefaultMappingName);
			_exporter = new CollisionExporter(Link);
			log.WriteLine("WildCraft: link Local\\SkyCraft_v1 ready (host = Outer Wilds)", MessageType.Info);
		}
		catch (Exception ex)
		{
			log.WriteLine("WildCraft: link failed — " + ex.Message, MessageType.Error);
		}
	}

	public static void Shutdown()
	{
		Disengage("shutdown");
		McWorldRender.Clear();
		try { Link?.Dispose(); } catch { /* */ }
		Link = null;
	}

	static void Say(string text, MessageType type = MessageType.Info) { try { Log?.WriteLine("WildCraft: " + text, type); } catch { /* */ } }

	// ------------------------------------------------------------------------------------------------ per frame

	/// <summary>Start of the frame: decide whether to engage/disengage, read input.</summary>
	public static void Update()
	{
		if (Link == null) return;
		try { Link.Heartbeat(); Link.DrainEvents(OnMcEvent); } catch { return; }

		if (!WantEnabled) { Disengage("disabled"); return; }

		OWRigidbody body = null; PlayerCharacterController controller = null;
		bool inGame = false;
		try
		{
			inGame = LoadManager.GetCurrentScene() == OWScene.SolarSystem;
			if (inGame)
			{
				body = Locator.GetPlayerBody();
				controller = Locator.GetPlayerController();
			}
		}
		catch { inGame = false; }

		if (!inGame || body == null || controller == null)
		{
			HudVisible = false;
			Why("not in the solar system (scene/player not ready)");
			Disengage("not in the solar system");
			OwPauseOpen = false;
			McScreenOpen = false;
			WriteSkyStandby();
			return;
		}

		OwPauseOpen = IsOwPauseOpen();
		bool mcAlive = false;
		try { mcAlive = Link.MinecraftAlive; } catch { /* */ }
		var mc = Link.ReadMc();
		McScreenOpen = mc.InWorld && mc.ScreenOpen;
		HudVisible = mcAlive && mc.InWorld && !OwPauseOpen && !ManualOw && !InShipLike();
		try
		{
			if (_manipulator == null) _manipulator = UnityEngine.Object.FindObjectOfType<FirstPersonManipulator>();
			OwHasInteractible = _manipulator != null && _manipulator.HasFocusedInteractible();
		}
		catch { _manipulator = null; OwHasInteractible = false; }
		try
		{
			var k1 = Keyboard.current;
			if (Active && k1 != null && k1.f1Key.wasPressedThisFrame && !OwPauseOpen) OwHudHidden = !OwHudHidden;
			if (!Active) OwHudHidden = false;
			var want = OwHudHidden ? GUIMode.RenderMode.Hidden : GUIMode.RenderMode.FPS;
			if (OwHudHidden != _hudApplied) { _hudApplied = OwHudHidden; GUIMode.SetRenderMode(want); }
		}
		catch { /* */ }
		try { var kb = Keyboard.current; if (kb != null && kb.f8Key.wasPressedThisFrame && !OwPauseOpen) { ManualOw = !ManualOw; Say(ManualOw ? "F8: Outer Wilds controls the player (F8 again for Minecraft)" : "F8: Minecraft controls the player again"); } } catch { /* */ }

		if (Active && body.GetInstanceID() != _bodyId) Disengage("new player body");

		bool onFoot = OnFootOnPlanet(controller, body);
		if (!onFoot || !mcAlive)
		{
			Why(!mcAlive ? "waiting for Minecraft (heartbeat missing)" : "not on foot (" + (_offFootReason == "" ? "not in character input mode" : _offFootReason) + ")");
			if (Active) Disengage(!mcAlive ? "Minecraft went away" : "player is " + _offFootReason);
			WriteSkyStandby();
			return;
		}

		if (!Active && !TryEngage(body, controller)) { WriteSkyStandby(); return; }

		Mc = mc;
		if (Running) { UpdateLook(mc); MirrorOwDamage(); CheckChart(); }
		if (_exporter != null && Frame != null)
		{
			try
			{
				var p = Running ? Interp.Evaluate(mc, HostLink.Qpc()) : default;
				double fx = Running ? p.FeetX : _spawnMc.x, fy = Running ? p.FeetY : _spawnMc.y, fz = Running ? p.FeetZ : _spawnMc.z;
				_exporter.Tick(fx, fy, fz, Time.unscaledTime, Running ? 2 : 4);
			}
			catch (Exception ex) { SayOnce("collision export failed: " + ex.Message); }
		}

		try
		{
			InputRouter.Tick(Link, OwPauseOpen, McScreenOpen, Math.Max(1, ViewW), Math.Max(1, ViewH));
		}
		catch (Exception ex) { SayOnce("input failed: " + ex.Message); }

		try { ApplyCursor(); } catch { /* */ }
	}

	/// <summary>End of the frame (after OW's own updates): move the puppet and publish state.</summary>
	public static void LateUpdate()
	{
		if (Link == null || !Active) { CameraValid = false; FeetValid = false; return; }
		try { DrivePuppet(); HidePlayerModel(); }
		catch (Exception ex) { SayOnce("puppet failed: " + ex); }
		try { WriteSky(); } catch { /* */ }
	}

	public static void FixedUpdate()
	{
		if (!Active || !FeetValid || _body == null) return;
		try { PlaceBody(); } catch { /* */ }
	}

	// ------------------------------------------------------------------------------------------------ engage

	static FluidDetector _fluidDetector;

	/// <summary>
	/// Water, geysers, the ship's tractor beam, sand, clouds and plasma push the player with Outer Wilds' own fluid
	/// physics, which a Minecraft-driven body cannot feel: while in one, Outer Wilds has the player (and takes them
	/// back to Minecraft once they are out and standing).
	/// </summary>
	static bool InOwFluid(out string name)
	{
		name = null;
		try
		{
			if (_fluidDetector == null)
			{
				var go = Locator.GetPlayerDetector();
				if (go != null) _fluidDetector = go.GetComponent<FluidDetector>() ?? go.GetComponentInChildren<FluidDetector>();
			}
			if (_fluidDetector == null) return false;
			foreach (var t in new[] { FluidVolume.Type.GEYSER, FluidVolume.Type.TRACTOR_BEAM, FluidVolume.Type.WATER, FluidVolume.Type.SAND, FluidVolume.Type.CLOUD, FluidVolume.Type.PLASMA })
				if (_fluidDetector.InFluidType(t)) { name = t.ToString().ToLowerInvariant().Replace("_", " "); return true; }
		}
		catch { _fluidDetector = null; }
		return false;
	}

	static string _offFootReason = "";
	static float _offFootSince;

	/// <summary>True while the player is walking around on a planet (not in the ship, a conversation, a cutscene...).</summary>
	static bool OnFootOnPlanet(PlayerCharacterController controller, OWRigidbody body)
	{
		string why = null;
		try
		{
			if (ManualOw) why = "handed to Outer Wilds with F8";
			else if (InOwFluid(out var fluid)) why = "in " + fluid + " (Outer Wilds physics)";
			else if (PlayerState.IsInsideShip()) why = "inside the ship";
			else if (PlayerState.AtFlightConsole()) why = "at the flight console";
			else if (PlayerState.IsDead()) why = "dead";
			else if (PlayerState.IsAttached()) why = "attached (seated / on a platform)";
			else if (PlayerState.InConversation()) why = "in a conversation";
			else if (PlayerState.UsingShipComputer()) why = "using the ship computer";
			else if (PlayerState.IsSleepingAtCampfire() || PlayerState.IsSleepingAtDreamCampfire()) why = "sleeping at a campfire";
		}
		catch { why = "player state unavailable"; }
		if (why != null)
		{
			_offFootReason = why;
			// A one-frame flicker must not hand the player back and forth.
			if (Active)
			{
				if (_offFootSince == 0f) _offFootSince = Time.unscaledTime;
				return Time.unscaledTime - _offFootSince < 0.3f;
			}
			return false;
		}
		_offFootSince = 0f;
		_offFootReason = "";
		// While engaged (including the OW pause menu) stay engaged unless Outer Wilds switched to another input mode
		// itself (roasting a marshmallow, a dialogue, a ship screen...): then it has the player until that ends.
		if (Active)
		{
			if (OwPauseOpen) return true;
			try
			{
				var mode = OWInput.GetInputMode();
				if (mode != InputMode.Character && mode != InputMode.All)
				{
					_offFootReason = "in Outer Wilds mode " + mode;
					if (_offFootSince == 0f) _offFootSince = Time.unscaledTime;
					return Time.unscaledTime - _offFootSince < 0.15f;
				}
			}
			catch { /* */ }
			_offFootSince = 0f;
			return true;
		}
		try { return OWInput.IsInputMode(InputMode.Character); }
		catch { return false; }
	}

	static bool IsOwPauseOpen()
	{
		try
		{
			if (_pauseMgr == null) _pauseMgr = UnityEngine.Object.FindObjectOfType<PauseMenuManager>();
			return _pauseMgr != null && _pauseMgr.IsOpen();
		}
		catch { _pauseMgr = null; return false; }
	}

	static bool IsPlanetBody(OWRigidbody rb, OWRigidbody player)
	{
		if (rb == null || player == null || ReferenceEquals(rb, player)) return false;
		var n = rb.name ?? "";
		foreach (var bad in new[] { "Player", "Ship", "Probe", "Satellite", "Debris", "Model" })
			if (n.IndexOf(bad, StringComparison.OrdinalIgnoreCase) >= 0) return false;
		return true;
	}

	static bool IsOwnCollider(Collider c, OWRigidbody player)
	{
		if (c == null || c.isTrigger) return true;
		return c.transform.IsChildOf(player.transform);
	}

	/// <summary>The body whose surface is under the player's feet, found by looking straight down (gravity's way).</summary>
	static OWRigidbody PlanetUnderfoot(OWRigidbody player)
	{
		try
		{
			Vector3 pos = player.GetPosition();
			Vector3 down = -player.transform.up;
			try
			{
				var fd = Locator.GetPlayerForceDetector();
				if (fd != null)
				{
					Vector3 g = fd.GetForceAcceleration();
					if (g.sqrMagnitude > 0.01f) down = g.normalized;
				}
			}
			catch { /* */ }
			var hits = Physics.RaycastAll(pos - down * 1.5f, down, 8f, ~0, QueryTriggerInteraction.Ignore);
			Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
			foreach (var h in hits)
			{
				if (IsOwnCollider(h.collider, player)) continue;
				var rb = h.collider.GetComponentInParent<OWRigidbody>();
				if (IsPlanetBody(rb, player)) return rb;
			}
		}
		catch { /* */ }
		return null;
	}

	static bool TryEngage(OWRigidbody body, PlayerCharacterController controller)
	{
		if (Time.unscaledTime < _engageAt) return false;
		bool grounded = false;
		try { grounded = controller.IsGrounded(); } catch { /* */ }
		if (!grounded) { Why("waiting to engage: player not grounded"); return false; }

		OWRigidbody planet = null;
		try { planet = controller.GetGroundBody(); } catch { /* */ }
		if (!IsPlanetBody(planet, body)) { try { planet = controller.GetLastGroundBody(); } catch { planet = null; } }
		if (!IsPlanetBody(planet, body)) planet = PlanetUnderfoot(body);
		if (!IsPlanetBody(planet, body)) { Why("waiting to engage: ground body = " + (planet != null ? planet.name : "none")); _engageAt = Time.unscaledTime + 0.5f; return false; }

		Vector3 bodyPos = body.GetPosition();
		Vector3 center = planet.GetPosition();
		Vector3 up = (bodyPos - center).normalized;
		// The ground right under the feet (not the player's own colliders).
		Vector3 ground = bodyPos - up * 0.9f;
		float offset = 0.9f;
		var hits = Physics.RaycastAll(bodyPos + up * 1.0f, -up, 6f, ~0, QueryTriggerInteraction.Ignore);
		Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
		foreach (var h in hits)
		{
			if (IsOwnCollider(h.collider, body)) continue;
			ground = h.point;
			offset = Vector3.Dot(bodyPos - h.point, up);
			break;
		}
		if (offset < 0.2f || offset > 2.5f) { offset = 0.9f; ground = bodyPos - up * offset; }

		_body = body;
		_refBody = planet;
		_bodyId = body.GetInstanceID();
		_bodyOffset = offset;
		var cam = Locator.GetPlayerCamera();
		try { _eyeOffsetOw = cam != null ? Mathf.Clamp(Vector3.Dot(cam.transform.position - ground, up), 0.8f, 2.2f) : 1.5f; } catch { _eyeOffsetOw = 1.5f; }

		FrameStore.Bind(planet.name, planet.transform, MetersPerBlock);
		Frame = FrameStore.For(planet.name, planet.transform, ground, MetersPerBlock);
		Vector3 spawn = Frame.WorldToMc(ground);
		spawn.y += 0.02f;
		_spawnMc = spawn;
		try
		{
			Vector3 fwd = cam != null ? cam.transform.forward : body.transform.forward;
			Frame.YawPitchFor(fwd, spawn.x, spawn.y, spawn.z, out var yaw, out var pitch);
			Yaw = yaw;
			Pitch = Mathf.Clamp(pitch, -80f, 80f);
		}
		catch { Yaw = 0f; Pitch = 0f; }

		_epoch++;
		_teleportSeq += 2;
		_exporter.Begin(Frame, _epoch, body.transform);
		Interp.Reset();
		InputRouter.Reset(Link);
		Running = false;
		_softPending = false; _haveVel = false;
		Active = true;
		_zoom = 0f;
		_slowSince = 0f;
		_haveSafe = false;
		_failedEngage = 0;
		_lastMcFrameTime = Time.unscaledTime;
		_lastHealth = -1f;

		try { body.MakeKinematic(); } catch (Exception ex) { SayOnce("MakeKinematic failed: " + ex.Message); }
		try { body.SetVelocity(planet.GetPointVelocity(bodyPos)); body.SetAngularVelocity(Vector3.zero); } catch { /* */ }
		Say("engaged on " + planet.name + " (frame slot " + Frame.Slot + ", spawn " + spawn.x.ToString("F1") + ", " + spawn.y.ToString("F1") + ", " + spawn.z.ToString("F1") + ", body offset " + offset.ToString("F2") + ", eye " + _eyeOffsetOw.ToString("F2") + ")", MessageType.Info);
		return true;
	}

	public static void Disengage(string why)
	{
		if (!Active) { Running = false; return; }
		Active = false;
		Running = false;
		CameraValid = false;
		FeetValid = false;
		ShowPlayerModel();
		Say("released: " + why + (_body != null && _refBody != null ? " (r=" + (_body.GetPosition() - _refBody.GetPosition()).magnitude.ToString("F1") + " R0=" + (Frame != null ? Frame.R0.ToString("F1") : "?") + ")" : ""));
		try
		{
			// Never leave a Minecraft screen (inventory, chat...) open behind Outer Wilds, where nothing could close it.
			if (McScreenOpen) { Link.PushInput(Proto.InKey, 41, 1); Link.PushInput(Proto.InKey, 41, 0); }
		}
		catch { /* */ }
		try { InputRouter.Reset(Link); } catch { /* */ }
		OwHudHidden = false;
		try { if (_hudApplied) { _hudApplied = false; GUIMode.SetRenderMode(GUIMode.RenderMode.FPS); } } catch { /* */ }
		try
		{
			Cursor.lockState = CursorLockMode.Locked;
			Cursor.visible = false;
		}
		catch { /* */ }
		try
		{
			var body = _body;
			if (body != null)
			{
				SafeHandoff(body);
				try { body.MakeNonKinematic(); } catch { /* */ }
				try { body.UnfreezeRotation(); } catch { /* */ }
				var rb = _refBody;
				if (rb != null)
				{
					try { body.SetVelocity(rb.GetPointVelocity(body.GetPosition())); } catch { /* */ }
					try { body.SetAngularVelocity(Vector3.zero); } catch { /* */ }
				}
			}
		}
		catch { /* */ }
		_body = null;
		_refBody = null;
		_bodyId = -1;
		_engageAt = Time.unscaledTime + 1.0f;
	}

	static float _nextChartCheck;
	static bool _softPending, _haveVel;
	static Vector3 _pendingVel;

	/// <summary>
	/// Past the edge of the current chart (see FrameStore) the map would start to stretch: move to the chart that
	/// fits this place, anchoring a new one if needed. Minecraft is teleported to the same spot in the new chart.
	/// </summary>
	static void CheckChart()
	{
		if (Time.unscaledTime < _nextChartCheck || Frame == null || _refBody == null || !FeetValid) return;
		_nextChartCheck = Time.unscaledTime + 0.25f;
		if (FrameStore.DistanceFromAnchor(Frame, FeetWorld) < FrameStore.ChartRadius * 0.9f) return;

		Vector3 up = FeetRot * Vector3.up;
		// The ground below (the player may be flying): the chart is anchored on the surface, not in the air.
		Vector3 ground = FeetWorld;
		var hits = Physics.RaycastAll(FeetWorld + up * 1f, -up, 400f, ~0, QueryTriggerInteraction.Ignore);
		Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
		foreach (var h in hits) { if (IsOwnCollider(h.collider, _body)) continue; ground = h.point; break; }
		var next = FrameStore.For(_refBody.name, _refBody.transform, ground, MetersPerBlock);
		if (next == Frame) return;

		Quaternion look = Frame.LookRotation(Yaw, Pitch, FeetMcInterpolated.x, FeetMcInterpolated.y, FeetMcInterpolated.z);
		// Minecraft's velocity (per tick) as a world vector, to give back in the new chart's axes.
		Frame.AxesAt(FeetMcInterpolated.x, FeetMcInterpolated.y, FeetMcInterpolated.z, out var oex, out var oey, out var oez);
		Vector3 worldVel = (oex * (float)(Mc.CurX - Mc.PrevX) + oey * (float)(Mc.CurY - Mc.PrevY) + oez * (float)(Mc.CurZ - Mc.PrevZ)) * MetersPerBlock;
		Frame = next;
		Vector3 spawn = Frame.WorldToMc(FeetWorld);
		spawn.y += 0.02f;
		_spawnMc = spawn;
		try
		{
			Frame.YawPitchFor(look * Vector3.forward, spawn.x, spawn.y, spawn.z, out var yaw, out var pitch);
			Yaw = yaw; Pitch = Mathf.Clamp(pitch, -90f, 90f);
		}
		catch { /* */ }
		Frame.AxesAt(spawn.x, spawn.y, spawn.z, out var nex, out var ney, out var nez);
		_pendingVel = new Vector3(Vector3.Dot(worldVel, nex), Vector3.Dot(worldVel, ney), Vector3.Dot(worldVel, nez)) / MetersPerBlock;
		_haveVel = true;
		_softPending = true;
		_epoch++;
		_teleportSeq += 2;
		_exporter.Begin(Frame, _epoch, _body.transform);
		Interp.Reset();
		Say("moved to another map chart (slot " + Frame.Slot + ") to keep blocks undistorted");
	}

	/// <summary>Stand the player on the ground before Outer Wilds gets the body back, so it never starts inside the terrain.</summary>
	static void SafeHandoff(OWRigidbody body)
	{
		try
		{
			if (!FeetValid || Frame == null) return;
			Vector3 up = FeetRot * Vector3.up;
			Vector3 pos = FeetWorld + up * _bodyOffset;
			var hits = Physics.RaycastAll(FeetWorld + up * 4f, -up, 14f, ~0, QueryTriggerInteraction.Ignore);
			Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
			foreach (var h in hits)
			{
				if (IsOwnCollider(h.collider, body)) continue;
				pos = h.point + up * (_bodyOffset + 0.05f);
				break;
			}
			body.transform.SetPositionAndRotation(pos, FeetRot);
			var rb = body.GetRigidbody();
			if (rb != null) { rb.position = pos; rb.rotation = FeetRot; }
		}
		catch { /* */ }
	}

	// ------------------------------------------------------------------------------------------------ hide the OW body
	// Only Steve (Minecraft's avatar, drawn in third person) should be seen: the Outer Wilds character model is hidden
	// while Minecraft drives, and shown again when Outer Wilds gets the player back.

	static readonly System.Collections.Generic.List<Renderer> _hiddenRenderers = new System.Collections.Generic.List<Renderer>();
	static float _nextHide;

	static bool KeepVisible(Renderer r)
	{
		string n = r.gameObject.name ?? "";
		foreach (var k in new[] { "Helmet", "Visor", "HUD", "Flashlight", "Tool", "Probe", "Signalscope", "ViewModel", "Reticle", "Camera", "Light", "Marshmallow", "Stick" })
			if (n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0) return true;
		return false;
	}

	static void HidePlayerModel()
	{
		if (_body == null || Time.unscaledTime < _nextHide) return;
		_nextHide = Time.unscaledTime + 0.5f;
		foreach (var r in _body.GetComponentsInChildren<Renderer>(true))
		{
			if (r == null || !r.enabled || !(r is SkinnedMeshRenderer || r is MeshRenderer) || KeepVisible(r)) continue;
			r.enabled = false;
			_hiddenRenderers.Add(r);
		}
	}

	static void ShowPlayerModel()
	{
		foreach (var r in _hiddenRenderers) if (r != null) r.enabled = true;
		_hiddenRenderers.Clear();
	}

	// ------------------------------------------------------------------------------------------------ events

	static PlayerResources _resources;
	static float _lastHealth = -1f;

	/// <summary>Minecraft's events: the Minecraft player died, so does the Outer Wilds one (the loop restarts).</summary>
	static void OnMcEvent(HostLink.McEvent e)
	{
		if (e.Type != 2 /* kEvPlayerDied */ || !Active) return;
		try
		{
			var dm = Locator.GetDeathManager();
			if (dm != null && !dm.IsPlayerDying() && !dm.IsPlayerDead())
			{
				Say("the Minecraft player died: the Outer Wilds one goes with them", MessageType.Warning);
				dm.KillPlayer(DeathType.Default);
			}
		}
		catch { /* */ }
	}

	/// <summary>Damage Outer Wilds deals (fire, the sun, oxygen, ...) shows up as Minecraft damage: 100 OW health = 20 hearts.</summary>
	static void MirrorOwDamage()
	{
		try
		{
			if (_resources == null) _resources = UnityEngine.Object.FindObjectOfType<PlayerResources>();
			if (_resources == null) return;
			float h = _resources.GetHealth();
			if (_lastHealth >= 0f && h < _lastHealth - 0.05f && Running)
				Link.PushInput(Proto.InHurt, 3 /* other */, (int)((_lastHealth - h) * 100f), 0, 0);
			_lastHealth = h;
		}
		catch { _resources = null; }
	}

	// ------------------------------------------------------------------------------------------------ look + cursor

	static void UpdateLook(HostLink.McSnapshot mc)
	{
		if (McScreenOpen || OwPauseOpen || !Application.isFocused) return;
		float s = Mathf.Clamp01(mc.Sensitivity <= 0f ? 0.5f : mc.Sensitivity);
		float d = s * 0.6f + 0.2f;
		float k = d * d * d * 8f * 0.15f;            // Minecraft's own mouse formula, degrees per pixel
		Yaw = Mathf.Repeat(Yaw + InputRouter.MouseDx * k, 360f);
		Pitch = Mathf.Clamp(Pitch + InputRouter.MouseDy * k, -90f, 90f);
	}

	static void ApplyCursor()
	{
		if (OwPauseOpen) return;      // OW manages its own cursor in its menu
		bool free = McScreenOpen;
		Cursor.lockState = free ? CursorLockMode.None : CursorLockMode.Locked;
		Cursor.visible = free;
	}

	// ------------------------------------------------------------------------------------------------ puppet

	static void DrivePuppet()
	{
		if (Frame == null || _body == null) return;
		var mc = Mc;
		bool arrived = mc.InWorld && mc.TeleportAck == _teleportSeq;
		if (!arrived && Running && FeetValid)
		{
			// Teleporting between charts: stay where we are for the moment Minecraft needs to arrive.
			return;
		}
		if (!arrived)
		{
			CameraValid = false;
			FeetValid = false;
			// Minecraft has gone quiet while waiting? Give up and hand the player back to Outer Wilds.
			if (Time.unscaledTime - _engageAt > 1000f) return;
			_failedEngage++;
			if (!Running && _failedEngage > 60 * 25) { _failedEngage = 0; Disengage("Minecraft never arrived"); }
			return;
		}
		if (_softPending)
		{
			_softPending = false;
			if (_haveVel)
			{
				_haveVel = false;
				Link.PushInput(Proto.InVelocity, 0, (int)(_pendingVel.x * 10000f), (int)(_pendingVel.y * 10000f), (int)(_pendingVel.z * 10000f));
			}
		}
		if (!Running)
		{
			Running = true;
			Say("LIVE — Minecraft drives the player. WASD/mouse = Minecraft, O = MC menu, Esc = Outer Wilds menu, G = interact", MessageType.Success);
		}

		// Is Minecraft rendering at all? (hidden/paused clients stop updating)
		if (mc.Frame != _lastMcFrame) { _lastMcFrame = mc.Frame; _lastMcFrameTime = Time.unscaledTime; }

		var pose = Interp.Evaluate(mc, HostLink.Qpc());
		double fx = pose.FeetX, fy = pose.FeetY, fz = pose.FeetZ;

		// Fell out of the described world (a hole in the ground, off a cliff into the void): back to safe ground.
		if (mc.OnGround) { _lastSafeMc = new Vector3((float)fx, (float)fy, (float)fz); _haveSafe = true; }
		if (_haveSafe && fy < _lastSafeMc.y - 90f)
		{
			Say("fell out of the world, returning to the last safe spot", MessageType.Warning);
			_spawnMc = _lastSafeMc + new Vector3(0f, 1.5f, 0f);
			_teleportSeq += 2;
			Interp.Reset();
			return;
		}

		FeetMcInterpolated = new Vector3((float)fx, (float)fy, (float)fz);
		if (Time.unscaledTime >= _nextTrace)
		{
			_nextTrace = Time.unscaledTime + 4f;
			Say("trace mc=(" + fx.ToString("F2") + ", " + fy.ToString("F2") + ", " + fz.ToString("F2") + ") ground=" + mc.OnGround + " yaw=" + Yaw.ToString("F0") + " pitch=" + Pitch.ToString("F0") + " cols=" + _exporter.ColumnsSent + " rays=" + _exporter.RaysCast + " mcFrame=" + mc.Frame + " fps=" + (1f / Mathf.Max(0.0001f, Time.unscaledDeltaTime)).ToString("F0") + " r=" + (_body != null && _refBody != null ? (_body.GetPosition() - _refBody.GetPosition()).magnitude.ToString("F1") : "?") + " " + McWorldRender.Stats(), MessageType.Debug);
		}
		FeetWorld = Frame.McToWorld(fx, fy, fz);
		Frame.AxesAt(fx, fy, fz, out var ex, out var ey, out var ez);
		Vector3 north = -ez;
		float yawRad = Yaw * Mathf.Deg2Rad;
		Vector3 horiz = ex * (-Mathf.Sin(yawRad)) + ez * Mathf.Cos(yawRad);
		FeetRot = Quaternion.LookRotation(horiz, ey);
		FeetValid = true;
		PlaceBody();

		// Camera: Minecraft's eye, with Minecraft's walk bob and F5 modes.
		double ex2 = fx, ey2 = fy + pose.EyeHeight, ez2 = fz;
		Vector3 eyeWorld = Frame.McToWorld(ex2, ey2, ez2);
		float phase = pose.BobPhase * Mathf.PI, bob = pose.BobAmount;
		Vector3 right = Vector3.Cross(ey, horiz).normalized;
		eyeWorld += right * (-Mathf.Sin(phase) * bob * 0.5f * MetersPerBlock) + ey * (Mathf.Abs(Mathf.Cos(phase) * bob) * MetersPerBlock);
		float bobPitch = Mathf.Abs(Mathf.Cos(phase - 0.2f) * bob) * 5f;
		float bobRoll = Mathf.Sin(phase) * bob * 3f;
		bool mirrored = mc.CameraMode == 2;
		float camYaw = mirrored ? Yaw + 180f : Yaw;
		float lookPitch = mirrored ? -Pitch : Pitch;
		Quaternion rot = Frame.LookRotation(camYaw, lookPitch + bobPitch, ex2, ey2, ez2);
		bool detached = mc.CameraMode != 0 && mc.CameraDistance > 0f;
		if (!detached || _zoomMode != mc.CameraMode || mc.CameraDistance < _zoom) _zoom = detached ? mc.CameraDistance : 0f;
		else _zoom += (mc.CameraDistance - _zoom) * (1f - Mathf.Exp(-Mathf.Max(Time.unscaledDeltaTime, 0f) / 0.2f));
		_zoomMode = mc.CameraMode;
		if (detached)
		{
			Quaternion noBob = Frame.LookRotation(camYaw, lookPitch, ex2, ey2, ez2);
			eyeWorld -= noBob * Vector3.forward * (_zoom * MetersPerBlock);
		}
		CameraPos = eyeWorld;
		CameraRot = rot * Quaternion.Euler(0f, 0f, bobRoll);
		CameraFov = mc.FovDeg > 5f ? mc.FovDeg : 70f;
		CameraValid = true;
	}

	static void PlaceBody()
	{
		if (_body == null || !FeetValid) return;
		Vector3 up = FeetRot * Vector3.up;
		Vector3 pos = FeetWorld + up * _bodyOffset;
		// Not WarpToPositionRotation: that fires "PlayerRepositioned", and Outer Wilds then re-centres the whole
		// universe around the player on every call. A plain transform/rigidbody move is all a kinematic body needs.
		_body.transform.SetPositionAndRotation(pos, FeetRot);
		try
		{
			var rb = _body.GetRigidbody();
			if (rb != null) { rb.position = pos; rb.rotation = FeetRot; }
		}
		catch { /* */ }
		try
		{
			if (_refBody != null)
			{
				_body.SetVelocity(_refBody.GetPointVelocity(pos));
				_body.SetAngularVelocity(Vector3.zero);
			}
		}
		catch { /* */ }
	}

	// ------------------------------------------------------------------------------------------------ sky state

	static void WriteSkyStandby()
	{
		if (Link == null) return;
		try
		{
			uint flags = Proto.SkyLoading | (OwPauseOpen ? Proto.SkyMenuOpen : 0u);
			Link.WriteSky(flags, 0x57494C44, _epoch, _spawnMc.x, _spawnMc.y, _spawnMc.z, Yaw, Pitch, _teleportSeq, ViewW, ViewH);
		}
		catch { /* */ }
	}

	static void WriteSky()
	{
		uint vw = (uint)Mathf.Clamp(Screen.width / 2, 640, 1280);
		uint vh = (uint)Mathf.Clamp(Screen.height / 2, 360, 720);
		// Keep the overlay's aspect equal to the screen's so the HUD isn't stretched.
		float aspect = Screen.height > 0 ? (float)Screen.width / Screen.height : 16f / 9f;
		vh = (uint)Mathf.Clamp(Mathf.RoundToInt(vw / aspect), 360, 1080);
		ViewW = vw; ViewH = vh;
		uint flags = Proto.SkyInGame | (OwPauseOpen ? Proto.SkyMenuOpen : 0u) | (_softPending ? Proto.SkySoftTeleport : 0u);
		Link.WriteSky(flags, 0x57494C44 ^ (uint)(Frame != null ? Frame.Slot : 0), _epoch, _spawnMc.x, _spawnMc.y, _spawnMc.z, Yaw, Pitch, _teleportSeq, vw, vh, GameHour());
	}

	/// <summary>Minecraft's clock follows the sun: noon when the sun is overhead, midnight when it is below.</summary>
	static float GameHour()
	{
		try
		{
			if (!FeetValid) return 12f;
			var sun = Locator.GetSunTransform();
			if (sun == null) return 12f;
			Vector3 up = FeetRot * Vector3.up;
			Vector3 toSun = (sun.position - FeetWorld).normalized;
			float h = Vector3.Dot(up, toSun);           // 1 sun overhead, -1 under our feet
			// Map to an hour so MC's sky brightness (which peaks at 12 and bottoms at 0) matches.
			float day = Mathf.Clamp01(h * 0.5f + 0.5f); // 0 night .. 1 day
			return Mathf.Lerp(0f, 12f, Mathf.Sqrt(day));
		}
		catch { return 12f; }
	}

	/// <summary>0 (night) to 1 (full sun) at the player, for the block shading.</summary>
	public static float SunLight()
	{
		try
		{
			if (!FeetValid) return 1f;
			var sun = Locator.GetSunTransform();
			if (sun == null) return 1f;
			Vector3 up = FeetRot * Vector3.up;
			float h = Vector3.Dot(up, (sun.position - FeetWorld).normalized);
			return Mathf.Clamp01(0.15f + (h + 0.1f) * 1.6f);
		}
		catch { return 1f; }
	}

	static string _why;
	static void Why(string text)
	{
		if (text == _why) return;
		_why = text;
		Say("standby: " + text);
	}

	static string _lastSaid;
	static void SayOnce(string text)
	{
		if (text == _lastSaid && Time.unscaledTime < _nextLog) return;
		_lastSaid = text;
		_nextLog = Time.unscaledTime + 5f;
		Say(text, MessageType.Warning);
	}
}
