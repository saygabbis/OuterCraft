namespace WildCraft
{
	/// <summary>Mirrors protocol/skycraft_protocol.h (v10) — host creates, Minecraft opens.</summary>
	public static class Proto
	{
		public const uint Magic = 0x43594B53; // "SKYC"
		public const uint Version = 10;
		public const string DefaultMappingName = @"Local\SkyCraft_v1";

		public const ulong OffHeader = 0x0;
		public const ulong OffSkyState = 0x100;
		public const ulong OffMcState = 0x200;
		public const ulong OffOverlayCtl = 0x300;
		public const ulong OffOverlaySlotHdr = 0x340;
		public const ulong OffInputRing = 0x1000;
		public const ulong OffActorTable = 0x12000;
		public const ulong OffEventRing = 0x17000;
		public const ulong OffWorldEntities = 0x1C000;
		public const ulong OffCollisionRing = 0x20000;
		public const ulong CollisionRingBytes = 32ul << 20;
		public const ulong OffOverlayPixels = OffCollisionRing + CollisionRingBytes;
		public const int MaxOverlayW = 3840;
		public const int MaxOverlayH = 2160;
		public const ulong OverlaySlotBytes = (ulong)MaxOverlayW * MaxOverlayH * 4;
		public const int OverlaySlots = 3;
		public const ulong OffRenderRing = OffOverlayPixels + OverlaySlotBytes * OverlaySlots;
		public const ulong RenderRingBytes = 64ul << 20;
		public const ulong MappingBytes = OffRenderRing + RenderRingBytes;

		public const uint SkyInGame = 1u << 0;
		public const uint McInWorld = 1u << 0;

		public const ushort InKey = 1;
		public const ushort InMouseButton = 2;
		public const ushort InScroll = 3;
		public const ushort InCursor = 4;
		public const ushort InText = 5;
		public const ushort InReleaseAll = 6;
		public const ushort InHurt = 7;
		public const ushort InOpenMenu = 8;
		public const ushort InVelocity = 9;

		public const uint ColClear = 1;
		public const uint ColRegion = 2;
		public const uint ColTris = 3;

		public const uint OverlayDirty = 1u << 2;
		public const uint McScreenOpen = 1u << 1;
		public const uint SkyMenuOpen = 1u << 1;
		public const uint SkyLoading = 1u << 2;
		public const uint SkySoftTeleport = 1u << 3;

		// Render ring (MC → host): block meshes, atlas, Steve, entities — same as SkyCraft WorldRender.
		public const uint RenPad = 0;
		public const uint RenAtlas = 1;
		public const uint RenSection = 2;
		public const uint RenClearAll = 3;
		public const uint RenTexture = 4;
		public const uint RenAvatar = 5;
		public const uint RenScene = 6;
		public const uint RenAtlasRegion = 7;
		public const uint RenLights = 8;
		public const uint RenRagdoll = 9;
		public const uint RenSolids = 10;
		public const int RenVertexBytes = 32;
		public const ulong RenRingHeadOff = 0x00;
		public const ulong RenRingTailOff = 0x40;
		public const ulong RenRingDataOff = 0x80;
		public const ulong RenRingDataBytes = RenderRingBytes - RenRingDataOff;
	}
}
