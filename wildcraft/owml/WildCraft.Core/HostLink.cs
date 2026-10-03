using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace WildCraft
{
	/// <summary>Host-side SkyCraft shared-memory link (Outer Wilds or FakeHost).</summary>
	public sealed class HostLink : IDisposable
	{
		private readonly MemoryMappedFile _mmf;
		private readonly MemoryMappedViewAccessor _view;
		private uint _skySeq;
		private ulong _colHead;
		private int _overlayFront = 2;
		private bool _disposed;
		private byte[] _overlayScratch;

		public string MappingName { get; }

		public HostLink() : this(Proto.DefaultMappingName) { }

		public HostLink(string mappingName)
		{
			if (string.IsNullOrEmpty(mappingName)) mappingName = Proto.DefaultMappingName;
			MappingName = mappingName;
			_mmf = MemoryMappedFile.CreateOrOpen(MappingName, (long)Proto.MappingBytes);
			_view = _mmf.CreateViewAccessor(0, (long)Proto.MappingBytes, MemoryMappedFileAccess.ReadWrite);
			Clear((long)Proto.OffHeader, 0x100);
			Clear((long)Proto.OffSkyState, 0x40);
			Clear((long)Proto.OffMcState, 0x100);
			Clear((long)Proto.OffOverlayCtl, 0x100);
			Clear((long)Proto.OffInputRing, 0x80);
			Clear((long)Proto.OffCollisionRing, 0x80);
			Clear((long)Proto.OffActorTable, 0x40);
			Clear((long)Proto.OffEventRing, 0x80);
			Clear((long)Proto.OffWorldEntities, 0x40);
			Clear((long)Proto.OffRenderRing, 0x80);
			_view.Write(0, Proto.Magic);
			_view.Write(4, Proto.Version);
			_view.Write(8, (uint)Process.GetCurrentProcess().Id);
			_view.Write(12, 0u);
		}

		private void Clear(long offset, int bytes)
		{
			var zeros = new byte[bytes];
			_view.WriteArray(offset, zeros, 0, bytes);
		}

		[DllImport(@"C:\Windows\System32\kernel32.dll")]
		private static extern ulong GetTickCount64();
		[DllImport(@"C:\Windows\System32\kernel32.dll")]
		private static extern bool QueryPerformanceCounter(out long value);
		[DllImport(@"C:\Windows\System32\kernel32.dll")]
		private static extern bool QueryPerformanceFrequency(out long value);

		public static long Qpc()
		{
			QueryPerformanceCounter(out var v);
			return v;
		}

		public static readonly long QpcFrequency = InitQpcFrequency();

		private static long InitQpcFrequency()
		{
			QueryPerformanceFrequency(out var f);
			return f == 0 ? 1 : f;
		}

		public void Heartbeat()
		{
			_view.Write(0x10, GetTickCount64());
		}

		public bool MinecraftAlive
		{
			get
			{
				var beat = _view.ReadUInt64(0x18);
				return beat != 0 && GetTickCount64() - beat < 8000;
			}
		}

		public uint MinecraftPid => _view.ReadUInt32(12);

		public void WriteSky(
			uint flags,
			uint worldId,
			uint collisionEpoch,
			double x, double y, double z,
			float yaw, float pitch,
			uint teleportSeq,
			uint viewportW, uint viewportH,
			float gameHour = 12f)
		{
			_skySeq++;
			var seqOdd = _skySeq * 2 - 1;
			var seqEven = _skySeq * 2;
			_view.Write((long)Proto.OffSkyState, seqOdd);
			var o = (long)Proto.OffSkyState + 4;
			_view.Write(o + 0, flags);
			_view.Write(o + 4, worldId);
			_view.Write(o + 8, collisionEpoch);
			_view.Write(o + 12, x);
			_view.Write(o + 20, y);
			_view.Write(o + 28, z);
			_view.Write(o + 36, yaw);
			_view.Write(o + 40, pitch);
			_view.Write(o + 44, teleportSeq);
			_view.Write(o + 48, viewportW);
			_view.Write(o + 52, viewportH);
			_view.Write(o + 56, gameHour);
			_view.Write((long)Proto.OffSkyState, seqEven);
		}

		/// <summary>Everything Minecraft publishes in McState (protocol v10), already decoded.</summary>
		public struct McSnapshot
		{
			public uint Flags;
			public double X, Y, Z;
			public float Yaw, Pitch;
			public float EyeHeight;
			public float Sensitivity;
			public uint TeleportAck;
			public uint GuiScale;
			public ulong Frame;
			public float FovDeg, BobPhase, BobAmount;
			public double EyeX, EyeY, EyeZ;
			public long TickQpc;
			public double PrevX, PrevY, PrevZ, CurX, CurY, CurZ;
			public float TickEyeO, TickEye, WalkDistO, WalkDist, BobO, Bob, TickMs;
			public uint CameraMode;
			public uint HeldLight;
			public float CameraDistance;

			public bool InWorld => (Flags & Proto.McInWorld) != 0;
			public bool ScreenOpen => (Flags & Proto.McScreenOpen) != 0;
			public bool OnGround => (Flags & 4u) != 0;
			public bool Sneaking => (Flags & 8u) != 0;
			public bool Sprinting => (Flags & 16u) != 0;
			public bool Dead => (Flags & 32u) != 0;
			public bool Swimming => (Flags & 64u) != 0;
			public bool Flying => (Flags & 128u) != 0;
		}

		/// <summary>Reads McState under its seqlock (Minecraft writes it every frame).</summary>
		public McSnapshot ReadMc()
		{
			var o = (long)Proto.OffMcState;
			McSnapshot m = default;
			for (var attempt = 0; attempt < 8; attempt++)
			{
				var seq0 = _view.ReadUInt32(o);
				if ((seq0 & 1) != 0) continue;
				m = new McSnapshot
				{
					Flags = _view.ReadUInt32(o + 4),
					X = _view.ReadDouble(o + 8),
					Y = _view.ReadDouble(o + 16),
					Z = _view.ReadDouble(o + 24),
					Yaw = _view.ReadSingle(o + 32),
					Pitch = _view.ReadSingle(o + 36),
					EyeHeight = _view.ReadSingle(o + 40),
					Sensitivity = _view.ReadSingle(o + 44),
					TeleportAck = _view.ReadUInt32(o + 48),
					GuiScale = _view.ReadUInt32(o + 52),
					Frame = _view.ReadUInt64(o + 56),
					FovDeg = _view.ReadSingle(o + 64),
					BobPhase = _view.ReadSingle(o + 68),
					BobAmount = _view.ReadSingle(o + 72),
					EyeX = _view.ReadDouble(o + 80),
					EyeY = _view.ReadDouble(o + 88),
					EyeZ = _view.ReadDouble(o + 96),
					TickQpc = _view.ReadInt64(o + 104),
					PrevX = _view.ReadDouble(o + 112),
					PrevY = _view.ReadDouble(o + 120),
					PrevZ = _view.ReadDouble(o + 128),
					CurX = _view.ReadDouble(o + 136),
					CurY = _view.ReadDouble(o + 144),
					CurZ = _view.ReadDouble(o + 152),
					TickEyeO = _view.ReadSingle(o + 160),
					TickEye = _view.ReadSingle(o + 164),
					WalkDistO = _view.ReadSingle(o + 168),
					WalkDist = _view.ReadSingle(o + 172),
					BobO = _view.ReadSingle(o + 176),
					Bob = _view.ReadSingle(o + 180),
					TickMs = _view.ReadSingle(o + 184),
					CameraMode = _view.ReadUInt32(o + 192),
					HeldLight = _view.ReadUInt32(o + 76),
					CameraDistance = _view.ReadSingle(o + 196),
				};
				if (_view.ReadUInt32(o) == seq0) break;
			}
			return m;
		}

		public void PushInput(ushort type, ushort code, int a = 0, int b = 0, int c = 0)
		{
			var head = _view.ReadUInt64((long)Proto.OffInputRing);
			var tail = _view.ReadUInt64((long)Proto.OffInputRing + 0x40);
			if (head - tail >= 4000) return; // Minecraft isn't draining (hidden/paused): drop, never corrupt the ring
			var slot = (long)Proto.OffInputRing + 0x80 + (long)(head % 4096) * 16;
			_view.Write(slot, type);
			_view.Write(slot + 2, code);
			_view.Write(slot + 4, a);
			_view.Write(slot + 8, b);
			_view.Write(slot + 12, c);
			_view.Write((long)Proto.OffInputRing, head + 1);
		}

		public void WriteCollision(uint type, byte[] payload)
		{
			payload = payload ?? Array.Empty<byte>();
			var msg = (ulong)((8 + payload.Length + 7) & ~7);
			var colData = Proto.CollisionRingBytes - 0x80;
			var tail = _view.ReadUInt64((long)Proto.OffCollisionRing + 0x40);
			// Never lap the consumer: if Minecraft is behind, wait (the caller retries next frame).
			if (_colHead + msg + 64 - tail > colData) throw new InvalidOperationException("collision ring full");
			var pos = _colHead % colData;
			if (pos + msg > colData)
			{
				_view.Write((long)Proto.OffCollisionRing + 0x80 + (long)pos, 0u);
				_view.Write((long)Proto.OffCollisionRing + 0x80 + (long)pos + 4, 0u);
				_colHead += colData - pos;
				pos = 0;
			}
			var baseOff = (long)Proto.OffCollisionRing + 0x80 + (long)pos;
			_view.Write(baseOff, type);
			_view.Write(baseOff + 4, (uint)payload.Length);
			if (payload.Length > 0)
				_view.WriteArray(baseOff + 8, payload, 0, payload.Length);
			_colHead += msg;
			_view.Write((long)Proto.OffCollisionRing, _colHead);
		}

		/// <summary>Bytes of collision data Minecraft still has to read.</summary>
		public ulong CollisionBacklog => _colHead - _view.ReadUInt64((long)Proto.OffCollisionRing + 0x40);

		/// <summary>Acquire latest Minecraft overlay frame (HUD/hand).</summary>
		public bool TryAcquireOverlay(out int width, out int height, out bool bottomUp, out byte[] rgba, out ulong frameId)
		{
			width = height = 0;
			bottomUp = false;
			frameId = 0;
			rgba = Array.Empty<byte>();
			var state = _view.ReadUInt32((long)Proto.OffOverlayCtl);
			if ((state & Proto.OverlayDirty) == 0) return false;
			_view.Write((long)Proto.OffOverlayCtl, (uint)_overlayFront);
			_overlayFront = (int)(state & 3);
			var hdr = (long)Proto.OffOverlaySlotHdr + _overlayFront * 0x40;
			width = (int)_view.ReadUInt32(hdr);
			height = (int)_view.ReadUInt32(hdr + 4);
			var flags = _view.ReadUInt32(hdr + 8);
			frameId = _view.ReadUInt64(hdr + 0x10);
			bottomUp = (flags & 1) != 0;
			if (width <= 0 || height <= 0 || width > Proto.MaxOverlayW || height > Proto.MaxOverlayH)
				return false;
			int bytes = width * height * 4;
			if (_overlayScratch == null || _overlayScratch.Length != bytes)
				_overlayScratch = new byte[bytes];
			rgba = _overlayScratch;
			var start = (long)Proto.OffOverlayPixels + _overlayFront * (long)Proto.OverlaySlotBytes;
			_view.ReadArray(start, rgba, 0, bytes);
			return true;
		}

		/// <summary>
		/// Drain Minecraft→host render ring (atlas, sections, avatar, scene).
		/// Same layout as SkyCraft Link::DrainRender.
		/// </summary>
		public void DrainRender(Action<uint, byte[]> onMessage, ulong maxBytes = 8ul << 20)
		{
			if (_disposed || onMessage == null) return;
			var ring = (long)Proto.OffRenderRing;
			var head = _view.ReadUInt64(ring + (long)Proto.RenRingHeadOff);
			var tail = _view.ReadUInt64(ring + (long)Proto.RenRingTailOff);
			var data = ring + (long)Proto.RenRingDataOff;
			var size = Proto.RenRingDataBytes;
			ulong done = 0;
			while (tail < head && done < maxBytes)
			{
				var pos = tail % size;
				var type = _view.ReadUInt32(data + (long)pos);
				var payloadBytes = _view.ReadUInt32(data + (long)pos + 4);
				if (type == Proto.RenPad)
				{
					tail += size - pos;
					continue;
				}
				byte[] payload = Array.Empty<byte>();
				if (payloadBytes > 0 && payloadBytes < (64u << 20))
				{
					payload = new byte[payloadBytes];
					_view.ReadArray(data + (long)pos + 8, payload, 0, (int)payloadBytes);
				}
				try { onMessage(type, payload); }
				catch (Exception) { /* a bad message must not wedge the ring */ }
				var msgBytes = (8ul + payloadBytes + 7ul) & ~7ul;
				tail += msgBytes;
				done += msgBytes;
			}
			_view.Write(ring + (long)Proto.RenRingTailOff, tail);
		}

		/// <summary>A Minecraft thing the host draws itself each frame (arrow, dropped item, block cracks...).</summary>
		public struct WorldEnt
		{
			public uint Kind, Id, Tint;
			public float X, Y, Z, Yaw, Pitch, Scale;
			public float ExtX, ExtY, ExtZ;
			public float[] Uv;   // 3 x {u0, v0, u1, v1}
		}

		/// <summary>Reads the world-entity table (seqlock). Returns false if nothing is published.</summary>
		public bool ReadWorldEntities(List<WorldEnt> into, out bool hasSelection, float[] selMin, float[] selMax)
		{
			hasSelection = false;
			into.Clear();
			var b = (long)Proto.OffWorldEntities;
			for (var attempt = 0; attempt < 6; attempt++)
			{
				var seq0 = _view.ReadUInt32(b);
				if ((seq0 & 1) != 0 || seq0 == 0) continue;
				into.Clear();
				var count = (int)Math.Min(_view.ReadUInt32(b + 4), 160u);
				hasSelection = _view.ReadUInt32(b + 8) != 0;
				for (var i = 0; i < 3; i++) { selMin[i] = _view.ReadSingle(b + 0x0C + i * 4); selMax[i] = _view.ReadSingle(b + 0x18 + i * 4); }
				for (var i = 0; i < count; i++)
				{
					var r = b + 0x40 + i * 96L;
					var e = new WorldEnt
					{
						Kind = _view.ReadUInt32(r), Id = _view.ReadUInt32(r + 4),
						X = _view.ReadSingle(r + 8), Y = _view.ReadSingle(r + 12), Z = _view.ReadSingle(r + 16),
						Yaw = _view.ReadSingle(r + 20), Pitch = _view.ReadSingle(r + 24), Scale = _view.ReadSingle(r + 28),
						ExtX = _view.ReadSingle(r + 32), ExtY = _view.ReadSingle(r + 36), ExtZ = _view.ReadSingle(r + 40),
						Uv = new float[12], Tint = _view.ReadUInt32(r + 92),
					};
					for (var k = 0; k < 12; k++) e.Uv[k] = _view.ReadSingle(r + 44 + k * 4);
					into.Add(e);
				}
				if (_view.ReadUInt32(b) == seq0) return true;
			}
			return false;
		}

		/// <summary>An event Minecraft raised for the host (protocol McEvent).</summary>
		public struct McEvent
		{
			public uint Type, FormId, Flags, Weapon;
			public float A, B, C, D;
		}

		/// <summary>Reads and consumes everything Minecraft put on the event ring.</summary>
		public void DrainEvents(Action<McEvent> handler)
		{
			var ring = (long)Proto.OffEventRing;
			var head = _view.ReadUInt64(ring);
			var tail = _view.ReadUInt64(ring + 0x40);
			if (head - tail > 512) tail = head - 512;
			while (tail < head)
			{
				var slot = ring + 0x80 + (long)(tail % 512) * 32;
				var e = new McEvent
				{
					Type = _view.ReadUInt32(slot),
					FormId = _view.ReadUInt32(slot + 4),
					A = _view.ReadSingle(slot + 8),
					B = _view.ReadSingle(slot + 12),
					C = _view.ReadSingle(slot + 16),
					D = _view.ReadSingle(slot + 20),
					Flags = _view.ReadUInt32(slot + 24),
					Weapon = _view.ReadUInt32(slot + 28),
				};
				tail++;
				try { handler?.Invoke(e); } catch (Exception) { /* a bad event must not wedge the ring */ }
			}
			_view.Write(ring + 0x40, tail);
		}

		public void Dispose()
		{
			if (_disposed) return;
			_disposed = true;
			_view.Dispose();
			_mmf.Dispose();
		}
	}

	/// <summary>Builders for collision ring messages (ColRegion / ColTris payloads).</summary>
	public static class CollisionMsg
	{
		public static byte[] Clear(uint epoch) => BitConverter.GetBytes(epoch);

		public static byte[] Tris(int minX, int minY, int minZ, int maxX, int maxY, int maxZ, uint epoch, List<float[]> tris)
		{
			using (var ms = new MemoryStream(32 + tris.Count * 40))
			using (var bw = new BinaryWriter(ms))
			{
				bw.Write(minX); bw.Write(minY); bw.Write(minZ);
				bw.Write(maxX); bw.Write(maxY); bw.Write(maxZ);
				bw.Write(epoch); bw.Write(tris.Count);
				foreach (var t in tris)
				{
					for (var i = 0; i < 9; i++) bw.Write(t[i]);
					bw.Write(0u);
				}
				return ms.ToArray();
			}
		}

		/// <summary>Blocks: x, y, z and an 8x8x8 occupancy (bits[y] bit (z*8+x)).</summary>
		public static byte[] Region(int minX, int minY, int minZ, int maxX, int maxY, int maxZ, uint epoch,
			List<KeyValuePair<long[], ulong[]>> blocks)
		{
			using (var ms = new MemoryStream(32 + blocks.Count * 80))
			using (var bw = new BinaryWriter(ms))
			{
				bw.Write(minX); bw.Write(minY); bw.Write(minZ);
				bw.Write(maxX); bw.Write(maxY); bw.Write(maxZ);
				bw.Write(epoch); bw.Write(blocks.Count);
				foreach (var b in blocks)
				{
					bw.Write((int)b.Key[0]); bw.Write((int)b.Key[1]); bw.Write((int)b.Key[2]); bw.Write(0u);
					for (var i = 0; i < 8; i++) bw.Write(b.Value[i]);
				}
				return ms.ToArray();
			}
		}
	}

	/// <summary>Flat test floor (FakeHost only).</summary>
	public static class FlatPatch
	{
		public const int FloorY = 80;
		public const int OriginX = 200000;

		private static readonly ulong[] Full =
		{
			ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue,
			ulong.MaxValue, ulong.MaxValue, ulong.MaxValue, ulong.MaxValue
		};

		public static void Send(HostLink link, uint epoch)
		{
			link.WriteCollision(Proto.ColClear, CollisionMsg.Clear(epoch));
			for (var rx = OriginX / 8 - 4; rx < OriginX / 8 + 4; rx++)
			for (var rz = -4; rz < 4; rz++)
			for (var ry = FloorY / 8 - 1; ry < FloorY / 8 + 3; ry++)
			{
				link.WriteCollision(Proto.ColTris, FloorTris(rx, ry, rz, epoch));
				link.WriteCollision(Proto.ColRegion, FloorRegion(rx, ry, rz, epoch));
			}
		}

		public static byte[] PackTrisMessage(int minX, int minY, int minZ, int maxX, int maxY, int maxZ, uint epoch, List<float[]> tris)
			=> CollisionMsg.Tris(minX, minY, minZ, maxX, maxY, maxZ, epoch, tris);

		private static byte[] FloorTris(int rx, int ry, int rz, uint epoch)
		{
			var tris = new List<float[]>();
			var y = (float)FloorY;
			if (ry * 8 <= FloorY - 1 && FloorY - 1 <= ry * 8 + 7)
			{
				float x0 = rx * 8f, z0 = rz * 8f, x1 = x0 + 8f, z1 = z0 + 8f;
				tris.Add(new[] { x0, y, z0, x1, y, z0, x1, y, z1 });
				tris.Add(new[] { x0, y, z0, x1, y, z1, x0, y, z1 });
			}
			return CollisionMsg.Tris(rx * 8, ry * 8, rz * 8, rx * 8 + 7, ry * 8 + 7, rz * 8 + 7, epoch, tris);
		}

		private static byte[] FloorRegion(int rx, int ry, int rz, uint epoch)
		{
			var blocks = new List<KeyValuePair<long[], ulong[]>>();
			for (var by = 0; by < 8; by++)
			for (var bz = 0; bz < 8; bz++)
			for (var bx = 0; bx < 8; bx++)
			{
				if (ry * 8 + by != FloorY - 1) continue;
				blocks.Add(new KeyValuePair<long[], ulong[]>(new long[] { rx * 8 + bx, ry * 8 + by, rz * 8 + bz }, Full));
			}
			return CollisionMsg.Region(rx * 8, ry * 8, rz * 8, rx * 8 + 7, ry * 8 + 7, rz * 8 + 7, epoch, blocks);
		}
	}
}
