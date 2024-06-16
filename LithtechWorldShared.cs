using ConversionLib;
using HelixToolkit.SharpDX.Core;
using SharpDX;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace AllOtherResourceImporters.LithTech;

public interface IWorldFile : IDisposable
{
	string FileName { get; }
	IEnumerable<(string, string)> PropertiesAsDict { get; }
	List<WorldObject> Objects { get; }
	//Dictionary<string, WorldBSP> ModelsByName { get; } = new(StringComparer.InvariantCultureIgnoreCase);
	Dictionary<string, WorldObject> ObjectsByName { get; }
	bool TryGetModelByName(string name, out IWorldModel model);
	WorldVersions Version { get; }
	BoundingBox BoundingBox { get; }
	Vector3 Offset { get; }
	EngineClass Dump();

	public void LinkObjects()
	{
		foreach (var obj in Objects) {
			var pName = obj.NameProperty;
			if (!string.IsNullOrEmpty(pName) && pName != "noname") {
				if (TryGetModelByName(pName, out var matchingWorldModel)) {
					matchingWorldModel.LinkedObjects ??= [];
					if (!matchingWorldModel.LinkedObjects.TryAdd(pName, obj)) {
						Debug.WriteLine($"! Warning: Duplicate model name \"{pName}\" could not be linked to WorldModel #{matchingWorldModel.Index}");
					}
					obj.LinkedModel = matchingWorldModel;
					//classesWithLinks.Add(obj.ClassName);
				} else {
					//Debug.WriteLine($"! Object \"{pName}\" [{obj.ClassName}] is not connected to any brush.");
				}
			}
		}
	}
}

public interface IWorldModel
{
	int Index { get; }
	//LithtechDatFile.WorldObject LinkedObject { get; set; }
	/// <summary>
	/// This may also be NULL.
	/// In LT before Jupiter EX, only a single object can be linked. Later, several objets with different names can be linked to the same WorldModel.
	/// </summary>
	Dictionary<string, WorldObject> LinkedObjects { get; set; }
}

public class WorldObject
{
	public int Index;
	public string ClassName;
	public ObjectProperty[] Properties;
	/// <summary>A WorldModel (BSP) to which this object is optionally linked. If so, it usually defines additional properties for that WM.</summary>
	public IWorldModel LinkedModel { get; set; }

	private Dictionary<string, object> CustomProperties;
	private string NameOverride;
	/// <summary>Inside the GameDb file <i>&lt;MapName&gt;</i>.GameDb00, this refers to the record that hold the remaining object properties.</summary>
	public int LokiObjectDbIndex = -1;

	public WorldObject(CustomBinaryReader br, int index, WorldVersions version)
	{
		Index = index;
		var data_length = version < WorldVersions.JupiterEx ? br.ReadUInt16() : 0;
		ClassName = br.ReadStringPrefixedInt16();
		if (version < WorldVersions.Loki) {
			var property_count = br.ReadInt32();
			CustomBinaryReader pbr = null;
			if (version >= WorldVersions.JupiterEx) {
				pbr = new CustomBinaryReader(new MemoryStream(br.ReadBytes(br.ReadInt32())));
			}
			Properties = new ObjectProperty[property_count];
			for (int i = 0; i < property_count; i++) {
				Properties[i] = new ObjectProperty(br, version, pbr);
			}
		} else {
			NameOverride = br.ReadStringPrefixedInt16();
			LokiObjectDbIndex = br.ReadInt32();
			Properties = new ObjectProperty[2];
			Properties[0] = new("Pos", ObjectPropertyTypes.Vector3, br.ReadVector3DX());
			Properties[1] = new("Rotation", ObjectPropertyTypes.Vector3, br.ReadVector3DX()); // euler angles this time...
		}
	}

	public string NameProperty {
		get {
			if (NameOverride != null) return NameOverride;
			var nameProp = Properties.FirstOrDefault(p => p.Name.Equals("name", StringComparison.InvariantCultureIgnoreCase));
			if (nameProp.Value != null) return nameProp.Value?.ToString();
			return null;
		}
	}

	public Vector3 Position {
		get {
			if (TryGetProperty("Pos", out var position, Vector3.Zero)) return position;
			return Vector3.Zero;
		}
	}
	public Vector3 GetRotation(Engines engine) {
		Vector3 rotation = Vector3.Zero;
		if (TryGetProperty("Rotation", out var rotationQ, Quaternion.Identity, ignoreTypeMismatch: engine >= Engines.LT5)) {
			if (engine < Engines.LT4_JupiterEX) {
				rotation = new(rotationQ.X, rotationQ.Y, rotationQ.Z);
				if (engine >= Engines.LT2_0) rotation = new(rotation.X, -rotation.Y, rotation.Z); // TODO: Is this correct?
			} else if (engine >= Engines.LT5) {
				rotation = new(rotationQ.X, rotationQ.Y, rotationQ.Z);
			}
		}
		return rotation;
	}

	public bool TryGetProperty(string name, out object value)
	{
		if (CustomProperties != null && CustomProperties.TryGetValue(name, out value)) return true;
		value = null;
		ArgumentNullException.ThrowIfNull(name);
		foreach (var prop in Properties) {
			if (name.Equals(prop.Name, StringComparison.InvariantCultureIgnoreCase)) {
				value = prop.Value;
				return true;
			}
		}
		return false;
	}
	public bool TryGetProperty<T>(string name, out T value, T defaultValue = default, bool ignoreTypeMismatch = false)
	{
		value = defaultValue;
		if (!TryGetProperty(name, out object value2)) return false;
		if (ignoreTypeMismatch && value2 is not T) {
			if (value2.GetType().Equals(typeof(int)) && typeof(T).Equals(typeof(bool))) {
				int value3 = (int)value2;
				Debug.Assert(value3 == 0 || value3 == 1);
				value = (T)(object)(value3 != 0);
			} else if (value2.GetType().Equals(typeof(bool)) && typeof(T).Equals(typeof(int))) {
				value = (T)(object)(((bool)value2) ? 1 : 0);
			} else if (value2.GetType().Equals(typeof(int)) && typeof(T).Equals(typeof(float))) {
				value = (T)(object)(float)(int)value2;
			} else if (value2.GetType().Equals(typeof(float)) && typeof(T).Equals(typeof(int))) {
				value = (T)(object)(int)(float)value2;
			} else if (value2.GetType().Equals(typeof(Vector3)) && typeof(T).Equals(typeof(Quaternion))) {
				var vec = (Vector3)value2;
				value = (T)(object)new Quaternion(vec.X, vec.Y, vec.Z, 1f);
			} else if (value2.GetType().Equals(typeof(int)) && typeof(T).Equals(typeof(Color4))) {
				//var rgba = new Rgba32(BitConverter.ToUInt32(BitConverter.GetBytes((int)value2), 0));
				value = (T)(object)new Color4((int)value2);
			} else {
				return true;
			}
		} else {
			value = (T)value2;
		}
		return true;
	}

	public object GetProperty(string name)
	{
		if (CustomProperties != null && CustomProperties.TryGetValue(name, out var value)) return value;
		ArgumentNullException.ThrowIfNull(name);
		foreach (var prop in Properties) {
			if (name.Equals(prop.Name, StringComparison.InvariantCultureIgnoreCase)) {
				return prop.Value;
			}
		}
		return null;
	}

	public void SetPropertyOverride(string name, object value)
	{
		if (value == null) return;
		CustomProperties ??= new(StringComparer.InvariantCultureIgnoreCase);
		CustomProperties[name] = value;
	}

	public override string ToString() => $"#{Index}: \"{NameProperty}\" [{ClassName}], {Properties.Length} properties, WM = #{LinkedModel?.Index} LokiDbIdx = #{LokiObjectDbIndex}";

	public string GetPropertiesString()
	{
		string s = "";
		foreach (var prop in Properties) {
			if ((prop.Flags & ObjectPropertyFlags.AnyGroup) != 0) continue;
			if ("Name".Equals(prop.Name, StringComparison.InvariantCultureIgnoreCase)) continue;
			s += $"\n• {prop.Name} = {prop.Value} \t[{prop.DataType}{(prop.Flags == ObjectPropertyFlags.None ? null : ", " + prop.Flags.ToString())}]";
		}
		return s;
	}
}



public enum ObjectPropertyTypes
{
	/// <summary>Max. 256 chars</summary>
	String,
	Vector3,
	/// <summary>RGB color (no alpha).</summary>
	Color,
	Float,
	Flags, // aka. "Flags"
	Bool,
	Int,
	/// <summary>This is a four-tuple, but NOT neccessarily a quaternion - <b>take care!</b></summary>
	Rotation,
	HPC,
	CommandString, // Jupiter EX only
	Text, // Jupiter EX only
}
public enum ObjectPropertyTypesLegacy : byte
{
	String = 0,
	Vector3 = 1,
	Color = 2,
	Float = 3,
	Flags = 4, // also an int? never used?
	Bool = 5,
	Int = 6,
	Rotation = 7,
	UnkInt = 9, // "HPC"
}
public enum ObjectPropertyTypesJupEx : byte
{
	String = 0,
	Vector3 = 1,
	Color = 2,
	Float = 3,
	Bool = 4,
	Int = 5,
	Rotation = 6,
	CommandString = 7,
	Text = 8,
}

[Flags]
public enum ObjectPropertyFlags
{
	None = 0x0,
	/// <summary>Property doesn't show up in DEdit.</summary>
	PF_HIDDEN = 0x1,
	/// <summary>Property is a number to use as radius for drawing circle.  There can be more than one.</summary>
	PF_RADIUS = 0x2,
	/// <summary>Property is a vector to use as dimensions for drawing box. There can be only one.</summary>
	PF_DIMS = 0x4,
	/// <summary>Property is a field of view.</summary>
	PF_FIELDOFVIEW = 0x8,
	/// <summary>Used with PF_DIMS. Causes DEdit to show dimensions rotated with the object.</summary>
	PF_LOCALDIMS = 0x10,
	/// <summary>
	/// This property owns the group it's in.
	/// NOTE: Bits 7 (0x40) through 12 (0x800) are reserved for the group number as described below.
	/// </summary>
	PF_GROUPOWNER = 0x20,
	Group1 = 0x40,
	Group2 = 0x80,
	Group3 = 0x100,
	Group4 = 0x200,
	Group5 = 0x400,
	Group6 = 0x800,
	AnyGroup = Group1 | Group2 | Group3 | Group4 | Group5 | Group6,
	/// <summary>If PF_FIELDOFVIEW is set, this defines the radius for it.</summary>
	PF_FOVRADIUS = 0x1000,
	/// <summary>If the object is selected, DEdit draws a line to any objects referenced (by name) in PF_OBJECTLINK properties. It won't draw any more than MAX_OBJECTLINK_OBJECTS.</summary>
	PF_OBJECTLINK = 0x2000,
	/// <summary>This indicates to DEdit that a string property is a filename in the resource.</summary>
	PF_FILENAME = 0x4000,
	/// <summary>If this property is a vector and its object is on a path, the path is drawn as a bezier curve.  The curve segment from this object to the next is defined as (THIS.Pos, THIS.Pos + THIS.NextTangent, NEXT.Pos + NEXT.PrevTangent, NEXT.Pos).</summary>
	PF_BEZIERPREVTANGENT = 0x8000,
	PF_BEZIERNEXTTANGENT = 0x10000,
	/// <summary>This string property has a populatable combobox with dropdown-list style (ie listbox, no edit control)</summary>
	PF_STATICLIST = 0x20000,
	/// <summary>This string property has a populatable combobox with dropdown style (ie listbox+edit control)</summary>
	PF_DYNAMICLIST = 0x40000,
	/// <summary>This is a composite property with a custom dedit control that incorporates all group members.  This flag notifies DEdit that it should look for a custom control that matches the type name of this property.</summary>
	PF_COMPOSITETYPE = 0x80000,
	/// <summary>This property defines a measurement or other value that is in, or relative to, world coordinates.  If this flag is specified, any scaling done to the world as a whole for unit conversion will also be applied to this property.</summary>
	PF_DISTANCE = 0x100000,
	/// <summary>This property defines the filename of a model to be displayed for the object. This is usually used in conjunction with PF_FILENAME. If the path is not absolute, it will append the filename to the project directory.If no extension, or LTB is provided, it will look first for an LTA file, then an LTC.</summary>
	PF_MODEL = 0x200000,
	/// <summary>This property defines that the associated vector should be used to render an orthographic frustum from the object. This frustum will have the width and height specified in the X and Y values of the vector and the far clip plane specified in the Z value.</summary>
	PF_ORTHOFRUSTUM = 0x400000,
	/// <summary>If this property changes, PreHook_PropChanged() will be called to give it's object a chance to debug the new value.</summary>
	PF_NOTIFYCHANGE = 0x800000,
	/// <summary>This value specifies that the group should be treated as an event, and use the appropriate event editor dialog to edit the fields.</summary>
	PF_EVENT = 0x1000000,
	/// <summary>This value specifies that the field is the name of a texture script group and should allow the user to select from their premade texture scripts or create new ones.</summary>
	PF_TEXTUREEFFECT = 0x2000000,
}

[Flags]
public enum ObjectFlags
{
	None = 0x0,
	Visible = 0x1,
	/// <summary>Does this model cast shadows?</summary>
	ModelShadow = 0x2,
	/// <summary>Tells the polygrid to use unsigned bytes for its data</summary>
	PolygridUnsigned = 0x2,
	/// <summary>
	/// If this is set, it draws a model in 2 passes.  In the second pass, it scales down the color with ColorR, ColorG, and ColorB.  This is used to tint the skins
	/// in multiplayer.  Note: it uses powers of 2 to determine scale so the color scale maps like this:<br />
	/// &gt; 253 = 1.0; &gt; 126 = 0.5; &gt; 62 = 0.25; &gt; 29  = 0.12; otherwise 0.
	/// </summary>
	ModelTint = 0x4,
	/// <summary>Should this light cast shadows (slower)?</summary>
	LightCastShadows = 0x8,
	SpriteRotateable = 0x8,
	ModelGouraudShade = 0x8,
	/// <summary>
	/// If this is set, the engine will update particles even when they're invisible.
	/// You should check FLAG_WASDRAWN on any particle systems you're iterating over so you don't update invisible ones.
	/// </summary>
	PsUpdateUnseen = 0x8,
	/// <summary>Use the 'fastlight' method for this light.</summary>
	LightSolid = 0x10,
	/// <summary>Disabled.. doesn't do anything anymore.</summary>
	SpriteChromaKey = 0x10,
	/// <summary></summary>
	ModelWireframe = 0x10,
	/// <summary>
	/// The engine sets this if a particle system or PolyGrid was drawn. You can use this to determine whether or not to do some expensive calculations on it.</summary>
	PsWasDrawn = 0x10,
	/// <summary>Shrinks the sprite as the viewer gets nearer.</summary>
	SpriteGlow = 0x20,
	/// <summary>Tells it to only light the world.</summary>
	LightOnlyWorld = 0x20,
	/// <summary>Environment map the model.</summary>
	ModelEnvironmentMap = 0x20,
	/// <summary>For PolyGrids - says to only use the environment map (ignore main texture).</summary>
	PolyGridEnvironmentMapOnly = 0x20,
	/// <summary>Biases the Z towards the view so a sprite doesn't clip as much.</summary>
	SpriteBias = 0x40,
	/// <summary>Don't light backfacing polies.</summary>
	LightNoBackFacing = 0x40,
	/// <summary>Used for models really close to the view (like PV weapons).</summary>
	ModelReallyClose = 0x40,
	/// <summary>This light generates fog instead of light.</summary>
	LightIsFog = 0x80,
	/// <summary>Does a 200ms transition between model animations.</summary>
	ModelAnimTransition = 0x80,
	/// <summary>Disable Z read/write on sprite (good for lens flares). These sprites must not be chromakeyed.</summary>
	SpriteNoZ = 0x80,
	/// <summary>LT normally compresses the position and rotation info to reduce packet size. This flag disables it for better accuracy.</summary>
	FullPositionRes = 0x100,
	/// <summary>Just use the object's color and global light scale. (Don't affect by area or by dynamic lights).</summary>
	NoLight = 0x200,
	/// <summary>Don't draw this object if we're using software rendering.</summary>
	HardwareOnly = 0x400,
	/// <summary>Uses minimal network traffic to represent rotation</summary>
	YRotation = 0x800,
	/// <summary>Don't render this object thru the normal stuff, only render it when processing sky objects.</summary>
	SkyObject = 0x1000,
	/// <summary>Object can't go thru other solid objects.</summary>
	Solid = 0x2000,
	/// <summary>Use simple box physics on this object (used for WorldModels and containers).</summary>
	BoxPhysics = 0x4000,
	/// <summary>This object is solid on the server and nonsolid on the client.</summary>
	ClientNonSolid = 0x8000,
	/// <summary>Gets touch notification.</summary>
	TouchNotify = 0x10000,
	/// <summary>Gravity is applied.</summary>
	Gravity = 0x20000,
	/// <summary>Steps up stairs.</summary>
	StairStep = 0x40000,
	/// <summary>The object won't get get MID_MODELSTRINGKEY messages unless it sets this flag.</summary>
	ModelKeys = 0x80000,
	/// <summary>Save and restore this object when switching worlds.</summary>
	KeepAlive = 0x100000,
	/// <summary>Object can pass through world</summary>
	GoThroughWorld = 0x200000,
	/// <summary>Object is hit by raycasts.</summary>
	RayHit = 0x400000,
	/// <summary>Dont follow the object this object is standing on.</summary>
	DontFollowStanding = 0x800000,
	/// <summary>Force client updates even if the object is OT_NORMAL or invisible.</summary>
	ForceClientUpdate = 0x1000000,
	/// <summary>Object won't slide agaist polygons</summary>
	NoSliding = 0x2000000,
	/// <summary>Uses much (10x) faster physics for collision detection, but the object is a point.</summary>
	PointCollide = 0x4000000,
	/// <summary>Remove this object automatically if it gets outside the world.</summary>
	RemoveIfOutside = 0x8000000,
	/// <summary>Force the engine to optimize this object.</summary>
	ForceOptimizeObject = 0x10000000,
}

public struct ObjectProperty
{
	public string Name;
	public ObjectPropertyTypes DataType = ObjectPropertyTypes.String;
	public ObjectPropertyFlags Flags;
	public object Value;
	public int Length;

	public ObjectProperty(string name, ObjectPropertyTypes dataType, object value)
	{
		Name = name;
		DataType = dataType;
		Value = value;
	}
	public ObjectProperty(CustomBinaryReader br, WorldVersions version, CustomBinaryReader pbr)
	{
		if (version < WorldVersions.JupiterEx) {
			Name = br.ReadDATString();
		} else {
			pbr.Position = br.ReadInt32();
			Name = pbr.ReadStringNullTerminated();
		}
		if (version < WorldVersions.JupiterEx) {
			var dataType = (ObjectPropertyTypesLegacy)br.ReadByte();
			Flags = (ObjectPropertyFlags)br.ReadInt32();
			Length = br.ReadInt16();
			(Value, DataType) = ((object, ObjectPropertyTypes))(dataType switch {
				ObjectPropertyTypesLegacy.Int => ((int)br.ReadSingle(), ObjectPropertyTypes.Int), // Looks like it's stored as float?!
				ObjectPropertyTypesLegacy.Bool => (br.ReadBoolean(), ObjectPropertyTypes.Bool),
				ObjectPropertyTypesLegacy.Float => (br.ReadSingle(), ObjectPropertyTypes.Float),
				ObjectPropertyTypesLegacy.String => (br.ReadDATString(), ObjectPropertyTypes.String),
				ObjectPropertyTypesLegacy.Vector3 => (br.ReadVector3DX(), ObjectPropertyTypes.Vector3),
				ObjectPropertyTypesLegacy.Color => (br.ReadVector3DX().ToColor4(), ObjectPropertyTypes.Color),
				ObjectPropertyTypesLegacy.Flags => (br.ReadInt32(), ObjectPropertyTypes.Flags),
				ObjectPropertyTypesLegacy.UnkInt => (br.ReadInt32(), ObjectPropertyTypes.HPC),
				ObjectPropertyTypesLegacy.Rotation => (br.ReadQuaternionDX(), ObjectPropertyTypes.Rotation),
				_ => throw new NotImplementedException($"Invalid object property \"{Name}\" with type {dataType}"),
			});
		} else {
			var dataType = (ObjectPropertyTypesJupEx)br.ReadInt32();
			string ReadString() { pbr.Position = br.ReadInt32(); return pbr.ReadStringNullTerminated(); }
			Vector3 ReadVec3() { pbr.Position = br.ReadInt32(); return pbr.ReadVector3DX(); }
			Quaternion ReadQuat() { pbr.Position = br.ReadInt32(); return pbr.ReadQuaternionDX(); }
			bool ReadBool()
			{
				var value = br.ReadInt32();
				Debug.Assert(value == 0 || value == 1);
				return value != 0;
			}
			(Value, DataType) = ((object, ObjectPropertyTypes))(dataType switch {
				ObjectPropertyTypesJupEx.Int => (br.ReadInt32(), ObjectPropertyTypes.Int),
				ObjectPropertyTypesJupEx.Bool => (ReadBool(), ObjectPropertyTypes.Bool),
				ObjectPropertyTypesJupEx.Float => (br.ReadSingle(), ObjectPropertyTypes.Float),
				ObjectPropertyTypesJupEx.String => (ReadString(), ObjectPropertyTypes.String),
				ObjectPropertyTypesJupEx.Text => (ReadString(), ObjectPropertyTypes.Text),
				ObjectPropertyTypesJupEx.CommandString => (ReadString(), ObjectPropertyTypes.CommandString),
				ObjectPropertyTypesJupEx.Vector3 => (ReadVec3(), ObjectPropertyTypes.Vector3),
				ObjectPropertyTypesJupEx.Color => (ReadVec3().ToColor4(), ObjectPropertyTypes.Color),
				ObjectPropertyTypesJupEx.Rotation => (ReadQuat(), ObjectPropertyTypes.Rotation),
				_ => throw new NotImplementedException($"Invalid object property \"{Name}\" with type {dataType}"),
			});
		}
	}

	public readonly int Group {
		get {
			if ((Flags & ObjectPropertyFlags.Group1) != 0) return 1;
			if ((Flags & ObjectPropertyFlags.Group2) != 0) return 2;
			if ((Flags & ObjectPropertyFlags.Group3) != 0) return 3;
			if ((Flags & ObjectPropertyFlags.Group4) != 0) return 4;
			if ((Flags & ObjectPropertyFlags.Group5) != 0) return 5;
			if ((Flags & ObjectPropertyFlags.Group6) != 0) return 6;
			return 0;
		}
	}

	public override readonly string ToString() => $"\"{Name}\" = {Value} [{DataType}]   Flags: {Flags}";
}

public interface IBundleFile : IDisposable
{
	public struct FileEntry
	{
		public int Index;
		public string Path;
		public long Offset;
		public long Size;

		public override readonly string ToString() => $"#{Index}: \"{Path}\" Offset = {Offset}, Size = {Size}";
	}

	FileEntry[] Files { get; }
	byte[] GetData(FileEntry file);
}

public record struct FearMaterial(string MaterialName, string ShaderName, Dictionary<string, object> Settings);
