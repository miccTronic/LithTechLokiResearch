using AllOtherResourceImporters;
using ConversionLib;
using HelixToolkit.SharpDX.Core;
using MFGames.ITS.Scenes3D;
using MFSTools;
using SharpDX;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace AllOtherResourceImporters.LithTech;

/// <remarks>
/// World file format used LT Jupiter Ex version from LT4 (.world00p) and LT5 (.wld).
/// Based on https://github.com/Five-Damned-Dollarz/io_scene_jupex
/// and https://gist.github.com/Five-Damned-Dollarz/56fb8f497056ec443021ea4aad71409c (FEAR)
/// and https://gist.github.com/Five-Damned-Dollarz/7f7223f5128a495777212685244dd229 (FEAR2)
/// </remarks>
public class LithtechJupiterExWorldFile : IWorldFile
{
	public const string CsvLoggerHeader = "filePath;possibleMagic;Version;render_data_pos;sector_data_pos;object_data_pos;streaming_data_pos;subdivs;#BSP_Names;#BSP_Names_Len;#Planes;#BSPs;node_count;polygon_count;vertex_type_count;vertex_count;physics_shapes_count;{lokiItemsCount};{lokiTotalChildCount};{strLength};{sUnk1};{sUnk2};{sUnk3};{sUnk4};{numMeshesRepeat};{strCountPlusX};{aratanaCount};{unk1};{unk2};{nodeCountY};{unk4};{unk5};{unk6};{lokiGlobalFileIndexes};{lokiMaxA};{renderNodeCount};{totalFaceCount};{totalRenderMeshCount};{embeddedRenderMeshCount};{totalMaterialCount};{instancesCount};{instancesCountInRenderTree};{ciUnk1};{ciUnk2};{ciUnk3};meshFaceCount;unkCount1;unkCount2;unkCount3;MeshUnk1;surfaceCount;materialCount;vertexDataSize;trianglesDataSize;vertexTypeCount;facesCount;numSectors;numPortals;numSectorNodes;iUnk2;iUnk3;iUnk4;iUnk5;iUnk6;ObjCount";
	public const bool DumpFileStructure = false, DumpRenderTree = false, DumpLokiInstances = false, DumpSectors = false, DumpBundleContents = false;

	public string FileName { get; }
	private readonly string GameDir;
	private readonly Dictionary<string, LithtechBndlFile> Bundles = new(StringComparer.InvariantCultureIgnoreCase);
	private readonly Dictionary<string, (LithtechBndlFile, IBundleFile.FileEntry)> BundleContents = new(StringComparer.InvariantCultureIgnoreCase);

	public WorldVersions Version { get; }
	/// <summary>World extents.</summary>
	public BoundingBox BoundingBox { get; }
	public BoundingBox BoundingBox2 { get; }
	/// <summary>Jupiter only. Offset to convert from this world to the source world.</summary>
	public Vector3 Offset { get; }

	/// <summary>Used for rendering.</summary>
	public RenderMesh[] Meshes { get; }
	/// <summary>Used for physics and collision detection, NOT for rendering.</summary>
	public List<WorldBSP> BSPs { get; } = new();
	public Vector3[] PlaneNormals { get; }
	public Dictionary<string, WorldBSP> ModelsByName { get; } = new(StringComparer.InvariantCultureIgnoreCase);
	public List<WorldObject> Objects { get; }
	public Dictionary<string, WorldObject> ObjectsByName { get; } = new(StringComparer.InvariantCultureIgnoreCase);
	IEnumerable<(string, string)> IWorldFile.PropertiesAsDict => [ ];
	private Dictionary<string, PrefabFile> PrefabCacheById = new(StringComparer.InvariantCultureIgnoreCase);

	public readonly int SubdivisionFlagsCount, TotalNodeCount, TotalPolygonCount, TotalVertexTypeCount, TotalVertexCount, prefabDefsTotalChildCount = -1, TotalFileStringsCount;
	public readonly int MeshLoadInfoCount, FileSectionStringsCountApprox, PortalCount, TotalMaterialCount;
	public readonly int RenderTreeFaceCount, RenderMeshCount, EmbeddedRenderMeshCount, TotalInstancesCount, RenderTreeInstancesCount, RenderTreeSupportBoxesCount;
	public readonly uint MeshLoadInfosMaxRenderNodeLT5 = 0;
	public string[] GlobalFileIndexesLT5;
	public RenderNode[] RenderNodesLT5; // Other than LT4, these are NOT per BSP.
	public InstanceInfo[] InstancesLT5;
	public RenderMesh GlobalMeshLT5;
	public Dictionary<int, int[]> PrefabNodeMapLT5; // maps hashed prefab names to (ordered) hashed node names
	private uint[] GlobalUnkArrayLT5;
	private string FileSectionUnksA, FileSectionUnksB, RenderMeshUnks;

	public LithtechJupiterExWorldFile(string filePath, Games game, Engines engine, StringBuilder logger, string gameDir)
	{
		GameDir = gameDir;
		FileName = filePath;
		var br = new CustomBinaryReader(filePath, Encoding.ASCII);
		CustomBinaryReader sbr, cbr;

		var possibleMagic = br.ReadString(4);
		bool isSplit = possibleMagic == "WLDP";
		if (isSplit) {
			// Only for "Condemend"
			sbr = new CustomBinaryReader(Path.ChangeExtension(filePath, engine switch {
				Engines.LT4_JupiterEX => ".WorldServer00p",
				Engines.LT5 => ".WldSrvr",
				_ => throw new NotImplementedException()
			}), Encoding.ASCII);
			cbr = new CustomBinaryReader(Path.ChangeExtension(filePath, engine switch {
				Engines.LT4_JupiterEX => ".WorldClient00p",
				Engines.LT5 => ".WldClnt",
				_ => throw new NotImplementedException()
			}), Encoding.ASCII);
			Trace.Assert(sbr.ReadString(4) == "WLDS");
			Trace.Assert(cbr.ReadString(4) == "WLDC");
		} else {
			br.Position -= 4;
			sbr = br; cbr = br;
		}
		Version = (WorldVersions)br.ReadInt32();
		if (Version != WorldVersions.Fear && Version != WorldVersions.Loki) throw new FormatException($"Unsupported file version: {Version}");
		if (isSplit) {
			Trace.Assert(sbr.ReadInt32() == (int)Version);
			Trace.Assert(cbr.ReadInt32() == (int)Version);
		}
		logger?.Append($"{filePath};{possibleMagic};{(int)Version}");

		// === Common Part ===
		int render_data_pos = -1, sector_data_pos = -1, object_data_pos = -1, streaming_data_pos = -1;
		if (!isSplit) {
			render_data_pos = br.ReadInt32();
			sector_data_pos = br.ReadInt32();
			object_data_pos = br.ReadInt32();
			streaming_data_pos = br.ReadInt32();
		}
		BoundingBox = br.ReadBoundingBoxDX();
		Offset = br.ReadVector3DX();
		logger?.Append($";{render_data_pos};{sector_data_pos};{object_data_pos};{streaming_data_pos}");

		// Read World Model
		if (game == Games.FEAR) Debug.Assert(br.Position == 56);
		BoundingBox2 = br.ReadBoundingBoxDX();
		SubdivisionFlagsCount = br.ReadInt32(); // some node count? not clear to which these flags map.
		var wmUnk1 = Version < WorldVersions.Loki ? br.ReadInt32() : 0; Debug.Assert(wmUnk1 == 0);
		var wmRaw = br.ReadBytes((int)Math.Ceiling(SubdivisionFlagsCount / 8f)); // each byte = 8 boolean flags
		var counts = br.ReadTArray<int>(8);
		for (int i = 0; i < counts.Length; i++) counts[i] ^= game switch {
			Games.FEAR => 399,
			Games.Condemned => 0,
			Games.District187 => 246,
			Games.FEAR2 => 0,
			_ => throw new NotImplementedException()
		};
		(var bsp_name_count, var bsp_names_length, var plane_count, var bspCount, TotalNodeCount, TotalPolygonCount, TotalVertexTypeCount, TotalVertexCount) = (counts[0], counts[1], counts[2], counts[3], counts[4], counts[5], counts[6], counts[7]);
		if (Version >= WorldVersions.Loki) {
			// Maybe offsets to sub-meshes...? Could also be a float array, but less likely to me...
			GlobalUnkArrayLT5 = br.ReadTArray<uint>(br.ReadInt32());
		}
		var bsp_names = new CustomBinaryReader(new MemoryStream(br.ReadBytes(bsp_names_length)));
		//if (Version >= WorldVersions.Loki) br.AlignPosition(4);
		var bsp_name_indices = new (int StringIndex, int BspId)[bsp_name_count];
		for (int i = 0; i < bsp_name_count; i++) bsp_name_indices[i] = (br.ReadInt32(), br.ReadInt32());
		// There can be multiple names for each BSP!
		var stringsByBsp = new Dictionary<int, List<string>>();
		foreach (var (StringIndex, BspId) in bsp_name_indices) {
			bsp_names.Position = StringIndex;
			if (!stringsByBsp.TryGetValue(BspId, out var namesList)) {
				namesList = new();
				stringsByBsp.Add(BspId, namesList);
			}
			namesList.Add(bsp_names.ReadStringNullTerminated());
		}
		PlaneNormals = br.ReadTArray<Vector3>(plane_count);
		// Read BSPs (World Models)
		BSPs = new(bspCount);
		for (int i = 0; i < bspCount; i++) {
			var bsp = new WorldBSP(br, this, i, stringsByBsp.GetValueOrDefault(i));
			stringsByBsp.Remove(i);
			BSPs.Add(bsp);
			if (bsp.Names != null) {
				foreach (var name in bsp.Names) {
					if (!ModelsByName.TryAdd(name, bsp)) {
						Debug.WriteLine($"! Warning: Duplicate model name \"{name}\" in BSP #{i}");
					}
				}
			}
		}
		Debug.Assert(stringsByBsp.Count == 0);
		if (Version < WorldVersions.Loki) {
			// Blocker Polygons (?)
			var count1 = br.ReadInt32();
			var count1b = br.ReadInt32(); // = total "vec" count, e.g. count1 * 4
			for (int i = 0; i < count1; i++) {
				var plane = br.ReadStruct<LTPlane>();
				var vectors = br.ReadTArray<Vector3>(br.ReadInt32());
			}
		}

		// Physics Shapes
		var physicsShapesCount = br.ReadInt32();
		Debug.Assert(physicsShapesCount == bspCount);
		PhysicsShape ReadShape()
		{
			var pos = br.ReadVector3DX();
			var rot = br.ReadQuaternionDX();
			var type = (PhysicsShapeType)br.ReadInt32();
			switch (type) {
				case PhysicsShapeType.Null:
					return new PhysicsShape(pos, rot, type);
				case PhysicsShapeType.Mesh:
					return new PhysicsShapeMesh(pos, rot, type, br);
				case PhysicsShapeType.OBB:
					return new PhysicsShapeOBB(pos, rot, type, br);
				case PhysicsShapeType.Sphere:
					return new PhysicsShapeSphere(pos, rot, type, br);
				case PhysicsShapeType.Hull:
					return new PhysicsShapeHull(pos, rot, type, br);
				case PhysicsShapeType.Capsule:
					return new PhysicsShapeCapsule(pos, rot, type, br);
				case PhysicsShapeType.SubShapes:
					var childCount = br.ReadInt32();
					var parent = new PhysicsShape(pos, rot, type) {
						Children = new(childCount)
					};
					for (int j = 0; j < childCount; j++) {
						var peek = br.ReadInt32();
						if (peek == 0 || peek > 0x10) {
							br.Position -= 4;
							break;
						} else if (peek == (int)PhysicsShapeType.Capsule) {
							parent.Children.Add(new PhysicsShapeCapsule(pos, rot, type, br));
						} else {
							parent.Children.Add(ReadShape());
						}
					}
					return parent;
				default:
					throw new NotImplementedException("Unknown physics shape type: " + type.ToString());
			}
		}
		for (int i = 0; i < physicsShapesCount; i++) {
			BSPs[i].Shape = ReadShape();
		}
		logger?.Append($";{SubdivisionFlagsCount};{bsp_name_count};{bsp_names_length};{plane_count};{bspCount};{TotalNodeCount};{TotalPolygonCount};{TotalVertexTypeCount};{TotalVertexCount};{physicsShapesCount}");

		Dictionary<string, MeshLoadInfo> meshLoadInfos = null;
		// Files Section
		{
			if (Version >= WorldVersions.Loki) {
				// This lists the prefabs used in this package (by their IDs), and the render nodes they contain.
				// This is used as a map, as sometimes the order in the .inst file differs.
				var prefabDefsCount = br.ReadInt32();
				PrefabNodeMapLT5 = new(prefabDefsCount);
				prefabDefsTotalChildCount = br.ReadInt32(); // prefab render tree nodes (sum over all prefabs)
				for (int i = 0; i < prefabDefsCount; i++) {
					PrefabNodeMapLT5.Add(br.ReadInt32(), br.ReadTArray<int>(br.ReadInt32()));
				}
			}

			var strLength = br.ReadInt32();
			ushort sUnk1 = br.ReadUInt16(), sUnk2 = br.ReadUInt16(), sUnk3 = br.ReadUInt16(), sUnk4 = br.ReadUInt16();
			FileSectionUnksA = $"{sUnk1} {sUnk2} {sUnk3} {sUnk4}";
			MeshLoadInfoCount = br.ReadInt32(); // differs by 1?
			FileSectionStringsCountApprox = br.ReadInt32();
			PortalCount = br.ReadInt32();
			var fsUnk1 = br.ReadInt32();
			var fsUnk2 = br.ReadInt32();
			var nodeCountY = br.ReadInt32();
			var fsUnk3 = br.ReadInt32();
			var fsUnk4 = br.ReadInt32(); // max render node?
			var fsUnk5 = Version >= WorldVersions.Loki ? br.ReadInt32() : 0;
			FileSectionUnksB = $"{fsUnk1} {fsUnk2} {fsUnk3} {fsUnk4} {fsUnk5}";
			using var sbr2 = br.ReadBytes(strLength).AsCustomBinaryReader();
			// Count the strings just for statistics
			TotalFileStringsCount = 0;
			while (!sbr2.EOS) {
				var s = sbr2.ReadStringNullTerminated();
				TotalFileStringsCount++;
			}
			sbr2.AlignPosition(4);
			string ReadStringFromTable(int offset = -1)
			{
				if (offset < 0) offset = br.ReadInt32();
				sbr2.Position = offset;
				return sbr2.ReadStringNullTerminated();
			}

			if (Version < WorldVersions.Loki) {
				var idxCount = br.ReadInt32();
				for (int i = 0; i < idxCount; i++) {
					var folder = ReadStringFromTable();
					var file = ReadStringFromTable();
					if (DumpFileStructure) Debug.WriteLine($"AssetDir #{i}: {file} --> {folder}");
				}
			} else {

				string LocateBundle(string bundleFile)
				{
					var newFile = Path.Combine(gameDir, bundleFile);
					if (File.Exists(newFile)) return newFile;
					if (game == Games.FEAR2) {
						newFile = Path.Combine(gameDir, "DLC01", bundleFile);
						if (File.Exists(newFile)) return newFile;
						newFile = Path.Combine(gameDir, "DLC02", bundleFile);
						if (File.Exists(newFile)) return newFile;
						newFile = Path.Combine(gameDir, "DLC03", bundleFile);
						if (File.Exists(newFile)) return newFile;
					}
					return null;
				}
				bool CacheBundle(string bundleName)
				{
					if (Bundles.ContainsKey(bundleName)) return false;
					if (DumpBundleContents) Debug.WriteLine($"Caching bundle: {bundleName}");
					var bundleFile = LocateBundle(bundleName);
					if (bundleFile == null) return false;
					var bundle = new LithtechBndlFile(bundleFile);
					Bundles.Add(bundleName, bundle);
					foreach (var file in bundle.Files) {
						BundleContents.Add(file.Path, (bundle, file));
						if (DumpBundleContents) Debug.WriteLine($"   Entry {file}");
					}
					return true;
				}

				// String offsets, probably to files that are required for all meshes (including bundles)
				var unk7 = br.ReadInt32(); Debug.Assert(unk7 == 0);
				var lokiGlobalFileIndexes = br.ReadInt32();
				GlobalFileIndexesLT5 = new string[lokiGlobalFileIndexes];
				for (int i = 0; i < lokiGlobalFileIndexes; i++) {
					string file = ReadStringFromTable();
					GlobalFileIndexesLT5[i] = file;
					if (DumpFileStructure) Debug.WriteLine($"GlobalFile #{i}: {file}");
					if (Path.GetExtension(file).Equals(".bndl", StringComparison.InvariantCultureIgnoreCase)) CacheBundle(file);
				}

				// Assets per (external?) Mesh
				meshLoadInfos = new(MeshLoadInfoCount, StringComparer.InvariantCultureIgnoreCase);
				int nodeCount = 0;
				for (int i = 0; i < MeshLoadInfoCount; i++) {
					var mi = new MeshLoadInfo {
						Name = ReadStringFromTable(),
						ActivateMsg = ReadStringFromTable(),
						DeactivateMsg = ReadStringFromTable(),
						Sound = ReadStringFromTable(),
						TypeId = br.ReadUInt32(),
						RenderNodes = br.ReadTArray<uint>(br.ReadInt32()), // These are the rendernodes used by this mesh
						RequiredBundles = br.ReadTArray<int>(br.ReadInt32()).Select(ReadStringFromTable).ToArray(),
					};
					if (DumpFileStructure) Debug.WriteLine($"AssetLink #{i} \"{mi.Name}\":\tID = {mi.TypeId}\t#Nodes = {mi.RenderNodes.Length}\t#Bundles = {mi.RequiredBundles.Length}\tActivate = \"{mi.ActivateMsg}\"\tDeactivate = \"{mi.DeactivateMsg}\"\tSnd = \"{mi.Sound}\"\n    Nodes = {mi.RenderNodes.Implode()}");
					foreach (var bundleFile in mi.RequiredBundles) {
						CacheBundle(bundleFile);
						if (DumpFileStructure) Debug.WriteLine($"   Bundle: {bundleFile}");
					}
					if (mi.RenderNodes.Length > 0) MeshLoadInfosMaxRenderNodeLT5 = Math.Max(MeshLoadInfosMaxRenderNodeLT5, mi.RenderNodes.Max());
					meshLoadInfos.Add(mi.Name, mi);
					nodeCount += mi.RenderNodes.Length;
				}
				if (DumpFileStructure) Debug.WriteLine($"MAX: {MeshLoadInfosMaxRenderNodeLT5}");
			}
			logger?.Append($";{PrefabNodeMapLT5?.Count};{prefabDefsTotalChildCount};{strLength};{sUnk1};{sUnk2};{sUnk3};{sUnk4};{MeshLoadInfoCount};{FileSectionStringsCountApprox};{PortalCount};{fsUnk1};{fsUnk2};{nodeCountY};{fsUnk3};{fsUnk4};{fsUnk5};{GlobalFileIndexesLT5?.Length};{MeshLoadInfosMaxRenderNodeLT5}");
		}
		// TODO: Some more data following in Loki (SectorPortals, SectorNodes, some_mesh_info, Streaming...?)

		if (isSplit) { br?.Dispose(); br = null; } // for safety

		// === Client Part ===
		// Reder render data
		if (!isSplit) br.Position = render_data_pos;
		var bspCount2 = cbr.ReadInt32();
		Debug.Assert(bspCount2 == bspCount); // The render tree should map the BSPs to RenderMeshes
		var renderNodeCount = cbr.ReadInt32(); // actual node count
		RenderTreeFaceCount = cbr.ReadInt32(); // faces referenced in RenderNodes

		//int totalRenderMeshCount = -1, instancesCount = -1, instancesCountInRenderTree = -1, totalMaterialCount = -1, embeddedRenderMeshCount = 0, totalSupportBoxesCount = -1;
		int ciUnk1 = -1, ciUnk2 = -1, ciUnk3 = -1;
		if (Version < WorldVersions.Loki) {
			RenderMeshCount = cbr.ReadInt32();
			TotalMaterialCount = cbr.ReadInt32();
			EmbeddedRenderMeshCount = cbr.ReadInt32();
		} else {
			TotalInstancesCount = cbr.ReadInt32();
			RenderTreeInstancesCount = cbr.ReadInt32(); // Same as summing up instances in all RenderNodes
			ciUnk1 = cbr.ReadInt32();
			RenderMeshCount = cbr.ReadInt32();
			ciUnk2 = cbr.ReadInt32();
			RenderTreeSupportBoxesCount = cbr.ReadInt32();
			ciUnk3 = cbr.ReadInt32();
			RenderMeshUnks = $"{ciUnk1} {ciUnk2} {ciUnk3}";
		}
		Debug.Assert(Version >= WorldVersions.Loki || EmbeddedRenderMeshCount == 1);
		//Debug.Assert(Version < WorldVersions.Loki || MeshLoadInfoCount == RenderMeshCount);
		logger?.Append($";{renderNodeCount};{RenderTreeFaceCount};{RenderMeshCount};{EmbeddedRenderMeshCount};{TotalMaterialCount};{TotalInstancesCount};{RenderTreeInstancesCount};{ciUnk1};{ciUnk2};{ciUnk3}");

		Meshes = new RenderMesh[RenderMeshCount];
		for (int i = 0; i < EmbeddedRenderMeshCount; i++) {
			Debug.Assert(i == 0, "Untested!");
			var mesh = new RenderMesh(cbr, this, i, logger, true, null);
			Meshes[i] = mesh;
		}

		// External RenderMeshes
		for (int i = EmbeddedRenderMeshCount; i < RenderMeshCount; i++) {
			int iUnk7 = cbr.ReadInt32(); Debug.Assert(iUnk7 == 1);
			int meshMaterialsCount = cbr.ReadInt32();
			var meshFile = cbr.ReadStringPrefixedInt16();
			int iUnk8 = cbr.ReadInt32(); Debug.Assert(iUnk8 == 0);
			var meshMaterials = ReadLTStringArray(cbr, meshMaterialsCount);
			using var brMesh = ResolveDataStream(meshFile);
			var mesh = new RenderMesh(brMesh, this, i, logger, false, meshMaterials) {
				ExternalFileName = meshFile
			};
			if (meshLoadInfos != null) {
				string meshId = Path.GetFileNameWithoutExtension(meshFile);
				if (meshId.Contains('.')) meshId = meshId[(meshId.LastIndexOf('.') + 1)..];
				if (meshLoadInfos.TryGetValue(meshId, out var mi)) {
					mesh.InfoLT5 = mi;
					meshLoadInfos.Remove(mi.Name);
				}
				if (meshId.Equals("global", StringComparison.InvariantCultureIgnoreCase)) GlobalMeshLT5 = mesh;
			}
			Meshes[i] = mesh;
		}
		if (meshLoadInfos != null && meshLoadInfos.Count > 0) Debug.WriteLine($"!! Failed to assign {meshLoadInfos.Count} MeshLoadInfos, including mesh \"{meshLoadInfos.First().Key}\"");

		// RenderTree
		if (Version < WorldVersions.Loki) {
			// *** LT4 ***
			// The RenderTree can map render meshes to WorldModels (BSPs)
			for (int i = 0; i < bspCount2; i++) {
				var wm = BSPs[i];
				if (DumpRenderTree) Debug.WriteLine($"RenderTree #{i}");
				var nodeCount = cbr.ReadInt32();
				wm.RenderNodes = new RenderNode[nodeCount];
				for (int j = 0; j < nodeCount; j++) {
					if (DumpRenderTree) Debug.WriteLine($"   RenderNode #{j}");
					var numFaces = cbr.ReadInt32(); // never 0
					var numSupportBoxes = cbr.ReadInt32(); // often 0
					var node = new RenderNode() {
						Index = j,
						BSP_LT4 = wm,
						RenderFaces = new Face[numFaces],
						FaceFlags = new int[numFaces],
						SupportBoxes = new(numSupportBoxes),
					};
					for (int k = 0; k < numFaces; k++) {
						// Seems to be reference to a face and its bounding box...
						var bbNode = cbr.ReadBoundingBoxDX();
						int iShadowFlag = cbr.ReadInt32(), idxMesh = cbr.ReadInt32(), idxFace = cbr.ReadInt32();
						Debug.Assert(iShadowFlag >= 0 && iShadowFlag <= 3); // != 0 for shadow volumes. Often 1 for the first entry. Also seen to be 2/3. Is it a flag?
						if (DumpRenderTree) Debug.WriteLine($"      Box #{k}: \t{iShadowFlag}\t{idxMesh}\t{idxFace}\t{bbNode.ToStringMF()}");
						if (Meshes[idxMesh] != null) {
							var face = Meshes[idxMesh].Faces[idxFace];
							if (DumpRenderTree) Debug.WriteLine($"         VertexPos: \t\t\t{face.Vertices[0].Position.ToStringMF()} --- {Meshes[idxMesh].Materials[face.MaterialId]}");
							face.Bounds = bbNode;
							Debug.Assert(face.RenderNode_LT4 == null); // if this assertion failes, do it like for LT5.
							face.RenderNode_LT4 = wm.RenderNodes[j];
							face.IsShadowVolume = iShadowFlag != 0;
							node.RenderFaces[k] = face;
						}
						node.FaceFlags[k] = iShadowFlag;
					}
					for (int k = 0; k < numSupportBoxes; k++) {
						var vectors = cbr.ReadTArray<Vector3>(cbr.ReadByte());
						node.SupportBoxes.Add(vectors);
						if (DumpRenderTree) {
							Debug.WriteLine($"      VecSet #{k}: \t{vectors.Length}");
							for (int l = 0; l < vectors.Length; l++) {
								Debug.WriteLine($"         Vec #{l}: {vectors[l].ToStringMF()}");
							}
						}
					}
					wm.RenderNodes[j] = node;
				}
			}
		} else {
			// *** LT5 ***
			// Global Instances
			InstancesLT5 = new InstanceInfo[TotalInstancesCount];
			for (int i = 0; i < TotalInstancesCount; i++) {
				var info = new InstanceInfo(cbr, i);
				InstancesLT5[i] = info;
				if (DumpLokiInstances) Debug.WriteLine($"Instance #{i}: {info}");
			}

			// Render Nodes
			int max2 = -1, check_totalFaceCount = 0, check_totalSupportBoxesCount = 0, check_instancesCountInRenderTree = 0;
			RenderNodesLT5 = new RenderNode[renderNodeCount];
			for (int j = 0; j < renderNodeCount; j++) {
				// TODO: How to get BSP index?
				//var wm = BSPs[i];
				//wm.RenderNodes = new RenderNode[1];
				var numFaces = cbr.ReadInt32(); check_totalFaceCount += numFaces;
				var numSupportBoxes = cbr.ReadInt32(); check_totalSupportBoxesCount += numSupportBoxes;
				var numInstances = cbr.ReadInt32(); check_instancesCountInRenderTree += numInstances;
				int idxMesh = cbr.ReadInt32();
				max2 = Math.Max(max2, idxMesh);
				if (DumpRenderTree) Debug.WriteLine($"RenderNode #{j} (Mesh {idxMesh})");
				var node = new RenderNode() {
					Index = j,
					//BSP = wm,
					Mesh_LT5 = idxMesh >= 0 ? Meshes[idxMesh] : null,
					RenderFaces = new Face[numFaces],
					FaceFlags = new int[numFaces],
					SupportBoxes = new(numSupportBoxes),
				};
				for (int k = 0; k < numFaces; k++) {
					// Seems to be reference to a face and its bounding box...
					var bbFace = cbr.ReadBoundingBoxDX();
					int iShadowFlag = cbr.ReadInt32(), idxFace = cbr.ReadInt32();
					if (DumpRenderTree) Debug.WriteLine($"   Box #{k}: \tS={iShadowFlag}\tF={idxFace}\t{bbFace.ToStringMF()}");
					Debug.Assert(idxMesh >= 0); // ok in Loki
					var face = Meshes[idxMesh].Faces[idxFace];
					//Debug.WriteLine($"      VertexPos: \t\t\t{face.Vertices[0].Position.ToStringMF()} --- {Meshes[idxMesh].Materials[face.MaterialId]}");
					face.Bounds = bbFace;
					//face.RenderNode = wm.RenderNodes[0];
					node.RenderFaces[k] = face;
					face.IsShadowVolume = iShadowFlag != 0;
					face.RenderNodes_LT5 ??= [];
					face.RenderNodes_LT5.Add(node);
					node.FaceFlags[k] = iShadowFlag;
				}
				for (int k = 0; k < numSupportBoxes; k++) {
					var vectors = cbr.ReadTArray<Vector3>(cbr.ReadByte());
					node.SupportBoxes.Add(vectors);
					if (DumpRenderTree) Debug.WriteLine($"   VecSet #{k}: \t{vectors.Length}");
					for (int l = 0; l < vectors.Length; l++) {
						if (DumpRenderTree) Debug.WriteLine($"      Vec #{l}: {vectors[l].ToStringMF()}");
					}
				}
				if (numInstances > 0) {
					node.InstancesLT5 = cbr.ReadTArray<int>(numInstances);
					if (DumpRenderTree) Debug.WriteLine($"   Instances ({numInstances}):\t{node.InstancesLT5.Implode(", ")}");
					// It appears that an instance CAN be assigned to multiple RenderNodes.
					foreach (var instIdx in node.InstancesLT5) {
						InstancesLT5[instIdx].RenderNodes.Add(node);
					}
				}
				RenderNodesLT5[j] = node;
				//wm.RenderNodes[0] = node;
			}
			Debug.Assert(check_totalFaceCount == RenderTreeFaceCount);
			Debug.Assert(check_totalSupportBoxesCount == RenderTreeSupportBoxesCount);
			Debug.Assert(check_instancesCountInRenderTree == RenderTreeInstancesCount);

			// Instance Placement (one per BSP)
			// Instances are often spread across multiple BSPs, with the InstNodeIndex increasing. They may jump between a "type 1" and a "placement".
			int max5a = -1, max5b = -1, max5c = -1, max5d = -1, max7 = -1;
			for (int i = 0; i < bspCount; i++) {
				var wm = BSPs[i];
				var data = new LokiRenderInstanceData();
				data.Type = cbr.ReadInt32();
				Debug.Assert(data.Type == 0 || data.Type == 1);
				if (data.Type == 0) {
					// Reference to RenderFace?
					data.MeshIndex = cbr.ReadInt32();
					Debug.Assert(data.MeshIndex == 0 || data.MeshIndex == -1); // not really sure if it's an index!
					data.FaceIndex = cbr.ReadInt32();
					data.FaceCount = cbr.ReadInt32();
					data.unk3a = cbr.ReadByte(); data.unk3b = cbr.ReadByte(); data.unk3c = cbr.ReadByte(); data.unk3d = cbr.ReadByte(); // often 0-4. maybe vertex order?
					if (DumpLokiInstances) Debug.WriteLine($"Inst_WM #{i} Type 0: Mesh=#{data.MeshIndex}\tFace=#{data.FaceIndex}\tUnk2={data.FaceCount}\tBytes={data.unk3a} {data.unk3b} {data.unk3c} {data.unk3d}");
					if (data.MeshIndex >= 0 && data.FaceIndex >= 0) {
						for (int j = 0; j < data.FaceCount; j++) {
							var face = Meshes[data.MeshIndex].Faces[data.FaceIndex + j];
							face.BSPs_LT5 ??= [];
							face.BSPs_LT5.Add(wm);
						}
					}
					max5a = Math.Max(max5a, data.unk3a);
					max5b = Math.Max(max5b, data.unk3b);
					max5c = Math.Max(max5c, data.unk3c);
					max5d = Math.Max(max5d, data.unk3d);
				} else if (data.Type == 1) {
					// Reference to .inst file & instance render node ID?
					data.InstNodeIndex = cbr.ReadInt32();
					data.InstFileName = cbr.ReadStringPrefixedInt16();
					if (DumpLokiInstances) Debug.WriteLine($"Inst_WM #{i} Type 1: InstIdx={data.InstNodeIndex}\tInstance = \"{data.InstFileName}\"");
				} else {
					throw new NotSupportedException();
				}
				data.InstancePlacements = new InstancePlacement[cbr.ReadInt32()];
				//Debug.Assert(data.Type == 0 || data.InstancePlacements.Length == 0);
				for (int j = 0; j < data.InstancePlacements.Length; j++) {
					data.InstancePlacements[j] = new InstancePlacement(cbr);
					Debug.Assert(data.InstancePlacements[j].Prefabs.Length == 1);
					if (DumpLokiInstances) Debug.WriteLine($"   Placement #{j}: {data.InstancePlacements[j]}");
					for (int k = 0; k < data.InstancePlacements[j].Prefabs.Length; k++) {
						if (DumpLokiInstances) Debug.WriteLine($"      Ref #{k}: {data.InstancePlacements[j].Prefabs[k]}");
						max7 = Math.Max(max7, data.InstancePlacements[j].Prefabs[k].Unk1);
					}
				}
				wm.InstanceInfoLT5 = data;
			}

			// For testing!
			foreach (var wm in BSPs) {
				var rn = new RenderNode() {
					BSP_LT4 = wm,
					RenderFaces = [],
					FaceFlags = [],
				};
				if (wm.Index == 0) {
					//var allFaces = new List<Face>();
					//for (int i = 0; i < Meshes.Length; i++) {
					//	allFaces.AddRange(Meshes[i].Faces);
					//}
					//rn.RenderFaces = allFaces.ToArray();
					//rn.FaceFlags = new int[allFaces.Count];
					//wm.RenderNodes = [rn];
					wm.RenderNodes = RenderNodesLT5;
				} else {
					wm.RenderNodes = [rn];
				}
			}
		}

		var numSectors = cbr.ReadInt32();
		var numPortals = cbr.ReadInt32();
		var numSectorNodes = cbr.ReadInt32();
		var iUnk2 = cbr.ReadInt32(); // tree depth?
		var iUnk3 = cbr.ReadInt32();
		var iUnk4 = cbr.ReadInt32();
		var iUnk5 = cbr.ReadInt32();
		var iUnk6 = Version < WorldVersions.Loki ? cbr.ReadInt32() : -1;
		for (int i = 0; i < numPortals; i++) {
			var vectors = cbr.ReadTArray<Vector3>(Version < WorldVersions.Loki ? cbr.ReadUInt16() : cbr.ReadInt32());
			var plane1 = cbr.ReadStruct<LTPlane>();
			var plane2 = cbr.ReadStruct<LTPlane>(); // not sure if this really is a "plane"
			if (DumpSectors) Debug.WriteLine($"SectorPortal {i} #Planes = {vectors.Length}, P1 = {plane1}, P2 = {plane2}");
		}
		for (int i = 0; i < numSectors; i++) {
			if (Version < WorldVersions.Loki) {
				var name = cbr.ReadStringPrefixedInt16();
				var bb = cbr.ReadBoundingBoxDX();
				var sectorPlanes = cbr.ReadTArray<LTPlane>(cbr.ReadInt32());
				var portalIndices = cbr.ReadTArray<int>(cbr.ReadInt32());
				var sectorId = cbr.ReadInt32();
				Debug.Assert(sectorId == i);
				if (DumpSectors) Debug.WriteLine($"Sector {i} \"{name}\" #Planes = {sectorPlanes.Length}, #Portals = {portalIndices.Length}");
			} else {
				var fUnk = cbr.ReadSingle(); // could also be an uint ID
				var bb = cbr.ReadBoundingBoxDX();
				var portalCount = cbr.ReadInt32();
				var unkCount = cbr.ReadInt32();
				var iUnk7 = cbr.ReadInt32();
				var iUnk8 = cbr.ReadInt32();
				var sectorPlanes = cbr.ReadTArray<LTPlane>(cbr.ReadInt32());
				var unkInts = cbr.ReadTArray<int>(unkCount);
				var portalIndices = cbr.ReadTArray<int>(portalCount);
				if (DumpSectors) Debug.WriteLine($"Sector {i} Unk={fUnk:f2} {iUnk7} {iUnk8}, #Planes = {sectorPlanes.Length}, #Portals = {portalIndices.Length}, #Unks = {unkInts.Length}");
			}
		}
		for (int i = 0; i < numSectorNodes; i++) {
			var sectorIndices = cbr.ReadTArray<int>(cbr.ReadInt32());
			var iUnk7 = cbr.ReadInt32();
			var fUnk1 = cbr.ReadSingle();
			var iLeft = cbr.ReadInt32(); // maybe
			var iRight = cbr.ReadInt32(); // maybe
			if (DumpSectors) Debug.WriteLine($"SectorNode {i} Sectors={sectorIndices.Length}, UnkA = {iUnk7}, UnkB = {fUnk1}, Left = {iLeft}, Right = {iRight}");
		}
		logger?.Append($";{numSectors};{numPortals};{numSectorNodes};{iUnk2};{iUnk3};{iUnk4};{iUnk5};{iUnk6}");


		// === Server Part ===
		// Read object data
		if (!isSplit) br.Position = object_data_pos;
		var objCount = sbr.ReadInt32();
		Objects = new(objCount);
		for (int i = 0; i < objCount; i++) {
			WorldObject obj;
			obj = new WorldObject(sbr, i, Version);
			Objects.Add(obj);
			var name = obj.NameProperty;
			if (!string.IsNullOrEmpty(name) && name != "noname") ObjectsByName[name] = obj;
		}
		if (Version >= WorldVersions.Loki) {
			var gameDbFile = Path.ChangeExtension(filePath, ".gamedb");
			if (File.Exists(gameDbFile)) {
				var gameDb = new LithtechGameDbFile(gameDbFile);
				Debug.Assert(gameDb.Categories.Count == 1);
				var cat = gameDb.Categories[0];
				// There seem to be a few "unreferenced" entries in the GameDB, but the yield little useful info (in particular, no position)
				//var recordsUnused = new HashSet<int>(Enumerable.Range(0, cat.Records.Count));
				foreach (var obj in Objects) {
					if (obj.LokiObjectDbIndex < 0) continue;
					//recordsUnused.Remove(obj.LokiObjectDbIndex);
					var rcd = cat.Records[obj.LokiObjectDbIndex];
					var newProperties = new List<ObjectProperty>(obj.Properties.Length + rcd.Attributes.Count);
					newProperties.AddRange(obj.Properties);
					foreach (var attr in rcd.Attributes) {
						if (attr.Values == null || attr.Values.Length == 0) continue;
						Debug.Assert(attr.Values.Length == 1);
						if (attr.Values[0] == null) continue;
						newProperties.Add(new(attr.Name, attr.ObjectPropertyType, attr.Values[0]));
					}
					obj.Properties = newProperties.ToArray();
				}
				//foreach (var rcdId in recordsUnused) {
				//	var rcd = cat.Records[rcdId];
				//	Debug.WriteLine($"Unused DbRecord #{rcdId}: {rcd.Name}, {rcd.Attributes.Count} attribs: " + rcd.Attributes.Select(x => x.Name).Implode());
				//}
			} else {
				Debug.WriteLine($"!!! GameDb for map '{filePath}' not found!");
			}
		}
		logger?.Append($";{objCount}");

		// === Streaming Part ===
		if (!isSplit) br.Position = streaming_data_pos;
		// Like the objects section, but only for specific object types for which this information is encoded here in binary form, probably for performance reasons.
		// These objects are keyframers, AINavMeshes, and something else. TODO, but not so relevant.
		// For the "split" variant, it seems these are encoded at the end of the common part (at least in "Loki").

		sbr?.Dispose();
		cbr?.Dispose();
		br?.Dispose();
		logger?.AppendLine();
		((IWorldFile)this).LinkObjects();
	}

	private static string[] ReadLTStringArray(CustomBinaryReader br, int count)
	{
		var ret = new string[count];
		for (int i = 0; i < count; i++) {
			ret[i] = br.ReadStringPrefixedInt16();
		}
		return ret;
	}

	public static Vertex ReadVertex(CustomBinaryReader br, VertexType vdef)
	{
		var vert = new Vertex();
		var pos0 = br.Position;
		foreach (var prop in vdef.Properties) {
			if (prop.Format == VertexPropertyFormat.Exit) break;
			if (prop.Offset >= 0) br.Position = pos0 + prop.Offset;
			switch (prop.Usage, prop.Format, prop.Index) {
				case (VertexPropertyUsage.Position, VertexPropertyFormat.Vector3, 0):
					vert.Position = br.ReadVector3DX();
					break;
				case (VertexPropertyUsage.Normal, VertexPropertyFormat.Vector3, 0):
					vert.Normal = br.ReadVector3DX();
					break;
				case (VertexPropertyUsage.Tangent, VertexPropertyFormat.Vector3, 0):
					vert.Tangent = br.ReadVector3DX();
					break;
				case (VertexPropertyUsage.Binormal, VertexPropertyFormat.Vector3, 0):
					vert.Binormal = br.ReadVector3DX();
					break;
				case (VertexPropertyUsage.TexCoords, VertexPropertyFormat.Vector2, 0):
					vert.UV1 = br.ReadVector2DX();
					break;
				case (VertexPropertyUsage.TexCoords, VertexPropertyFormat.Vector2, 1):
					vert.UV2 = br.ReadVector2DX();
					break;
				case (VertexPropertyUsage.TexCoords, VertexPropertyFormat.Vector2, 2):
					vert.UV3 = br.ReadVector2DX();
					break;
				case (VertexPropertyUsage.TexCoords, VertexPropertyFormat.Vector2, 3):
					vert.UV4 = br.ReadVector2DX();
					break;
				case (VertexPropertyUsage.TexCoords, VertexPropertyFormat.CompressedVector2, 0):
					vert.UV1 = new(br.ReadInt16() / (float)short.MaxValue, br.ReadInt16() / (float)short.MaxValue);
					break;
				case (VertexPropertyUsage.TexCoords, VertexPropertyFormat.CompressedVector2, 1):
					vert.UV2 = new(br.ReadInt16() / (float)short.MaxValue, br.ReadInt16() / (float)short.MaxValue);
					break;
				case (VertexPropertyUsage.TexCoords, VertexPropertyFormat.Vector3, 0):
					vert.Tex41 = br.ReadVector3DX().ToVector4(0f);
					break;
				case (VertexPropertyUsage.TexCoords, VertexPropertyFormat.Vector4, 0):
					vert.Tex41 = br.ReadVector4DX();
					break;
				case (VertexPropertyUsage.TexCoords, VertexPropertyFormat.Vector3, 1):
					vert.Tex42 = br.ReadVector3DX().ToVector4(0f);
					break;
				case (VertexPropertyUsage.TexCoords, VertexPropertyFormat.Vector4, 1):
					vert.Tex42 = br.ReadVector4DX();
					break;
				case (VertexPropertyUsage.TexCoords, VertexPropertyFormat.Vector3, _):
					// ignore for now
					_ = br.ReadVector3DX();
					break;
				case (VertexPropertyUsage.TexCoords, VertexPropertyFormat.Vector4, _):
					// ignore for now
					_ = br.ReadVector4DX();
					break;
				case (VertexPropertyUsage.Color, VertexPropertyFormat.Rgba, 0):
					vert.Color = br.ReadRgba32();
					break;
				case (VertexPropertyUsage.BlendWeight, _, _):
					throw new NotImplementedException("Unhandled vertex BlendWeight parameter");
				case (VertexPropertyUsage.BlendIndices, _, _):
					throw new NotImplementedException("Unhandled vertex BlendIndices parameter");
				default:
					throw new NotImplementedException();
			}
		}
		return vert;
	}

	public CustomBinaryReader ResolveDataStream(string fileName)
	{
		if (Version < WorldVersions.Loki) return new CustomBinaryReader(Path.Combine(GameDir, fileName));
		if (BundleContents.TryGetValue(fileName, out var de)) {
			var data = de.Item1.GetData(de.Item2);
			return data.AsCustomBinaryReader();
		}
		Debug.WriteLine($"!! World data stream not found: {fileName}");
		return null;
	}

	public PrefabFile ReadPrefabFile(string instFileName)
	{
		if (PrefabCacheById.TryGetValue(instFileName, out var prefabFile)) return prefabFile;
		using var br = ResolveDataStream(instFileName);
		if (br == null) return null;
		prefabFile = new PrefabFile(br, this, instFileName);
		PrefabCacheById.Add(instFileName, prefabFile);
		return prefabFile;
	}


	public EngineClass Dump() => Dump(false);
	public EngineClass Dump(bool extended)
	{
		// NOTICE: This just outputs some debug info about the map in a custom tree format ("EngineClass") that could then be converted to a string.
		var worldCls = new LithEngineClass(Path.GetFileNameWithoutExtension(FileName), "World");
		worldCls.Properties.Add("FileName", FileName);
		worldCls.Properties.Add("Version", Version);
		worldCls.Properties.Add("BoundingBox", BoundingBox);
		worldCls.Properties.Add("Offset", Offset);
		worldCls.Properties.Add("WorldModels (BSPs)", $"{BSPs.Count} ({BSPs.Count(bsp => bsp.FirstName != null)} with name, {BSPs.Count(bsp => bsp.FirstName == null)} without)");
		worldCls.Properties.Add("BSP Names", ModelsByName?.Count);
		worldCls.Properties.Add("Objects", Objects?.Count);
		worldCls.Properties.Add("Named Objects", ObjectsByName?.Count);
		worldCls.Properties.Add("Subdivisions", SubdivisionFlagsCount);
		worldCls.Properties.Add("Planes", PlaneNormals?.Length);
		worldCls.Properties.Add("Nodes???", TotalNodeCount);
		worldCls.Properties.Add("Vertex Types???", TotalVertexTypeCount);
		worldCls.Properties.Add("Polygons???", TotalPolygonCount);
		worldCls.Properties.Add("Vertices???", TotalVertexCount);
		if (Version >= WorldVersions.Loki) worldCls.Properties.Add("UnkArray", GlobalUnkArrayLT5.Cast<object>().ToList());
		if (Bundles != null) worldCls.Properties.Add("Bundles", Bundles.Keys.Cast<object>().ToList());

		var filesCls = new LithEngineClass("Files Section");
		worldCls.Children.Add(filesCls);
		filesCls.Properties.Add("Strings", TotalFileStringsCount);
		filesCls.Properties.Add("Strings approx.", FileSectionStringsCountApprox);
		if (Version >= WorldVersions.Loki) {
			filesCls.Properties.Add("LT5 Prefab Map", PrefabNodeMapLT5?.Count);
			filesCls.Properties.Add("LT5 Prefab Mapped Render Nodes", prefabDefsTotalChildCount);
		}
		filesCls.Properties.Add("Mesh Load Infos", MeshLoadInfoCount);
		filesCls.Properties.Add("Portals", PortalCount);
		filesCls.Properties.Add("Unknowns A", FileSectionUnksA);
		filesCls.Properties.Add("Unknowns B", FileSectionUnksB);
		if (Version >= WorldVersions.Loki) {
			filesCls.Properties.Add("Referenced .bndl Bundles", Bundles?.Count);
			filesCls.Properties.Add("Referenced .bndl Bundles Contents", BundleContents?.Count);
			filesCls.Properties.Add("Global Files", extended ? GlobalFileIndexesLT5.Cast<object>().ToList() : GlobalFileIndexesLT5.Length);
		}

		var worldModelsCls = new LithEngineClass("WorldBSPs");
		worldCls.Children.Add(worldModelsCls);
		foreach (var wm in BSPs) {
			var modelCls = new LithEngineClass(wm.ToString(), "WorldBSP", index: wm.Index);
			if (wm.InstanceInfoLT5 != null) {
				modelCls.Properties.Add("InstanceInfo", wm.InstanceInfoLT5);
				if (wm.InstanceInfoLT5.InstancePlacements.Length > 0) {
					string s = wm.InstanceInfoLT5.InstancePlacements.Sum(x => x.Prefabs.Length) + " [" + string.Join(", ", wm.InstanceInfoLT5.InstancePlacements.SelectMany(x => x.Prefabs.Select(y => y.InstNodeIndex))) + "]";
					s += ", e.g. @ " + wm.InstanceInfoLT5.InstancePlacements[0].Position.ToStringMF();
					modelCls.Properties.Add("InstancePlacement", s);
				}
				if (wm.InstanceInfoLT5.MeshIndex >= 0 && wm.InstanceInfoLT5.FaceIndex >= 0) {
					var mesh = Meshes[wm.InstanceInfoLT5.MeshIndex];
					var face = mesh.Faces[wm.InstanceInfoLT5.FaceIndex];
					modelCls.Properties.Add("InstanceRenderFace", $"#{mesh.Index}.{face.Index} (Mat={mesh.Materials[face.MaterialId]})");
				}
			}
			worldModelsCls.Children.Add(modelCls);
			if (wm.Names == null) continue;
			modelCls.FriendlyName = wm.Names[0];
			if (wm.Names.Count > 1) {
				modelCls.FriendlyName += " (+" + (wm.Names.Count - 1).ToString() + ")";
				modelCls.Properties.Add("OtherNames", string.Join(", ", wm.Names.Skip(1)));
			}
			if (wm.LinkedObjects != null) {
				foreach (var linkedObj in wm.LinkedObjects.Values) {
					var linkedObjectCls = new LithEngineClass($"{linkedObj.NameProperty} <{linkedObj.LokiObjectDbIndex}> @ {linkedObj.Position.ToStringMF()}", linkedObj.ClassName, index: linkedObj.Index);
					modelCls.Children.Add(linkedObjectCls);
				}
			}
		}

		var renderDataCls = new LithEngineClass("Render Data");
		worldCls.Children.Add(renderDataCls);
		renderDataCls.Properties.Add("Total Faces", RenderTreeFaceCount);
		renderDataCls.Properties.Add("RenderMeshes", RenderMeshCount);
		renderDataCls.Properties.Add("Support Boxes", RenderTreeSupportBoxesCount);
		if (Version >= WorldVersions.Loki) {
			renderDataCls.Properties.Add("LT5 RenderTree Nodes", RenderNodesLT5?.Length);
			//renderDataCls.Properties.Add("LT5 RenderTree Faces", RenderNodesLT5.Sum(x => x.RenderFaces.Length));
			//renderDataCls.Properties.Add("LT5 RenderTree SupportBoxes", RenderNodesLT5.Sum(x => x.SupportBoxes.Count));
			//renderDataCls.Properties.Add("LT5 RenderTree Instances", RenderNodesLT5.Sum(x => x.InstancesLT5?.Length));
			renderDataCls.Properties.Add("LT5 Max RenderTree Node", MeshLoadInfosMaxRenderNodeLT5);
			renderDataCls.Properties.Add("LT5 Instances", TotalInstancesCount);
			renderDataCls.Properties.Add("LT5 Instances in RenderTree", RenderTreeInstancesCount);
			renderDataCls.Properties.Add("LT5 Unknowns", RenderMeshUnks);
		} else {
			renderDataCls.Properties.Add("LT4 Total Materials", TotalMaterialCount);
			renderDataCls.Properties.Add("LT4 Embedded Meshes", EmbeddedRenderMeshCount);
		}

		var renderMeshesCls = new LithEngineClass("Render Meshes");
		worldCls.Children.Add(renderMeshesCls);
		foreach (var mesh in Meshes) {
			var meshCls = new LithEngineClass(mesh.ToString(), "RenderMesh");
			renderMeshesCls.Children.Add(meshCls);
			if (mesh.ExternalFileName != null) meshCls.Properties.Add("FileName", mesh.ExternalFileName);
			if (mesh.InfoLT5 != null) {
				meshCls.FriendlyName = mesh.InfoLT5.Name;
				meshCls.Properties.Add("TypeId", mesh.InfoLT5.TypeId);
				meshCls.Properties.Add("ActivateMsg", mesh.InfoLT5.ActivateMsg);
				meshCls.Properties.Add("DeactivateMsg", mesh.InfoLT5.DeactivateMsg);
				meshCls.Properties.Add("Sound", mesh.InfoLT5.Sound);
				meshCls.Properties.Add("RequiredBundles", extended ? mesh.InfoLT5.RequiredBundles.Cast<object>().ToList() : mesh.InfoLT5.RequiredBundles.Length);
				meshCls.Properties.Add("RenderNodes", mesh.InfoLT5.RenderNodes.Length.ToString() + " [" + string.Join(", ", mesh.InfoLT5.RenderNodes) + "]");
			}
			meshCls.Properties.Add("VertexTypes", mesh.VertexTypes.Select(x => x.ToString()).Cast<object>().ToList());
			if (extended) meshCls.Properties.Add("Materials", extended ? mesh.Materials.Cast<object>().ToList() : mesh.Materials.Length);
			meshCls.Properties.Add("Unknowns", mesh.MeshUnks);
			foreach (var face in mesh.Faces) {
				var faceCls = new LithEngineClass(face.ToString(), "Face");
				meshCls.Children.Add(faceCls);
				if (face.RenderNodes_LT5 != null && face.RenderNodes_LT5.Count > 0) {
					faceCls.Properties.Add("Used in RNs", face.RenderNodes_LT5.Select(rn => rn.Index).Implode());
				}
				if (face.BSPs_LT5 != null && face.BSPs_LT5.Count > 0) {
					faceCls.Properties.Add("Used in BSPs", face.BSPs_LT5.Select(bsp => BSPs[bsp.Index].LinkedObjects != null ? bsp.Index.ToString() : bsp.Index.ToString() + " ***").Implode());
				}
			}
		}

		if (RenderNodesLT5 != null) {
			var renderTreeCls = new LithEngineClass("Render Tree Nodes");
			renderMeshesCls.Children.Add(renderTreeCls);
			foreach (var renderNode in RenderNodesLT5) {
				var nodeCls = new LithEngineClass(renderNode.ToString(), "RenderNode");
				renderTreeCls.Children.Add(nodeCls);
				var faces = new List<object>(renderNode.RenderFaces.Length);
				for (int i = 0; i < renderNode.RenderFaces.Length; i++) {
					faces.Add($"MeshFace #{renderNode.RenderFaces[i].Index}, Shadow = {renderNode.FaceFlags[i]}, Bounds = {renderNode.RenderFaces[i].Bounds.ToStringMF()}");
				}
				nodeCls.Properties.Add("Faces", faces);
				var supportBoxes = new List<object>(renderNode.SupportBoxes.Count);
				for (int i = 0; i < renderNode.SupportBoxes.Count; i++) {
					supportBoxes.Add($"SupportBox #{i}, Vectors = {renderNode.SupportBoxes[i].Select(v => "(" + v.ToStringMF() + ")").Implode(", ")}");
				}
				nodeCls.Properties.Add("Boxes", supportBoxes);
				if (renderNode.InstancesLT5 != null && renderNode.InstancesLT5.Length > 0) nodeCls.Properties.Add("Instances", string.Join(", ", renderNode.InstancesLT5));
			}
		}

		if (InstancesLT5 != null) {
			var instancesCls = new LithEngineClass("Global Instances");
			worldCls.Children.Add(instancesCls);
			foreach (var inst in InstancesLT5) {
				var instCls = new LithEngineClass(inst.ToString(), "Instance");
				instancesCls.Children.Add(instCls);
				if (inst.RenderNodes != null && inst.RenderNodes.Count > 0) instCls.Properties.Add("RenderNodes", inst.RenderNodes.Select(n => n.Index).Implode(", "));
			}
		}

		foreach (var obj in Objects) {
			// not done here
		}
		return worldCls;
	}

	bool IWorldFile.TryGetModelByName(string name, out IWorldModel model)
	{
		model = null;
		if (!ModelsByName.TryGetValue(name, out var model0)) return false;
		model = model0;
		return true;
	}

	public void Dispose()
	{
		if (Bundles != null) {
			foreach (var bundle in Bundles.Values) {
				bundle.Dispose();
			}
		}
	}



	// ****************************** Internal classes ******************************

	public class RenderMesh
	{
		public int Index;
		public string ExternalFileName;

		public VertexType[] VertexTypes { get; protected set; }
		public Face[] Faces { get; protected set; }
		public string[] Materials { get; protected set; }

		public MeshLoadInfo InfoLT5 { get; set; }
		public string MeshUnks;
		public int NumVertices, NumTriangles, NumIndices;

		public RenderMesh()
		{
			Index = -1;
		}
		public RenderMesh(CustomBinaryReader br, LithtechJupiterExWorldFile dat, int index, StringBuilder logger, bool isEmbedded, string[] materials)
		{
			Index = index;
			if (!isEmbedded) {
				Materials = materials;
				Trace.Assert(br.ReadString(4) == "MESH");
				int version = br.ReadInt32();
				Trace.Assert(version == 1);
			}
			var unkCount0 = dat.Version >= WorldVersions.Loki ? br.ReadInt32() : 1;
			Debug.Assert(unkCount0 == 1);
			var meshFaceCount = br.ReadInt32();
			var vertexTypeCountA = br.ReadInt32();
			var unkCount2 = br.ReadInt32(); // something related to vertex types...
			var unkCount3 = br.ReadInt32(); // high number that is probably related to vertex/triangle sizes
			var iUnk1 = br.ReadInt32(); //Debug.Assert(iUnk1 == 0); // 0 in embedded meshes?
			var faceCountA = dat.Version < WorldVersions.Loki ? br.ReadInt32() : 0;
			var materialCount = br.ReadInt32();
			MeshUnks = $"{unkCount2} {unkCount3} {iUnk1} {faceCountA}";

			ReadFacesAndVertices(br, vertexTypeCountA, meshFaceCount, dat);

			if (isEmbedded) Materials = ReadLTStringArray(br, materialCount);
			if (Index == 0) logger?.Append($";{meshFaceCount};{vertexTypeCountA};{unkCount2};{unkCount3};{iUnk1};{faceCountA};{materialCount};vertexDataSize;trianglesDataSize;vertexTypeCount;faceCountB");
		}

		protected void ReadFacesAndVertices(CustomBinaryReader br, int vertexTypeCountA, int meshFaceCount, LithtechJupiterExWorldFile dat)
		{
			int vertexDataSize = br.ReadInt32();
			int trianglesDataSize = br.ReadInt32();
			var vertexData = br.ReadBytes(vertexDataSize);
			var trianglesData = br.ReadBytes(trianglesDataSize);
			var brVertexData = vertexData.AsCustomBinaryReader();
			var brTrianglesData = trianglesData.AsCustomBinaryReader();

			var vertexTypeCount = br.ReadInt32();
			Debug.Assert(vertexTypeCount == vertexTypeCountA);
			VertexTypes = new VertexType[vertexTypeCount];
			for (int i = 0; i < vertexTypeCount; i++) {
				VertexTypes[i] = new(br, dat, i);
				if (VertexTypes[i].HasBackwardOffsets) Debug.WriteLine($"!!! Vertex type #{i} has backwards offsets: {VertexTypes[i]}");
			}

			var faceCountB = br.ReadInt32();
			Debug.Assert(meshFaceCount == faceCountB);
			Faces = new Face[faceCountB];
			for (int i = 0; i < faceCountB; i++) {
				Faces[i] = new(br, dat, i, VertexTypes, brVertexData, brTrianglesData, this);
				NumVertices += Faces[i].Vertices.Length;
				NumTriangles += Faces[i].NumTriangles;
				NumIndices += Faces[i].Indices.Length;
			}
		}

		//public override string ToString() => $"#{Index}{(ExternalFile != null ? $", Src = {Path.GetFileName(ExternalFile)}" : "")}, #Faces = {Faces?.Length}, #Mats = {Materials?.Length}, #VertTypes = {VertexTypes?.Length}";
		public override string ToString() => $"#{Index}, #Faces = {Faces?.Length}, #Mats = {Materials?.Length}, #VertTypes = {VertexTypes?.Length}, #Verts = {NumVertices}, #Triangles = {NumTriangles}, #Indices = {NumIndices}";
		private string NameStrPart => InfoLT5 == null ? null : $" \"{InfoLT5.Name}\"";
	}

	public class Face
	{
		public RenderMesh Mesh;
		public int Index;

		public Vertex[] Vertices;
		public int[] Indices;
		public int NumTriangles;
		public int MaterialId;
		public int Unk1;
		public VertexType VertexType;

		// Filled in later
		/// <summary>Bounds as defined by the <see cref="LithtechJupiterExWorldFile.RenderNode"/> that links to this face.</summary>
		public BoundingBox Bounds;
		/// <summary>[LT 4 only.] <see cref="LithtechJupiterExWorldFile.RenderNode"/> that contains this face in <see cref="LithtechJupiterExWorldFile.RenderNode.RenderFaces"/>.</summary>
		public RenderNode RenderNode_LT4;
		public bool IsShadowVolume;
		/// <summary>[LT 5 only.] Any <see cref="LithtechJupiterExWorldFile.RenderNode"/>s linking instances to this face via the render tree, or NULL if none.</summary>
		public List<RenderNode> RenderNodes_LT5;
		/// <summary>Any BSPs linking instances to this face via <see cref="LokiRenderInstanceData.FaceIndex"/>, or NULL if none.</summary>
		public List<WorldBSP> BSPs_LT5;

		public Face(CustomBinaryReader br, LithtechJupiterExWorldFile dat, int index, VertexType[] vertex_defs, CustomBinaryReader brVertexData, CustomBinaryReader brTrianglesData, RenderMesh mesh)
		{
			Mesh = mesh;
			Index = index;
			int vertices_start, vertices_count, vertex_size, triangles_start, triangles_shift, triangles_count, vertexType;
			if (dat.Version < WorldVersions.Loki) {
				vertices_start = br.ReadInt32();
				vertices_count = br.ReadInt32();
				vertex_size = br.ReadInt32();
				triangles_start = br.ReadInt32();
				triangles_shift = br.ReadInt32() - vertices_start;
				triangles_count = br.ReadInt32();
				MaterialId = br.ReadInt32();
				Unk1 = br.ReadInt32(); Debug.Assert(Unk1 == 0); // 0
				vertexType = br.ReadInt32();
				VertexType = vertex_defs[vertexType];
				brVertexData.Position = vertices_start * vertex_size;
			} else {
				triangles_shift = -br.ReadInt32();
				vertices_count = br.ReadInt32();
				triangles_start = br.ReadInt32();
				vertices_start = br.ReadInt32(); // is this the vertex start?
				triangles_count = br.ReadInt32();
				MaterialId = br.ReadInt32();
				Unk1 = br.ReadInt32(); // 0
				var Unk2 = br.ReadInt32(); Debug.Assert(Unk2 == 0); // 0
				vertexType = br.ReadInt32();
				VertexType = vertex_defs[vertexType];
				vertex_size = VertexType.Size;
				brVertexData.AlignPosition(vertex_size);
				//brVertexData.Position += vertices_count * vertex_size;
			}
			//Debug.WriteLine($"#{index}\t{vertexType}\t{vertices_start}\t{vertices_count}\t{vertex_size}\t{triangles_start}\t{triangles_shift}\t{triangles_count}\t{MaterialId}\t{Unk1}");

			// Read vertices
			Vertices = new Vertex[vertices_count];
			//Bounds = new(new(float.MaxValue), new(float.MinValue));
			for (int i = 0; i < vertices_count; i++) {
				var p0 = brVertexData.Position;
				Vertices[i] = ReadVertex(brVertexData, VertexType);
				//Bounds.Expand(Vertices[i].Position);
				if (dat.Version < WorldVersions.Loki && VertexType.HasBackwardOffsets) {
					brVertexData.Position = p0 + vertex_size;
				} else {
					Debug.Assert(brVertexData.Position == p0 + vertex_size);
				}
			}
			//if (VertexType.Properties.Any(p => p.Format == VertexPropertyFormat.SpecialTexCoords)) {
			//	FaceSpecials = new();
			//	using var bw = new BinaryWriter(FaceSpecials);
			//	foreach (var v in Vertices) {
			//		bw.Write(v.Position.X);
			//		bw.Write(v.Position.Y);
			//		bw.Write(v.Position.Z);
			//		bw.Write(v.SpecialCoord);
			//	}
			//}

			// Read triangles
			brTrianglesData.Position = triangles_start * 2; // is 2 correct? or 6?
			Indices = new int[triangles_count * 3];
			NumTriangles += triangles_count;
			for (int i = 0; i < triangles_count; i++) {
				var indices = brTrianglesData.ReadTArray<ushort>(3);
				for (int j = 0; j < indices.Length; j++) {
					var idx = indices[j] + triangles_shift;
					Debug.Assert(idx >= 0);
					Indices[i * 3 + j] = idx;
				}
			}
		}

		public override string ToString() => $"#{Index}: MatIdx = #{MaterialId}, #Verts = {Vertices?.Length}, #Indices = {Indices.Length}, VertType = {VertexType.Index}, Unk1 = {Unk1}, RenderNode = {RenderNode_LT4?.Index}, Shadow = {IsShadowVolume}, Center = {Bounds.Center.ToStringMF()}";
	}


	public class VertexType
	{
		public int Index { get; }
		public VertexProperty[] Properties;
		public bool HasBackwardOffsets;
		/// <summary>Calculated size of the vertex data.</summary>
		public int Size { get; }

		public int NumPositions, NumNormals, NumTangents, NumBinormals, NumColors, NumTexcoords, NumWeights, NumIndices;

		public VertexType(CustomBinaryReader br, LithtechJupiterExWorldFile dat, int index)
		{
			Index = index;
			var structSize = br.ReadInt32(); // size of *this* structure (not of the vertex), either in bits or bytes (depending on version)
			var numProperties = dat.Version < WorldVersions.Loki ? structSize / 8 : structSize;
			Properties = new VertexProperty[numProperties];
			int lastOffset = -1;
			for (int i = 0; i < numProperties; i++) {
				int unk1 = dat.Version < WorldVersions.Loki ? br.ReadUInt16() : 0;
				int shOffset = dat.Version < WorldVersions.Loki ? br.ReadUInt16() : -1; // shader offset
				byte unk2 = dat.Version >= WorldVersions.Loki ? br.ReadByte() : (byte)0;
				var format = (VertexPropertyFormat)br.ReadByte();
				unk2 = dat.Version < WorldVersions.Loki ? br.ReadByte() : unk2;
				var usage = dat.Version < WorldVersions.Loki ? (VertexPropertyUsage)br.ReadByte() : br.ReadByte() switch {
					0 => VertexPropertyUsage.Position,
					1 => VertexPropertyUsage.Normal,
					2 => VertexPropertyUsage.Tangent,
					3 => VertexPropertyUsage.Binormal,
					4 => VertexPropertyUsage.TexCoords,
					5 => VertexPropertyUsage.Color,
					_ => throw new NotImplementedException()
				};
				byte shIndex = br.ReadByte();
				Debug.Assert(unk2 == 0);
				Debug.Assert(unk1 == 0 || unk1 == 255);
				Properties[i] = new(shOffset, format, usage, shIndex, unk1, unk2);
				if (unk1 == 255) break;
				HasBackwardOffsets |= dat.Version < WorldVersions.Loki && shOffset <= lastOffset;
				lastOffset = shOffset;
				switch (usage, format) {
					case (VertexPropertyUsage.Position, VertexPropertyFormat.Vector3):
						NumPositions++; break;
					case (VertexPropertyUsage.Normal, VertexPropertyFormat.Vector3):
						NumNormals++; break;
					case (VertexPropertyUsage.Tangent, VertexPropertyFormat.Vector3):
						NumTangents++; break;
					case (VertexPropertyUsage.Binormal, VertexPropertyFormat.Vector3):
						NumBinormals++; break;
					case (VertexPropertyUsage.TexCoords, VertexPropertyFormat.Vector2):
						NumTexcoords++; break;
					case (VertexPropertyUsage.TexCoords, VertexPropertyFormat.Vector3):
						break; // LOKI
					case (VertexPropertyUsage.TexCoords, VertexPropertyFormat.Vector4):
						break; // LOKI
					case (VertexPropertyUsage.TexCoords, VertexPropertyFormat.CompressedVector2):
						NumTexcoords++; break; // LOKI
					case (VertexPropertyUsage.Color, VertexPropertyFormat.Rgba):
						NumColors++; break;
					case (VertexPropertyUsage.BlendWeight, _):
						NumWeights++; break;
					case (VertexPropertyUsage.BlendIndices, _):
						NumIndices++; break;
					default:
						throw new NotImplementedException();
				}
				Size += format switch {
					VertexPropertyFormat.Vector2 => 4 * 2,
					VertexPropertyFormat.Vector3 => 4 * 3,
					VertexPropertyFormat.Vector4 => 4 * 4,
					VertexPropertyFormat.Rgba => 4,
					VertexPropertyFormat.SkeletalIndex => throw new NotImplementedException(),
					VertexPropertyFormat.CompressedVector2 => 4,
					VertexPropertyFormat.Exit => throw new InvalidOperationException(),
					_ => throw new NotImplementedException()
				};
			}
		}

		public override string ToString()
		{
			if (Properties == null) return "<EMPTY>";
			var types = new List<string>(Properties.Length);
			foreach (var p in Properties) {
				string idx = p.Index == 0 ? "" : "#" + (p.Index + 1).ToString();
				if (p.Format != VertexPropertyFormat.Exit) types.Add($"{p.Usage}{idx} [{p.Format}]");
			}
			return $"#{Index} ({Size}b): " + string.Join(" + ", types);
		}
	}

	public enum VertexPropertyFormat : byte
	{
		Vector2 = 1,
		Vector3 = 2,
		Vector4 = 3,
		Rgba = 4, // 4 bytes? Uint?
		SkeletalIndex = 5, // Float or Int, depending on shader defs
		CompressedVector2 = 8, // 2x short. Loki only
		Exit = 17,
	}

	public enum VertexPropertyUsage : byte
	{
		Position = 0,
		BlendWeight = 1,
		BlendIndices = 2,
		Normal = 3,
		TexCoords = 5,
		Tangent = 6,
		Binormal = 7,
		Color = 10,
	}

	public record struct VertexProperty(int Offset, VertexPropertyFormat Format, VertexPropertyUsage Usage, int Index, int Unk1, int Unk2);

	public struct Vertex
	{
		public Vector3 Position, Normal, Tangent, Binormal;
		public Vector2 UV1, UV2, UV3, UV4;
		public Vector4 Tex41, Tex42;
		public Rgba32 Color;

		public override readonly string ToString() => $"Pos = {Position.ToStringMF()}, Norm = {Normal.ToStringMF()}, UV1 = {UV1.ToStringMF()}, UV2 = {UV2.ToStringMF()}, Color = {Color}, Tan = {Tangent.ToStringMF()}, BiNo = {Binormal.ToStringMF()}";
	}



	public class WorldBSP : IWorldModel
	{
		public int Index { get; }
		public string FirstName;
		public List<string> Names;

		public Vector3 Center, HalfDims;
		public BoundingBox BBox => new(Center - HalfDims, Center + HalfDims);
		public WorldPoly[] Polygons;
		public WorldNode[] Nodes;
		public Vector3[] Points;
		public int TotalPolyPointCount;
		public int Unk1;

		public Dictionary<string, WorldObject> LinkedObjects { get; set; }

		// The following is filled in by the linked RenderMesh:
		public RenderNode[] RenderNodes;
		public PhysicsShape Shape;
		// "Loki" Instance placement
		public LokiRenderInstanceData InstanceInfoLT5;
		public int TotalPolyVertexCount;

		public WorldBSP(CustomBinaryReader br, LithtechJupiterExWorldFile dat, int index, List<string> names)
		{
			Index = index;
			Names = names;
			if (Names != null && Names.Count > 0) FirstName = Names[0];
			Unk1 = br.ReadInt32(); Debug.Assert(Unk1 == 0 || Unk1 == 1); // 1?
			var point_count = br.ReadInt32();
			var poly_count = br.ReadInt32();
			TotalPolyPointCount = br.ReadInt32(); // often poly_count * 3
			var node_count = br.ReadInt32();
			HalfDims = br.ReadVector3DX();
			Center = br.ReadVector3DX();
			var iUnk2 = dat.Version < WorldVersions.Loki ? br.ReadInt32() : 0; //Debug.Assert(iUnk2 == 0);
			var verticesPerPolygon = br.ReadBytes(poly_count);
			Polygons = new WorldPoly[poly_count];
			for (int i = 0; i < poly_count; i++) {
				Polygons[i] = new WorldPoly(br, dat, i, verticesPerPolygon[i], this);
			}
			Nodes = new WorldNode[node_count];
			for (int i = 0; i < node_count; i++) {
				Nodes[i] = new WorldNode(br, dat, i);
			}
			Points = br.ReadTArray<Vector3>(point_count);
		}

		//public override string ToString() => $"#{Index}: \"{FirstName}\", #Polys = {Polygons?.Length}, #Points = {Points?.Length}, #PolyVertices = {TotalPolyVertexCount}, #Nodes = {Nodes?.Length}, BB = {BBox.ToStringMF()}";
		public override string ToString() => $"#{Index}: \"{FirstName}\", #Polys = {Polygons?.Length}, #Points = {Points?.Length}, #TPP = {TotalPolyPointCount}, #PolyVertices = {TotalPolyVertexCount}, #Nodes = {Nodes?.Length}, Unk1 = {Unk1}, Center = {Center.ToStringMF()}, Dims = {(HalfDims * 2f).ToStringMF()}";
	}

	public class WorldPoly
	{
		public WorldBSP Parent { get; }
		public int Index;
		public ushort SurfaceFlags;
		/// <summary>Can be used to obtain the normal from <see cref="LithtechJupiterExWorldFile.PlaneNormals"/>.</summary>
		public int PlaneIndex;
		public float PlaneDistance;
		public int[] VertexIndices;

		public WorldPoly(CustomBinaryReader br, LithtechJupiterExWorldFile dat, int index, int vert_count, WorldBSP parent)
		{
			Parent = parent;
			Index = index;
			var b1 = br.ReadByte();
			var b2 = br.ReadByte();
			SurfaceFlags = br.ReadUInt16();
			PlaneIndex = br.ReadInt32();
			PlaneDistance = br.ReadSingle();
			VertexIndices = br.ReadTArray<int>(vert_count);
		}

		public override string ToString() => $"#{Index}, PlaneIndex = {PlaneIndex}, Flags = #{SurfaceFlags}, PlaneDistance = {PlaneDistance}";
	}

	public class WorldNode
	{
		public int Index;
		public int PolyIndex;
		public int LeftChildNodeIndex; // or -1
		public int RightChildNodeIndex; // or -1

		public WorldNode(CustomBinaryReader br, LithtechJupiterExWorldFile dat, int index)
		{
			Index = index;
			PolyIndex = br.ReadInt32();
			LeftChildNodeIndex = dat.Version < WorldVersions.Loki ? br.ReadInt32() : br.ReadInt16();
			RightChildNodeIndex = dat.Version < WorldVersions.Loki ? br.ReadInt32() : br.ReadInt16();
		}

		public WorldNode() { }

		public override string ToString() => $"#{Index}: Poly = #{PolyIndex}, Left = #{LeftChildNodeIndex}, Right = #{RightChildNodeIndex}";
	}

	public class RenderNode
	{
		public int Index;
		public WorldBSP BSP_LT4; // LT4 only
		public RenderMesh Mesh_LT5; // LT5 only
		public Face[] RenderFaces;
		public int[] FaceFlags;
		public List<Vector3[]> SupportBoxes; // Not sure what this is
		public int[] InstancesLT5;

		public RenderNode()
		{
		}

		public override string ToString() => $"#{Index}: {MeshIndexStr}#Faces = {RenderFaces?.Length}, #Boxes = {SupportBoxes?.Count}, #Instances = {InstancesLT5?.Length}";
		private string MeshIndexStr => Mesh_LT5 != null ? $"Mesh #{Mesh_LT5.Index}, " : null;
	}

	public enum PhysicsShapeType
	{
		Null=0,
		Mesh=1,
		OBB=2,
		Sphere=3,
		Hull=4,
		Unknown1 = 5,
		SubShapes=6,
		Capsule=7,
	}

	public class PhysicsShape(Vector3 pos, Quaternion rot, LithtechJupiterExWorldFile.PhysicsShapeType type)
	{
		public Vector3 Pos { get; } = pos;
		public Quaternion Rot { get; } = rot;
		public PhysicsShapeType Type { get; } = type;
		public List<PhysicsShape> Children { get; set; }
	}
	public class PhysicsShapeMesh : PhysicsShape
	{
		public float mass, density;
		public Vector3[] vectors;
		public float unk1, unk2;
		public int point_count;
		public int triangle_count;
		public Vector3[] points;
		public int[] ids;
		public float[] physics_props;
		public int unknown_count;
		public byte[] unknown_array;

		public PhysicsShapeMesh(Vector3 pos, Quaternion rot, PhysicsShapeType type, CustomBinaryReader br) : base(pos, rot, type)
		{
			mass = br.ReadSingle(); density = br.ReadSingle();
			vectors = br.ReadTArray<Vector3>(4);
			unk1 = br.ReadSingle(); unk2 = br.ReadSingle();
			point_count = br.ReadInt32();
			triangle_count = br.ReadInt32();
			points = br.ReadTArray<Vector3>(point_count);
			if (point_count < 0x10000) {
				ids = br.ReadTArray<ushort>(triangle_count).Select(p => (int)p).ToArray();
			} else {
				ids = br.ReadTArray<int>(triangle_count);
			}
			physics_props = br.ReadTArray<float>(4);
			unknown_count = br.ReadInt32();
			unknown_array = br.ReadBytes(unknown_count);
		}
	}
	public class PhysicsShapeHull : PhysicsShape
	{
		public float mass, density;
		public Vector3[] vectors;
		public float unk1, unk2;
		public int point_count;
		public int plane_count;
		public Vector3[] points;
		public LTPlane[] planes;

		public PhysicsShapeHull(Vector3 pos, Quaternion rot, PhysicsShapeType type, CustomBinaryReader br) : base(pos, rot, type)
		{
			mass = br.ReadSingle(); density = br.ReadSingle();
			vectors = br.ReadTArray<Vector3>(4);
			unk1 = br.ReadSingle(); unk2 = br.ReadSingle();
			point_count = br.ReadInt32();
			plane_count = br.ReadInt32();
			points = br.ReadTArray<Vector3>(point_count);
			planes = br.ReadTArray<LTPlane>(plane_count);
		}
	}
	public class PhysicsShapeOBB : PhysicsShape
	{
		public float mass, density;
		public float unk; // dimensions + this = WorldEdit dimensions?
		public Vector3 dimensions;

		public PhysicsShapeOBB(Vector3 pos, Quaternion rot, PhysicsShapeType type, CustomBinaryReader br) : base(pos, rot, type)
		{
			mass = br.ReadSingle(); density = br.ReadSingle();
			unk = br.ReadSingle();
			dimensions = br.ReadVector3DX();
		}
	}
	public class PhysicsShapeSphere : PhysicsShape
	{
		public float mass, density;
		public float radius;

		public PhysicsShapeSphere(Vector3 pos, Quaternion rot, PhysicsShapeType type, CustomBinaryReader br) : base(pos, rot, type)
		{
			mass = br.ReadSingle(); density = br.ReadSingle();
			radius = br.ReadSingle();
		}
	}
	public class PhysicsShapeCapsule : PhysicsShape
	{
		public float mass, density;
		public float radius;
		public Vector3[] vectors; // y = something to do with length?
		public Vector3 pos2;
		public Quaternion rot2;
		public uint id;

		public PhysicsShapeCapsule(Vector3 pos, Quaternion rot, PhysicsShapeType type, CustomBinaryReader br) : base(pos, rot, type)
		{
			mass = br.ReadSingle(); density = br.ReadSingle();
			radius = br.ReadSingle();
			vectors = br.ReadTArray<Vector3>(2);
			var bookmark = br.Position;
			br.Position += 7 * 4;
			var checkId = br.ReadInt32();
			br.Position = bookmark;
			if (checkId == 2) {				
			} else if (checkId == 3) {
				pos2 = br.ReadVector3DX();
				rot2 = br.ReadQuaternionDX();
				id = br.ReadUInt32();
			}
		}
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	public struct LTPlane
	{
		public Vector3 Normal;
		public float Distance;
		public override readonly string ToString() => $"Normal = {Normal.ToStringMF()} Dist = {Distance:f2}";
	}

	public class MeshLoadInfo
	{
		internal string Name;
		internal string ActivateMsg;
		internal string DeactivateMsg;
		internal string Sound;
		internal uint TypeId; // NOTE: This is not unique to a mesh.
		internal uint[] RenderNodes;
		internal string[] RequiredBundles;
	}

	/// <summary>
	/// Each instance is either "Type 0" or "Type 1", in combination with any number of <see cref="InstancePlacements"/>.
	/// </summary>
	public class LokiRenderInstanceData
	{
		// Common:
		public InstancePlacement[] InstancePlacements; // there can be any number of those in combination with "Type 0" and "Type 1".
		public int Type; // 0 or 1
		// Type 0:
		public int MeshIndex = -1; // Not 100% sure if it's a mesh index. Sometimes -1 for "none"???
		public int FaceIndex = -1; // often 0
		public int FaceCount;
		public byte unk3a, unk3b, unk3c, unk3d;
		// Type 1:
		public int InstNodeIndex;
		public string InstFileName;

		public bool IsEmpty => (InstancePlacements == null || InstancePlacements.Length == 0) && ((Type == 0 && MeshIndex < 0) || (Type == 1 && string.IsNullOrEmpty(InstFileName)));

		public override string ToString()
		{
			string common = $"Type {Type}, #Placements = {InstancePlacements?.Length}";
			if (Type == 0) return $"{common}, Mesh = #{MeshIndex}, Face = #{FaceIndex}, Count = {FaceCount}, Bytes = {unk3a} {unk3b} {unk3c} {unk3d}";
			if (Type == 1) return $"{common}, InstNode = #{InstNodeIndex}, Name = \"{InstFileName}\"";
			throw new NotImplementedException();
		}
	}
	public struct InstancePlacement
	{
		public Vector3 Position;
		public Quaternion Rotation;
		public PrefabReference[] Prefabs;

		public InstancePlacement(CustomBinaryReader br)
		{
			Position = br.ReadVector3DX();
			Rotation = br.ReadQuaternionDX();
			Prefabs = new PrefabReference[br.ReadInt32()];
			for (int i = 0; i < Prefabs.Length; i++) {
				Prefabs[i] = new PrefabReference(br);
			}
		}
		public override readonly string ToString() => $"Pos = {Position.ToStringMF()}, Rot = {Rotation.ToStringMF()}, #Instances = {Prefabs?.Length}";
	}
	public record struct PrefabReference(int Unk1, int InstNodeIndex, string InstFileName)
	{
		// Unk1 = 0-3?
		public PrefabReference(CustomBinaryReader br) : this(br.ReadInt32(), br.ReadInt32(), br.ReadStringPrefixedInt16())
		{
		}
		//public override string ToString() => $"Unk = {Unk1}, UIndex = {UnkIndex}, File = \"{InstanceFileName}\"";
	}

	public class LokiPrefabNodeDef
	{
		/// <summary>Actually a name hash.</summary>
		public int Id;
		/// <summary>Node IDs are probably name hashes of the original node/bone names. E.g. 1564975234 = Body, 1553165145 = base.</summary>
		public int[] NodeIds;
	}

	public class InstanceInfo
	{
		public int Index;
		public string InstFileName;
		public BoundingBox BoundsOrZero;
		public Vector3 Position;
		public Quaternion Rotation;
		public int I1, I2, I3; // Small numbers. I1 is usually 1, I2 is usually 0-3, and I3 is usually 0.
		public List<RenderNode> RenderNodes = new();

		public InstanceInfo(CustomBinaryReader br, int index)
		{
			Index = index;
			BoundsOrZero = br.ReadBoundingBoxDX();
			Position = br.ReadVector3DX();
			Rotation = br.ReadQuaternionDX();
			I1 = br.ReadInt32(); I2 = br.ReadInt32(); I3 = br.ReadInt32(); // these are all small numbers, e.g. 0-3
			InstFileName = br.ReadStringPrefixedInt16();
		}

		public override string ToString() => $"#{Index}: \"{InstFileName}\", #RNodes = {RenderNodes?.Count}, Ints = {I1} {I2} {I3}, Pos = {Position.ToStringMF()}, Rot = {Rotation.ToStringMF()}" + (!BoundsOrZero.Size.Equals(Vector3.Zero) ? ", Bounds = " + BoundsOrZero.ToStringMF() : "");
	}

	public class PrefabFile : RenderMesh
	{
		//public string Name;
		public uint ID;

		public PrefabRenderNode[] PrefabNodes;
		public Dictionary<int, PrefabRenderNode> NodesByID;

		public PrefabFile(CustomBinaryReader br, LithtechJupiterExWorldFile dat, string name)
		{
			ExternalFileName = name;
			Trace.Assert(br.ReadString(4) == "PRFB");
			int version = br.ReadInt32();
			Trace.Assert(version == 1);

			ID = br.ReadUInt32();
			int renderNodesCount = br.ReadInt32();
			int meshFaceCount = br.ReadInt32();
			int materialCount = br.ReadInt32();
			int material_block_size = br.ReadInt32();
			int vertexTypeCountA = br.ReadInt32();
			int iGlobalUnk4 = br.ReadInt32(); // something related to vertex types...?
			int vertex_and_triangle_block_size = br.ReadInt32(); // combined size?
			int faceCountA = br.ReadInt32();

			ReadFacesAndVertices(br, vertexTypeCountA, meshFaceCount, dat);

			Materials = new string[materialCount];
			var sbr = br.ReadBytes(material_block_size).AsCustomBinaryReader();
			sbr.Encoding = Encoding.ASCII;
			for (int i = 0; i < materialCount; i++) {
				Materials[i] = sbr.ReadStringNullTerminated();
				sbr.AlignPosition(4);
			}
			for (int i = 0; i < Faces.Length; i++) {
				if (Materials[Faces[i].MaterialId].Contains("shadowvolume.mat", StringComparison.InvariantCultureIgnoreCase)) {
					Faces[i].IsShadowVolume = true;
				}
			}

			PrefabNodes = new PrefabRenderNode[renderNodesCount];
			NodesByID = new(renderNodesCount);
			for (int i = 0; i < renderNodesCount; i++) {
				PrefabNodes[i].Index = i;
				PrefabNodes[i].ID = br.ReadInt32();
				var offset = br.ReadInt32();
				var count = br.ReadInt32();
				PrefabNodes[i].Faces = new Face[count];
				for (int j = 0; j < count; j++) {
					PrefabNodes[i].Faces[j] = Faces[offset + j];
				}
				PrefabNodes[i].Flag1 = br.ReadByte(); PrefabNodes[i].Flag2 = br.ReadByte(); PrefabNodes[i].Flag3 = br.ReadByte(); PrefabNodes[i].Flag4 = br.ReadByte();
				NodesByID.Add(PrefabNodes[i].ID, PrefabNodes[i]);
			}
		}

		public override string ToString() => $"\"{ExternalFileName}\" / {ID}\n{base.ToString()}\n#PrefabNodes = {PrefabNodes.Length}";
	}

	public struct PrefabRenderNode
	{
		public int Index;
		public int ID;
		public Face[] Faces;
		public byte Flag1, Flag2, Flag3, Flag4;

		public override string ToString() => $"#{Index}: ID = {ID}, #Faces = {Faces.Length}, Flags = {Flag1} {Flag2} {Flag3} {Flag4}";
	}

}
