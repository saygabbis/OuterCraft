using UnityEngine;

namespace Tecno.WildCraft;

/// <summary>
/// DESIGN §6 CameraDriver: overwrite Outer Wilds' player camera with Minecraft's eye, view direction and FOV.
/// Done twice, at the end of Update and again right before the camera culls, so nothing in Outer Wilds'
/// own camera code can leave it a frame behind.
/// </summary>
[DefaultExecutionOrder(32001)]
public class CameraDriver : MonoBehaviour
{
	void OnEnable() { Camera.onPreCull += PreCull; }
	void OnDisable() { Camera.onPreCull -= PreCull; }

	void LateUpdate() { Apply(null); }

	static void PreCull(Camera cam) { Apply(cam); }

	static void Apply(Camera cam)
	{
		if (!Bridge.CameraValid) return;
		try
		{
			var owCam = Locator.GetPlayerCamera();
			if (owCam == null) return;
			var main = owCam.mainCamera;
			if (cam != null && cam != main && cam != owCam.farCamera) return;
			var t = owCam.transform;
			t.SetPositionAndRotation(Bridge.CameraPos, Bridge.CameraRot);
			if (main != null) main.fieldOfView = Bridge.CameraFov;
			var far = owCam.farCamera;
			if (far != null) far.fieldOfView = Bridge.CameraFov;
		}
		catch { /* the camera is mid-teardown */ }
	}
}
