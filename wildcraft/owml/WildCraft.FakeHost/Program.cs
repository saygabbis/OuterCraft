using System.Runtime.InteropServices;
using WildCraft;

/// <summary>
/// Playable Outer Wilds stand-in: creates the SkyCraft shared mapping, streams a flat floor,
/// forwards WASD/mouse into Minecraft. Pair with the SkyCraft Fabric mod.
/// </summary>
internal static class Program
{
	private static readonly Dictionary<int, ushort> VkToSdl = new()
	{
		[0x57] = 26, // W
		[0x41] = 4,  // A
		[0x53] = 22, // S
		[0x44] = 7,  // D
		[0x20] = 44, // Space
		[0xA0] = 225, // LShift
		[0x10] = 225,
		[0x45] = 8,  // E
		[0x51] = 20, // Q
		[0x31] = 30,
		[0x32] = 31,
		[0x33] = 32,
		[0x34] = 33,
		[0x35] = 34,
		[0x36] = 35,
		[0x37] = 36,
		[0x38] = 37,
		[0x39] = 38,
	};

	[DllImport("user32.dll")]
	private static extern short GetAsyncKeyState(int vKey);

	[DllImport("user32.dll")]
	private static extern bool GetCursorPos(out POINT p);

	[DllImport("user32.dll")]
	private static extern bool SetCursorPos(int x, int y);

	[StructLayout(LayoutKind.Sequential)]
	private struct POINT { public int X, Y; }

	private static int Main(string[] args)
	{
		var captureMouse = !args.Contains("--no-mouse-capture");
		using var link = new HostLink();
		Console.WriteLine("WildCraft FakeHost (C#)");
		Console.WriteLine($"  mapping: {link.MappingName}");
		Console.WriteLine(string.Format(
			System.Globalization.CultureInfo.InvariantCulture,
			"  spawn:   ({0}, {1}, 0.5)", FlatPatch.OriginX + 0.5, FlatPatch.FloorY));
		Console.WriteLine("  waiting for Minecraft (SkyCraft Fabric)...");

		const uint worldId = 0x57494C44; // 'WILD'
		const uint epoch = 1;
		var spawnX = FlatPatch.OriginX + 0.5;
		var spawnY = (double)FlatPatch.FloorY;
		var spawnZ = 0.5;
		var tseq = (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() % 100000) + 2;
		var yaw = -90f;
		var pitch = 10f;
		var sent = false;
		var keyState = VkToSdl.Keys.ToDictionary(k => k, _ => false);
		var btnState = new Dictionary<int, bool> { [1] = false, [2] = false, [3] = false };
		var start = DateTime.UtcNow;
		var lastPrint = DateTime.UtcNow;

		try
		{
			while (true)
			{
				link.Heartbeat();

				foreach (var (vk, sdl) in VkToSdl)
				{
					var down = (GetAsyncKeyState(vk) & 0x8000) != 0;
					if (down != keyState[vk])
					{
						link.PushInput(Proto.InKey, sdl, down ? 1 : 0);
						keyState[vk] = down;
					}
				}
				foreach (var (sdlBtn, vk) in new (int, int)[] { (1, 0x01), (3, 0x02), (2, 0x04) })
				{
					var down = (GetAsyncKeyState(vk) & 0x8000) != 0;
					if (down != btnState[sdlBtn])
					{
						link.PushInput(Proto.InMouseButton, (ushort)sdlBtn, down ? 1 : 0);
						btnState[sdlBtn] = down;
					}
				}

				if (captureMouse && link.MinecraftAlive)
				{
					GetCursorPos(out var cur);
					const int cx = 640, cy = 360;
					var dx = cur.X - cx;
					var dy = cur.Y - cy;
					SetCursorPos(cx, cy);
					yaw = (yaw + dx * 0.15f) % 360f;
					pitch = Math.Clamp(pitch + dy * 0.15f, -89f, 89f);
				}

				link.WriteSky(Proto.SkyInGame, worldId, epoch, spawnX, spawnY, spawnZ, yaw, pitch, tseq, 1280, 720);

				if (link.MinecraftAlive && !sent)
				{
					FlatPatch.Send(link, epoch);
					sent = true;
					Console.WriteLine("collision sent (Timber Hearth flat patch)");
				}

				var mc = link.ReadMc();
				if ((DateTime.UtcNow - lastPrint).TotalSeconds >= 1)
				{
					lastPrint = DateTime.UtcNow;
					var alive = link.MinecraftAlive ? "yes" : "no";
					Console.WriteLine(
						$"t={(DateTime.UtcNow - start).TotalSeconds,6:0.0} mc={alive} flags=0x{mc.Flags:X} " +
						$"ack={mc.TeleportAck}/{tseq} pos=({mc.X:F2},{mc.Y:F2},{mc.Z:F2}) " +
						$"look=({yaw:F0},{pitch:F0}) frame={mc.Frame}");
				}

				Thread.Sleep(16);
			}
		}
		catch (Exception ex) when (ex is OperationCanceledException or IOException)
		{
			Console.WriteLine("shutting down");
		}

		return 0;
	}
}
