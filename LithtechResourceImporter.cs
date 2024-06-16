using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using HelixToolkit.SharpDX.Core;
using HelixToolkit.SharpDX.Core.Model;
using HelixToolkit.SharpDX.Core.Model.Scene;
using HelixToolkit.SharpDX.Core.Shaders;
using HelixToolkit.Wpf.SharpDX;
using SharpDX;
using SharpDX.Direct3D11;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;
using Row = System.Collections.Generic.Dictionary<string, string>;

namespace AllOtherResourceImporters.LithTech;

public class LithtechResourceImporter
{
	public const bool UseInstanceTestMaterialsLT5 = false;

	private readonly HashSet<string> TexturesToIgnoreLT5 = new(StringComparer.InvariantCultureIgnoreCase) {
		"flat_normal_map", "flat_black", "flat_white"
	};
	private readonly HashSet<string> MaterialsToIgnoreLT5 = new(StringComparer.InvariantCultureIgnoreCase) {
		"shadowvolume", "grid_playernavigational", "invisible", "default_shootthrough", "default"
	};


	public void PlayWorld(string worldName, string gameSubdir = null)
	{
		var worldFile = SourceDir + "\\WORLDS\\" + worldName + ".wld";
		using var world = LoadWorld(worldFile);

		// Render Objects
		Debug.WriteLine($"Rendering {world.Objects.Count} objects...");
		foreach (var obj in world.Objects) {
			var objMesh = RenderObject(obj, scene);
			scene.RootNode.AddChildNode(objMesh);
		}

		// Render World Geometry
		RenderJupiterExWorld(world00, scene.RootNode);

		// MISSING: Display scene
	}

	private static PhongMaterial red = PhongMaterials.Red, blue = PhongMaterials.Blue, green = PhongMaterials.Green, black = PhongMaterials.Black, obs = PhongMaterials.Obsidian;
	private static PhongMaterial cyan = PhongMaterials.Turquoise, orange = PhongMaterials.Orange, pearl = PhongMaterials.Pearl, violet = PhongMaterials.Violet, white = PhongMaterials.White;
	/// <summary>For LT4/LT5.</summary>
	private void RenderJupiterExWorld(LithtechJupiterExWorldFile world, GroupNode rootNode)
	{
		// There are basically 3 ways mesh geometry can come frome:
		// 1) Defined in a World.Face (subunit of a World.Mesh). This is the most common type of render geometry.
		// 2) Defined in a Prefab (sometimes called instance). This is used for some parts of the world, especially if it appears in multiple instances.
		// 3) From a model file. This is mostly used for animated models like NPCs, and not handeled here.
		// 4)* BSPs also define meshes, but those are only used for collision detection, not for rendering.
		// - Hint: Set UseInstanceTestMaterialsLT5 = true to visualize the different "special" sources.

		//var pp = world.ReadPrefabFile("prefabs\\_global\\vehicles\\sedan_m01.inst");
		//var ppNode = RenderJupiterExWorld_Prefab(world, pp, -1);
		//ppNode.ModelMatrix = Matrix.Translation(2000, -2000, 2000);
		//rootNode.AddChildNode(ppNode);

		var surfaceMaterials = new Dictionary<(int MeshId, int MatId), MaterialCore>();
		if (Engine == Engines.LT4_JupiterEX) {
			// In LT4, we can render by BSP
			foreach (var wm in world.BSPs) {
				Debug.WriteLine($"Rendering WorldModel {wm.Index + 1} of {world.BSPs.Count}...");
				// A single WM can have several instances at different positions.
				string wmTag = wm.ToString();
				var wmInstances = new List<WorldObject>();
				if (wm.LinkedObjects != null && wm.LinkedObjects.Count > 0) {
					wmInstances.AddRange(wm.LinkedObjects.Values);
				} else {
					wmInstances.Add(null);
				}
				var modelGroup = new GroupNode() {
					Name = $"WORLD-MODEL #{wm.Index}",
				};
				for (int i = 0; i < wmInstances.Count; i++) {
					var linkedObject = wmInstances[i];
					GroupNode instanceGroup = null;
					if (linkedObject != null) {
						instanceGroup = new GroupNode() {
							Name = $"INSTANCE #{i + 1}/{wmInstances.Count} \"{wm.Names[i]}\"",
						};
						var mat = Matrix.Identity;
						if (Engine < Engines.LT5) {
							if (linkedObject.TryGetProperty("Rotation", out var rotation, Quaternion.Identity)) {
								mat *= Matrix.RotationQuaternion(rotation);
							}
						} else {
							if (linkedObject.TryGetProperty("Rotation", out var rotation, Vector3.Zero)) {
								mat *= ConvTools.GetMatrixRotationEuler(rotation);
							}
						}
						if (linkedObject.TryGetProperty("Pos", out var position, Vector3.Zero)) {
							mat *= Matrix.Translation(position);
						}
						instanceGroup.ModelMatrix = mat;
					} else if (!string.IsNullOrEmpty(wm.FirstName)) {
						modelGroup.Name += $" \"{wm.FirstName}\"";
					}
					foreach (var node in wm.RenderNodes) {
						var nodeGroup = RenderJupiterExWorld_RenderNode(world, node, linkedObject, surfaceMaterials);
						if (nodeGroup != null) {
							if (linkedObject != null) {
								instanceGroup.AddChildNode(nodeGroup);
							} else {
								modelGroup.AddChildNode(nodeGroup);
							}
						}
					}
					if (linkedObject != null) modelGroup.AddChildNode(instanceGroup);
				}
				rootNode.AddChildNode(modelGroup);
			}
		} else if (Engine == Engines.LT5) {
			// In LT5, we don't have the BSP <--> RenderTree assoc (at least it's not know how to get it), so we render by RenderMesh

			foreach (var inst in world.InstancesLT5) {
				var prefabFile = world.ReadPrefabFile(inst.InstFileName);
				if (prefabFile == null) continue;
				var prefabNode = RenderJupiterExWorld_Prefab(world, prefabFile, 0, materialOverride: UseInstanceTestMaterialsLT5 ? red : null, addTag: $"PrefabInstance: {inst}");
				if (prefabNode == null) continue;
				prefabNode.ModelMatrix = Matrix.RotationQuaternion(inst.Rotation) * Matrix.Translation(inst.Position);
				rootNode.AddChildNode(prefabNode);
			}

			var meshesPlusNone = new List<LithtechJupiterExWorldFile.RenderMesh>(world.Meshes);
			meshesPlusNone.Insert(0, new());
			foreach (var mesh in meshesPlusNone) {
				Debug.WriteLine($"Rendering Mesh {mesh.Index + 1} of {world.Meshes.Length}...");
				string meshTag = mesh.ToString();
				if (mesh.InfoLT5 != null) {
					meshTag += $"\nActivation: {mesh.InfoLT5.ActivateMsg}\n#Nodes = {mesh.InfoLT5.RenderNodes?.Length}, #Bundles = {mesh.InfoLT5.RequiredBundles?.Length}";
				}
				var meshGroup = new GroupNode() {
					Name = $"RENDER-MESH #{mesh.Index} \"{mesh.InfoLT5?.Name}\"",
				};
				// Draw all RenderNodes associated with that mesh
				foreach (var node in world.RenderNodesLT5.Where(n => (n.Mesh_LT5 == null && mesh.Index == -1) || (n.Mesh_LT5 != null && n.Mesh_LT5 == mesh))) {
					var nodeGroup = RenderJupiterExWorld_RenderNode(world, node, null, surfaceMaterials);
					if (nodeGroup != null) meshGroup.AddChildNode(nodeGroup);
				}
				//if (mesh.Index >= 0) {
				//	foreach (var face in mesh.Faces) {
				//		var nodeGroup = RenderJupiterExWorld_Face(world, face, null, surfaceMaterials);
				//		if (nodeGroup != null) rootNode.AddChildNode(nodeGroup);
				//	}
				//}
				rootNode.AddChildNode(meshGroup);
			}

			// Draw all world-models that have been instance-placed
			var globalFacesLinkedToOtherBsp = new HashSet<LithtechJupiterExWorldFile.Face>();
			foreach (var wm in world.BSPs) {
				if (wm.InstanceInfoLT5 == null) continue;
				var links = new List<WorldObject>();
				if (wm.LinkedObjects != null) links.AddRange(wm.LinkedObjects.Values);
				if (links.Count == 0) links.Add(null);
				int i = -1;
				foreach (var linkedObj in links) {
					i++;
					var pos = linkedObj != null ? linkedObj.Position : Vector3.Zero;
					var mrot = linkedObj != null ? ConvTools.GetMatrixRotationXYZ(linkedObj.GetRotation(Engine)) : Matrix.Identity;
					string tagString = wm.ToString();
					if (linkedObj != null) tagString += $"\nLinkedObj #{linkedObj.Index} [{linkedObj.ClassName}], GameDB #{linkedObj.LokiObjectDbIndex}";
					var wmGroup = new GroupNode() {
						Name = $"WORLD-MODEL-LT5 #{wm.Index}_{i} \"{linkedObj?.NameProperty}\"",
						ModelMatrix = mrot * Matrix.Translation(pos),
					};
					//if (linkedObj == null) rootNode.AddChildNode(RenderJupiterExWorld_BSP(world, wm, pearl));
					if (wm.InstanceInfoLT5.Type == 0 && wm.InstanceInfoLT5.MeshIndex >= 0) {
						//if (linkedObj == null) Debug.WriteLine($"--> BSP without name has type 0 ref: {wm} ==> {wm.InstanceInfoLT5}");
						var mesh = world.Meshes[wm.InstanceInfoLT5.MeshIndex];
						for (int j = 0; j < wm.InstanceInfoLT5.FaceCount; j++) {
							var face = mesh.Faces[wm.InstanceInfoLT5.FaceIndex + j];
							globalFacesLinkedToOtherBsp.Add(face);
							var meshNode = RenderJupiterExWorld_Face(world, face, linkedObj, surfaceMaterials, materialOverride: UseInstanceTestMaterialsLT5 ? (linkedObj != null ? blue : cyan) : null, addTag: $"InstInfo {wm.InstanceInfoLT5}\nFace {j + 1} of {wm.InstanceInfoLT5.FaceCount} starting from #{wm.InstanceInfoLT5.FaceIndex}");
							if (meshNode == null) continue;
							wmGroup.AddChildNode(meshNode);
						}
					} else if (wm.InstanceInfoLT5.Type == 1) {
						//if (linkedObj == null) Debug.WriteLine($"--> BSP without name has type 1 ref: {wm} ==> {wm.InstanceInfoLT5}");
						var prefabFile = world.ReadPrefabFile(wm.InstanceInfoLT5.InstFileName);
						if (prefabFile == null) continue;
						var prefabNode = RenderJupiterExWorld_Prefab(world, prefabFile, wm.InstanceInfoLT5.InstNodeIndex, materialOverride: UseInstanceTestMaterialsLT5 ? (linkedObj != null ? green : cyan) : null, addTag: $"InstInfo {wm.InstanceInfoLT5}", linkedObject: linkedObj);
						if (prefabNode == null) continue;
						wmGroup.AddChildNode(prefabNode);
					}
					foreach (var instPlacement in wm.InstanceInfoLT5.InstancePlacements) {
						foreach (var instRef in instPlacement.Prefabs) {
							//if (linkedObj == null) Debug.WriteLine($"--> BSP without name has instance placement: {wm} ==> {instPlacement} | {instRef}");
							var prefabFile = world.ReadPrefabFile(instRef.InstFileName);
							if (prefabFile == null) continue;
							var prefabNode = RenderJupiterExWorld_Prefab(world, prefabFile, instRef.InstNodeIndex, materialOverride: UseInstanceTestMaterialsLT5 ? (linkedObj != null ? violet : cyan) : null, addTag: $"InstInfo {wm.InstanceInfoLT5}\nPlacement {instPlacement}\nPrefabRef {instRef}", linkedObject: linkedObj);
							if (prefabNode == null) continue;
							// Most things look more correct without this line:
							//prefabNode.ModelMatrix = Matrix.RotationQuaternion(instPlacement.Rotation) * Matrix.Translation(instPlacement.Position);
							wmGroup.AddChildNode(prefabNode);
						}
					}
					if (wmGroup.ItemsCount > 0) rootNode.AddChildNode(wmGroup);
				}

				//var bspNode = RenderFearWorldBSP(world, wm);
				//if (bspNode != null) rootNode.AddChildNode(bspNode);
			}
			return;
		} else throw new NotSupportedException();
	}
	private SceneNode RenderJupiterExWorld_RenderNode(LithtechJupiterExWorldFile world, LithtechJupiterExWorldFile.RenderNode node, WorldObject linkedObject, Dictionary<(int MeshId, int MatId), MaterialCore> surfaceMaterials)
	{
		if (node.RenderFaces.Length == 0) return null;
		string tagString = node.ToString();
		var rmesh = node.RenderFaces[0]?.Mesh;
		if (Engine < Engines.LT5 && node.RenderFaces.All(f => f.Mesh == rmesh)) {
			tagString += $"\nRenderMesh: {rmesh}";
		}
		if (node.InstancesLT5 != null && node.InstancesLT5.Length > 0) {
			tagString += $"\nInstances: {node.InstancesLT5.Length}";
			foreach (var inst in node.InstancesLT5) tagString += $"\n   {world.InstancesLT5[inst]}";
		}
		var nodeGroup = new GroupNode() {
			Name = $"RENDER-NODE #{node.Index}",
		};

		foreach (var face in node.RenderFaces) {
			if (face.IsShadowVolume) continue;
			var meshNode = RenderJupiterExWorld_Face(world, face, linkedObject, surfaceMaterials);
			nodeGroup.AddChildNode(meshNode);
		}

		return nodeGroup;
	}
	private MeshNode RenderJupiterExWorld_Face(LithtechJupiterExWorldFile world, LithtechJupiterExWorldFile.Face face, WorldObject linkedObject, Dictionary<(int MeshId, int MatId), MaterialCore> surfaceMaterials, MaterialCore materialOverride = null, string addTag = null)
	{
		var mesh = face.Mesh;
		var matName = mesh.Materials[face.MaterialId];
		Debug.Assert(face.VertexType != null && face.VertexType.NumPositions > 0, "Face vertex type has no positions.");

		MaterialCore CreateMaterialForSection()
		{
			if (!RenderWorldTextures) return new VertColorMaterial();
			if (surfaceMaterials.TryGetValue((mesh.Index, face.MaterialId), out var mat1)) return mat1;
			if (!ReadFearMaterialFile(matName, out var matInfo)) {
				surfaceMaterials.Add((mesh.Index, face.MaterialId), null);
				return red;
			}
			// Shaders:
			// specular: This is the default specular material. This allows specification of a diffuse, emissive, specular, and normal map.  In addition, for DX9 level cards and higher it allows for specifying a maximum specular power.  The alpha channel of the specular map will then represent how glossy the surface is, ranging from black which is zero, to white which is the specified number. The higher the number, the shinier the surface.
			// specular_alphatest: This is the default specular material with alphatest support. This allows specification of a diffuse, emissive, specular, and normal map. The alpha channel of the diffuse map will cut holes in the material wherever it's black. In addition, for DX9 level cards and higher it allows for specifying a maximum specular power.  The alpha channel of the specular map will then represent how glossy the surface is, ranging from black which is zero, to white which is the specified number. The higher the number, the shinier the surface.
			// glass: This is a glass material based on the default specular material. This allows specification of a diffuse, specular, and normal map. In addition, for DX9 level cards and higher it allows for specifying a maximum specular power.  The alpha channel of the specular map will then represent how glossy the surface is, ranging from black which is zero, to white which is the specified number. The higher the number, the shinier the surface.
			// additive: This material will additively blend a translucent object into a scene.
			// multiply: This material will blend a translucent object into the background using a multiplicative blend.
			// translucent: This material will interpolate between the diffuse texture of this object and the scene already rendered.
			var shader = matInfo.ShaderName;
			var cfg = matInfo.Settings;
			string matTag = null;
			matTag += $"\nShader: {matInfo.ShaderName} --> " + string.Join(", ", cfg.Select(de => de.Key + " = " + de.Value.ToString()));
			bool alphatest = shader.Contains("alphatest", StringComparison.InvariantCultureIgnoreCase);

			var mat = new ItsPhongMaterial() {
				Name = matName,
				Tag = matTag,
				HasAlphaInDiffuse = linkedObject != null && linkedObject.TryGetProperty("Translucent", out bool bTranslucent) && bTranslucent,
				AlphaClipping = alphatest ? ItsMaterialAlphaClipping.BinaryInDiffuse : ItsMaterialAlphaClipping.None,
				AlphaClippingThreshold = 0.37f,
				RequestedBlendMode = ItsMaterialBlendModes.None,
			};
			if (mat.HasAlphaInDiffuse && linkedObject != null && linkedObject.TryGetProperty("TranslucentLight", out bool bTranslucentLight) && !bTranslucentLight) {
				// Indicates whether or not this world model should use lighting when it is translucent or if it should just be treated as full bright.
				mat.PerformLighting = false;
			}
			if (shader.Contains("additive", StringComparison.InvariantCultureIgnoreCase)) {
				mat.RequestedBlendMode = ItsMaterialBlendModes.Additive;
			} else if (shader.Contains("multiply", StringComparison.InvariantCultureIgnoreCase)) {
				// This looks right only under full brightness...
				mat.RequestedBlendMode = ItsMaterialBlendModes.Multiplicative;
				mat.PerformLighting = false;
				mat.RequestDepthWrite = false;
				//mat.RequestedBlendMode = ItsMaterialBlendModes.Alpha;
				if (linkedObject == null) mat.HasAlphaInDiffuse = true;
			} else if (shader.Contains("translucent", StringComparison.InvariantCultureIgnoreCase)) {
				mat.RequestedBlendMode = ItsMaterialBlendModes.Alpha;
				if (linkedObject == null) mat.HasAlphaInDiffuse = true; // unsure?
			}
			if (shader.Contains("glass", StringComparison.InvariantCultureIgnoreCase)) {
				mat.RequestedBlendMode = ItsMaterialBlendModes.Additive; // TODO: right blend mode?? maybe we need "overlay"?
				mat.AmbientColor = Color4.Black;
				//mat.VertexColorBlendingFactor = 0.5f;
			}
			if (cfg.TryGetValue("tDiffuseMap", out object tDiffuseMap) && tDiffuseMap is string sDiffuseMapFileName && !string.IsNullOrEmpty(sDiffuseMapFileName) && !TexturesToIgnoreLT5.Contains(Path.GetFileNameWithoutExtension(sDiffuseMapFileName))) {
				mat.DiffuseMap = ReadTextureFromFile(sDiffuseMapFileName, world);
				mat.DiffuseMapFilePath = sDiffuseMapFileName;
			}
			if (cfg.TryGetValue("tNormalMap", out object tNormalMap) && tNormalMap is string sNormalMapFileName && !string.IsNullOrEmpty(sNormalMapFileName) && !TexturesToIgnoreLT5.Contains(Path.GetFileNameWithoutExtension(sNormalMapFileName))) {
				//mat.NormalMap = ReadTextureFromFile(sNormalMapFileName, world);
				//mat.NormalMapFilePath = sNormalMapFileName;
			}
			if (cfg.TryGetValue("tEmissiveMap", out object tEmissiveMap) && tEmissiveMap is string sEmissiveMapFileName && !string.IsNullOrEmpty(sEmissiveMapFileName) && !TexturesToIgnoreLT5.Contains(Path.GetFileNameWithoutExtension(sEmissiveMapFileName))) {
				mat.EmissiveMap = ReadTextureFromFile(sEmissiveMapFileName, world);
				mat.EmissiveMapFilePath = sEmissiveMapFileName;
				mat.EmissiveColor = Color4.White;
				if (mat.DiffuseMap == null) mat.DiffuseColor = Color4.Black;
			}
			if (cfg.TryGetValue("tSpecularMap", out object tSpecularMap) && tSpecularMap is string sSpecularMapFileName && !string.IsNullOrEmpty(sSpecularMapFileName) && !TexturesToIgnoreLT5.Contains(Path.GetFileNameWithoutExtension(sSpecularMapFileName))) {
				mat.SpecularMap = ReadTextureFromFile(sSpecularMapFileName, world);
				mat.SpecularMapFilePath = sSpecularMapFileName;
			}
			if (cfg.TryGetValue("tBumpMap", out object tBumpMap) && tNormalMap is string sBumpMapFileName && !string.IsNullOrEmpty(sBumpMapFileName) && !TexturesToIgnoreLT5.Contains(Path.GetFileNameWithoutExtension(sBumpMapFileName))) {
				// TODO!
			}
			if (cfg.TryGetValue("tEnvironmentMap", out object tEnvironmentMap) && tEnvironmentMap is string sEnvironmentMapFileName && !string.IsNullOrEmpty(sEnvironmentMapFileName) && !TexturesToIgnoreLT5.Contains(Path.GetFileNameWithoutExtension(sEnvironmentMapFileName))) {
				// TODO!
				// Plus: tEnvironmentMapMask
			}
			if (cfg.TryGetValue("tDetailNormalMap", out object tDetailNormalMap) && tNormalMap is string sDetailNormalMapFileName && !string.IsNullOrEmpty(sDetailNormalMapFileName) && !TexturesToIgnoreLT5.Contains(Path.GetFileNameWithoutExtension(sDetailNormalMapFileName))) {
				// TODO!
				(float su, float sv) = ((float)cfg.GetValueOrDefault("fDetailScaleU", 1.0f), (float)cfg.GetValueOrDefault("fDetailScaleV", 1.0f));
			}
			if (cfg.TryGetValue("fMaxSpecularPower", out object fMaxSpecularPower) && fMaxSpecularPower is float specularPower) {
				mat.SpecularShininess = specularPower;
			}
			if (cfg.TryGetValue("fBumpScale", out object fBumpScale) && fBumpScale is float bumpScale) {
				// TODO!
			}
			if (cfg.TryGetValue("fRefractScale", out object fRefractScale) && fRefractScale is float refractScale) {
				// TODO!
			}
			if (cfg.TryGetValue("vTintColor", out object vTintColor) && vTintColor is Color4 tintColor) {
				mat.DiffuseColor = tintColor;
			}
			// TODO: SurfaceFlags? fRefractScale? fHDRScale? fPanDiffuseU/V? fPanNormalMapU/V? fDepthDistance? fScaleDiffuseU/V? tMaskMap? tControlMap?
			// There are also a couple of more specialized textures, see "shadersettingcount_by_number.txt" when exporting.
			surfaceMaterials.Add((mesh.Index, face.MaterialId), mat);
			return mat;
		}

		int numPoints = face.Vertices.Length;
		MeshGeometry3D geo;
		geo = new MeshGeometry3D();
		geo.Positions = new(numPoints);
		if (face.VertexType.NumNormals > 0) geo.Normals = new(numPoints);
		if (face.VertexType.NumTangents > 0) geo.Tangents = new(numPoints);
		if (face.VertexType.NumBinormals > 0) geo.BiTangents = new(numPoints);
		if (face.VertexType.NumColors > 0) geo.Colors = new(numPoints);
		if (face.VertexType.NumTexcoords > 0) geo.TextureCoordinates = new(numPoints);
		foreach (var vert in face.Vertices) {
			geo.Positions.Add(vert.Position);
			geo.Normals?.Add(vert.Normal);
			geo.Tangents?.Add(vert.Tangent);
			geo.BiTangents?.Add(vert.Binormal);
			geo.Colors?.Add(vert.Color.ToColor4());
			geo.TextureCoordinates?.Add(vert.UV1);
			if (geo is ItsMeshGeometry3D geo2) {
				geo2.TextureCoordinates2?.Add(vert.UV2);
			}
		}
		geo.Indices = new(face.Indices);
		if (geo.TextureCoordinates != null) geo.CalculateNormalsAndTangentsAuto();

		Debug.Assert(geo.Indices.Count % 3 == 0);
		if (geo.Colors != null && geo.Colors.Count == 0) geo.Colors = null;

		var mat = materialOverride ?? CreateMaterialForSection();
		//MaterialCore mat = green;

		MeshNode meshNode;
		if (geo is ItsMeshGeometry3D) {
			meshNode = new ItsMeshNode();
		} else {
			meshNode = new MeshNode();
		}
		meshNode.Name = $"FACE #{mesh.Index}.{face.Index} [{mesh.Materials[face.MaterialId]}]";
		meshNode.Material = mat;
		meshNode.Geometry = geo;
		meshNode.CullMode = DefaultCullMode;
		meshNode.FrontCCW = false;
		//if (section.ShaderType == LithtechDatFile.EPCShaderType.Lightmap) meshNode.DepthBias = -100;
		if (mat is ItsPhongMaterial ipm) {
			ipm.ApplyToNode(meshNode);
		}
		return meshNode;
	}
	private SceneNode RenderJupiterExWorld_Prefab(LithtechJupiterExWorldFile world, LithtechJupiterExWorldFile.PrefabFile prefab, int nodeIndex = 0, MaterialCore materialOverride = null, string addTag = null, WorldObject linkedObject = null, bool showErrors = true)
	{
		// Map the node index
		if (nodeIndex >= 0) {
			if (world.PrefabNodeMapLT5.TryGetValue(LithtechGameDbFile.CalcHash(prefab.ExternalFileName), out var nodes)) {
				if (prefab.NodesByID.TryGetValue(nodes[nodeIndex], out var prn)) {
					nodeIndex = prn.Index;
				} else {
					if (showErrors) Debug.WriteLine($"! Attempted to render non-existant source-node #{nodeIndex} of prefab \"{prefab.ExternalFileName}\" with {prefab.PrefabNodes.Length} nodes");
					return null;
				}
			}
		}
		if (prefab.Faces.Length == 0) {
			if (showErrors) Debug.WriteLine($"! Attempted to render node #{nodeIndex} of empty prefab \"{prefab.ExternalFileName}\" with {prefab.PrefabNodes.Length} nodes");
			return null;
		} else if (nodeIndex >= prefab.Faces.Length) {
			if (showErrors) Debug.WriteLine($"! Attempted to render non-existant target-node #{nodeIndex} of prefab \"{prefab.ExternalFileName}\" with {prefab.PrefabNodes.Length} nodes");
			return null;
		}
		var surfaceMaterials = new Dictionary<(int MeshId, int MatId), MaterialCore>();
		var prefabGroup = new GroupNode() {
			Name = $"PREFAB {Path.GetFileNameWithoutExtension(prefab.ExternalFileName)}",
		};
		foreach (var node in prefab.PrefabNodes) {
			if (nodeIndex >= 0 && node.Index != nodeIndex) continue;
			var nodeGroup = new GroupNode() {
				Name = $"PREFAB-NODE #{node.Index}",
			};
			foreach (var face in node.Faces) {
				if (face.IsShadowVolume) continue;
				var meshNode = RenderJupiterExWorld_Face(world, face, linkedObject, surfaceMaterials, materialOverride);
				nodeGroup.AddChildNode(meshNode);
			}
			prefabGroup.AddChildNode(nodeGroup);
		}
		if (prefabGroup.ItemsCount == 0) {
			if (showErrors) Debug.WriteLine($"! Cannot render empty prefab \"{prefab.ExternalFileName}\" with {prefab.PrefabNodes.Length} nodes");
			return null;
		}
		return prefabGroup;
	}
	private SceneNode RenderJupiterExWorld_BSP(LithtechJupiterExWorldFile world, LithtechJupiterExWorldFile.WorldBSP wm, MaterialCore materialOverride = null)
	{
		string tagString = wm.ToString();
		if (wm.Names != null && wm.Names.Count > 1) tagString += $"\nHas {wm.Names.Count - 1} more names";
		var group = new GroupNode() {
			Name = $"WORLD-MODEL #{wm.Index}: {wm.FirstName}",
		};

		var nodes = new List<SceneNode>();
		foreach (var poly in wm.Polygons) {
			string nodeTag = poly.ToString();
			TextureModel lightmapTexture = null;

			MeshGeometry3D geo;
			var mb = new MeshBuilder(false, false);
			foreach (var idx in poly.VertexIndices) {
				mb.Positions.Add(wm.Points[idx]);
			}
			// Add triangle fan
			for (var i = 0; i < mb.Positions.Count - 2; i++) {
				mb.TriangleIndices.Add(0);
				mb.TriangleIndices.Add(i + 1);
				mb.TriangleIndices.Add(i + 2);
			}
			mb.ComputeNormalsAndTangents(MeshFaces.Default);
			geo = mb.ToMeshGeometry3D();

			Debug.Assert(geo.Indices.Count % 3 == 0);
			if (geo.Colors?.Count == 0) geo.Colors = null;

			var mat = materialOverride ?? orange; // CreateMaterialForSection();

			MeshNode meshNode;
			if (geo is ItsMeshGeometry3D) {
				meshNode = new ItsMeshNode();
			} else {
				meshNode = new MeshNode();
			}
			meshNode.Name = $"POLY #{poly.Index}";
			meshNode.Material = mat;
			meshNode.Geometry = geo;
			meshNode.CullMode = DefaultCullMode;
			meshNode.FrontCCW = false;
			nodes.Add(meshNode);
		}

		foreach (var node in nodes) {
			group.AddChildNode(node);
		}
		return group;
	}

	private bool ReadFearMaterialFile(string matName, out FearMaterial mat)
	{
		mat = default;
		if (MaterialCacheLT5 == null) {
			MaterialCacheLT5 = new(1000, StringComparer.InvariantCultureIgnoreCase);
			foreach (var matlibFile in AllMaterialLibrariesLT5) {
				var matlib = new LithtechMatLibFile(matlibFile);
				foreach (var matEntry in matlib.Materials) {
					if (MaterialCacheLT5.TryAdd(matEntry.MaterialName, matEntry)) {
						//Debug.WriteLine($"! Duplicate material file name in library: {matEntry.MaterialName}");
					}
					//if (matEntry.Settings.TryGetValue("SurfaceFlags", out var surfaceFlags) && surfaceFlags is int sflags) {
					//	Debug.WriteLine($"{Path.GetFileNameWithoutExtension(matEntry.MaterialName)};{Path.GetFileNameWithoutExtension(matEntry.ShaderName)};{sflags};{sflags.ToBinString()}");
					//}
				}
			}
		}
		if (!MaterialCacheLT5.TryGetValue(matName, out mat)) {
			Debug.WriteLine($"!! Material file not found: {matName}");
			return false;
		}
		return true;
	}



	private SceneNode RenderObject(WorldObject obj, Scene3D scene)
	{
		// Position of the object in world coordinates.
		var position = obj.Position;
		// Rotation of the object in the world (i.e., Pitch, Yaw, Roll of the object).
		var rotation = obj.GetRotation(Engine);

		// MISSING: This currently just draws a marker at the given position.
		// Model files would need to be loaded and rendered.
		return null;
	}

}

