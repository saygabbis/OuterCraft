using OWML.Common;
using OWML.ModHelper;
using UnityEngine;

namespace Tecno.WildCraft;

public class WildCraftMod : ModBehaviour
{
	GameObject _host;

	public override void Configure(IModConfig config) => ReadConfig(config);

	static void ReadConfig(IModConfig config)
	{
		try { Bridge.WantEnabled = config.GetSettingsValue<bool>("EnableBridge"); } catch { Bridge.WantEnabled = true; }
		try { McCompositor.Enabled = config.GetSettingsValue<bool>("DrawMinecraftLayers"); } catch { McCompositor.Enabled = true; }
		try { Bridge.DrawBlocks = config.GetSettingsValue<bool>("DrawMinecraftBlocks"); } catch { Bridge.DrawBlocks = true; }
		try { Bridge.MetersPerBlock = Mathf.Clamp(config.GetSettingsValue<float>("UnitsPerBlock"), 0.25f, 4f); } catch { Bridge.MetersPerBlock = 1f; }
	}

	void Start()
	{
		ReadConfig(ModHelper.Config);
		FrameStore.Dir = ModHelper.Manifest.ModFolderPath;
		GamePatches.Apply(ModHelper.Manifest.UniqueName, ModHelper.Console);
		Bridge.Init(ModHelper.Console);

		_host = new GameObject("WildCraft_Host");
		DontDestroyOnLoad(_host);
		_host.AddComponent<CameraDriver>();
		_host.AddComponent<McCompositor>();
		_host.AddComponent<McWorldRender>();
		_host.AddComponent<HostTicker>();

		ModHelper.Console.WriteLine(
			Bridge.WantEnabled
				? "WildCraft: ready. On foot on a planet, with Minecraft running, you play as a Minecraft player. WASD/mouse = Minecraft · O = MC menu · Esc = Outer Wilds menu · G = interact"
				: "WildCraft: disabled (EnableBridge = false).",
			MessageType.Info);
	}

	void OnDestroy()
	{
		Bridge.Shutdown();
		if (_host != null) Destroy(_host);
	}
}

/// <summary>Runs the bridge at the points of the frame it needs: Update, FixedUpdate and the end of the frame.</summary>
[DefaultExecutionOrder(32000)]
public class HostTicker : MonoBehaviour
{
	void Update() { Bridge.Update(); }
	void FixedUpdate() { Bridge.FixedUpdate(); }
	void LateUpdate() { Bridge.LateUpdate(); }
}

