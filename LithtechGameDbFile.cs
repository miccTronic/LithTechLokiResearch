using ConversionLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using SharpDX;
using RadiantMapToObj.Internal;
using System.Runtime.InteropServices;
using System.Diagnostics;

namespace AllOtherResourceImporters.LithTech;

public class LithtechGameDbFile
{
	public List<DatabaseCategory> Categories = [];
	public Dictionary<string, DatabaseCategory> CategoriesByName = new(StringComparer.InvariantCultureIgnoreCase);
	public string CsvDebugString { get; }

	// For FEAR: https://github.com/burmaraider/JupiterEX_DatabaseExtractor
	// For FEAR2: https://github.com/Nenkai/Fear2Tools/tree/master
	public LithtechGameDbFile(string fileName)
	{
		const bool DebugLog = false;
		using var fs = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
		var br = new CustomBinaryReader(fs, Encoding.ASCII);
		if (br.ReadString(4) != "GADB") throw new FormatException("Not a game database file.");
		int version = br.ReadInt32(); // 3 is FEAR, 6 is FEAR2
		if (version != 3 && version != 6 && version != 7) throw new FormatException($"Unsupported GameDB version: {version}");
		int iUnk4 = 0, iUnk5 = 0;
		if (version >= 7) {
			iUnk4 = br.ReadInt32();
			iUnk5 = br.ReadInt32();
		}
		var valueTableLength = br.ReadInt32();
		var iUnk1 = br.ReadInt32();
		var iUnk2 = br.ReadInt32();
		var iUnk3 = br.ReadInt32();
		var iAttributeCount = br.ReadInt32(); // v6
		CsvDebugString = $"{fileName};{version};{fs.Length};{valueTableLength};{iAttributeCount};{iUnk1};{iUnk2};{iUnk3};{iUnk4};{iUnk5}";
		var valueTable = br.ReadBytes(valueTableLength);
		var vbr = new CustomBinaryReader(new MemoryStream(valueTable, false));
		string GetTableString(int offset = -1, Encoding encoding = null)
		{
			vbr.Position = offset < 0 ? br.ReadInt32() : offset;
			return vbr.ReadStringNullTerminated(encoding: encoding);
			//nbr.AlignPosition(4);
		}
		Vector2 GetTableVec2(int offset = -1)
		{
			vbr.Position = offset < 0 ? br.ReadInt32() : offset;
			return new(vbr.ReadSingle(), vbr.ReadSingle());
		}
		Vector3 GetTableVec3(int offset = -1)
		{
			vbr.Position = offset < 0 ? br.ReadInt32() : offset;
			return new(vbr.ReadSingle(), vbr.ReadSingle(), vbr.ReadSingle());
		}
		Quaternion GetTableQuat(int offset = -1)
		{
			vbr.Position = offset < 0 ? br.ReadInt32() : offset;
			return new Quaternion(vbr.ReadSingle(), vbr.ReadSingle(), vbr.ReadSingle(), vbr.ReadSingle()).FlipWIndex();
		}

		DatabaseAttributeInfo[] attributeTable = null;
		if (version >= 6) attributeTable = br.ReadTArray<DatabaseAttributeInfo>(iAttributeCount);
		//if (DebugLog) {
		//	for (int i = 0; i < attributeTable.Length; i++) {
		//		Debug.WriteLine($"Attribute #{i}: {attributeTable[i]}");
		//	}
		//}

		var numCategories = br.ReadInt32();
		//int lastAttributeTableOffset = -1, lastNumAttribs = -1;
		for (int i = 0; i < numCategories; i++) {
			if (DebugLog) Debug.Write($"Category #{i}/{numCategories} @ {br.Position}: ");
			var cat = new DatabaseCategory() {
				Index = i,
				Name = GetTableString(),
			};
			var numRecords = br.ReadInt32();
			if (DebugLog) Debug.WriteLine($"{cat}");
			if (version >= 6 && CalcHash(cat.Name) != br.ReadInt32()) throw new FormatException("Category name hash did not match.");
			for (int j = 0; j < numRecords; j++) {
				if (DebugLog) Debug.Write($"   Record #{j}/{numRecords} @ {br.Position}: ");
				var rcd = new DatabaseRecord() {
					Index = j,
					Name = GetTableString(),
				};
				var recordDataSize = version >= 6 ? br.ReadInt32() : -1;
				var numAttribs = br.ReadInt32();
				var attributeTableOffset = version >= 6 ? br.ReadInt32() : -1;
				var dataOffsetsUsed = version >= 6 ? new HashSet<int>(Enumerable.Range(0, recordDataSize)) : null;
				//if (attributeTableOffset != lastAttributeTableOffset) {
				//	if (lastAttributeTableOffset >= 0) Debug.Assert(attributeTableOffset == lastAttributeTableOffset + lastNumAttribs);
				//	lastAttributeTableOffset = attributeTableOffset;
				//	lastNumAttribs = numAttribs;
				//}
				if (DebugLog) Debug.WriteLine($"{rcd}, TblPos = {attributeTableOffset}, RcdSize = {recordDataSize} ({recordDataSize * 4} b)");
				if (version >= 6 && CalcHash(rcd.Name) != br.ReadInt32()) throw new FormatException("Record name hash did not match.");
				var dbr = version >= 6 ? new CustomBinaryReader(new MemoryStream(br.ReadBytes(recordDataSize * 4))) : null;
				object ReadAttributeValue(AttributeType type)
				{
					var xbr = version >= 6 ? dbr : br;
					return type switch {
						AttributeType.Bool => xbr.ReadInt32() != 0,
						AttributeType.Float => xbr.ReadSingle(),
						AttributeType.Int => xbr.ReadInt32(),
						AttributeType.String => GetTableString(xbr.ReadInt32()),
						AttributeType.WString => GetTableString(xbr.ReadInt32(), encoding: Encoding.Unicode),
						AttributeType.Vector2 => GetTableVec2(xbr.ReadInt32()),
						AttributeType.Vector3 => GetTableVec3(xbr.ReadInt32()),
						AttributeType.Vector4 => GetTableQuat(xbr.ReadInt32()),
						AttributeType.RecordLink => new RecordLink {
							RecordIndex = xbr.ReadInt16(),
							CategoryIndex = xbr.ReadInt16()
						},
						AttributeType.Struct when version == 3 => xbr.ReadInt32(),
						AttributeType.Struct => new RecordLink {
							RecordIndex = xbr.ReadInt16(),
							CategoryIndex = xbr.ReadInt16()
						},
						_ => throw new NotImplementedException(),
					};
				}
				for (int k = 0; k < numAttribs; k++) {
					if (DebugLog) Debug.Write($"      Attribute #{k}/{numAttribs}: ");
					DatabaseAttribute attr;
					if (version == 3) {
						// FEAR 1
						attr = new DatabaseAttribute() {
							Name = GetTableString(),
							Type = (AttributeType)br.ReadInt32(),
							Usage = (AttributeUsage)br.ReadInt32(),
							Values = new object[br.ReadInt32()], 
						};
						if (DebugLog) Debug.WriteLine($"{attr}");
						for (int l = 0; l < attr.Values.Length; l++) {
							attr.Values[l] = ReadAttributeValue(attr.Type);
						}
					} else {
						// FEAR 2
						var attrInfo = attributeTable[attributeTableOffset + k];
						if (DebugLog && !dataOffsetsUsed.Remove(attrInfo.PositionInRecordData)) {
							Debug.Write($"!!!---DUPLICATE---!!!");
						}
						var dataOffset = attrInfo.PositionInRecordData * 4;
						//if (attrInfo.Bits > 0x3F) Console.WriteLine($"{Name}: {entry.Bits >> 6}");
						attr = new DatabaseAttribute() {
							Type = attrInfo.Type,
							Usage = attrInfo.Usage,
							Values = new object[attrInfo.ArrayLength],
						};
						attr.Name = GetNameFromHash(attrInfo.NameHash);
						if (DebugLog) Debug.WriteLine($"{attr}, RcdPos = {attrInfo.PositionInRecordData}");
						for (var l = 0; l < attrInfo.ArrayLength; l++) {
							dbr.Position = dataOffset;
							if (dbr.EOS) {
								if (DebugLog) Debug.WriteLine($"! GameDb \"{Path.GetFileName(fileName)}\" beyond EOS in cat #{i} rcd #{j} attr #{k}/{numAttribs} value #{l}/{attrInfo.ArrayLength}.");
								break;
							}
							attr.Values[l] = ReadAttributeValue(attrInfo.Type);
							if (DebugLog) Debug.WriteLine($"         Value #{l}/{attrInfo.ArrayLength} @ {dataOffset} in DBR: {attr.Values[l]} [{attr.Values[l]?.GetType()?.Name}]");
							dataOffset += 4;
						}
					}
					rcd.Attributes.Add(attr);
					rcd.AttributesByName.TryAdd(attr.Name, attr);
				}
				cat.Records.Add(rcd);
				cat.RecordsByName.TryAdd(rcd.Name, rcd);
			}
			Categories.Add(cat);
			CategoriesByName.TryAdd(cat.Name, cat);
		}
	}


	private static readonly byte[] hashSBox =
	[
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x25, 0x3A, 0x33, 0x3C, 0x3D, 0x3E, 0x40, 0x32, 0x42, 0x43, 0x41, 0x28, 0x36, 0x27, 0x37, 0x34,
		0x1B, 0x1C, 0x1D, 0x1E, 0x1F, 0x20, 0x21, 0x22, 0x23, 0x24, 0x31, 0x30, 0x38, 0x29, 0x39, 0x35,
		0x3B, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
		0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x2C, 0x2A, 0x2D, 0x3F, 0x26,
		0x44, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
		0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x2E, 0x2B, 0x2F, 0x45, 0x00,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
		0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
	];
	/// <summary>FEAR 2 only.</summary>
	public static int CalcHash(string str)
	{
		int result = 0;
		for (var i = 0; i < str.Length; i++) result = hashSBox[str[i]] + 919 * result;
		return result;
	}

	private static Dictionary<int, string> HashToName;
	public static string GetNameFromHash(int hash)
	{
		if (HashToName == null) {
			HashToName = new();
			foreach (var line in File.ReadAllLines(ConvTools.OnedriveDir + @"\Software\GameRes\FEAR\HashedNames.txt")) {
				if (line.StartsWith("//")) continue;
				HashToName[CalcHash(line)] = line.Trim();
			}
		}
		if (HashToName.TryGetValue(hash, out var attrName)) {
			return attrName;
		} else {
			return $"0x{hash:X8}";
		}
	}



	public enum AttributeType
	{
		Invalid,
		Bool,
		Float,
		Int,
		String,
		WString,
		Vector2,
		Vector3,
		Vector4,
		RecordLink,
		Struct,
	}
	public enum AttributeUsage
	{
		Default,
		Filename,
		ClientFX,
		Animation,
	}

	public class DatabaseCategory
	{
		public int Index;
		public string Name;
		public List<DatabaseRecord> Records = [];
		public Dictionary<string, DatabaseRecord> RecordsByName = new(StringComparer.InvariantCultureIgnoreCase);
		public override string ToString() => $"Cat \"{Name}\", #Records = {Records.Count}";
	}

	public class DatabaseRecord
	{
		public int Index;
		public string Name;
		public List<DatabaseAttribute> Attributes = [];
		public Dictionary<string, DatabaseAttribute> AttributesByName = new(StringComparer.InvariantCultureIgnoreCase);
		public override string ToString() => $"Rcd \"{Name}\", #Attributes = {Attributes.Count}";

		public string GetAttribString(string name)
		{
			if (AttributesByName.TryGetValue(name, out var attr) && attr.Values != null && attr.Values.Length > 0 && attr.Values[0] is string value) return value;
			return null;
		}
		public int GetAttribInt(string name)
		{
			if (AttributesByName.TryGetValue(name, out var attr) && attr.Values != null && attr.Values.Length > 0 && attr.Values[0] is int value) return value;
			return 0;
		}
		public DatabaseRecord GetAttribRecord(string name, LithtechGameDbFile gamdb)
		{
			if (AttributesByName.TryGetValue(name, out var attr) && attr.Values != null && attr.Values.Length > 0 && attr.Values[0] is RecordLink link) {
				if (link.CategoryIndex >= 0 && gamdb.Categories.Count > link.CategoryIndex) {
					if (link.RecordIndex >= 0 && gamdb.Categories[link.CategoryIndex].Records.Count > link.RecordIndex) {
						return gamdb.Categories[link.CategoryIndex].Records[link.RecordIndex];
					}
				}
			}
			return null;
		}
	}

	public class DatabaseAttribute
	{
		public string Name;
		public AttributeType Type;
		public AttributeUsage Usage;
		public object[] Values;
		public ObjectPropertyTypes ObjectPropertyType => Type switch {
			AttributeType.Invalid => throw new NotSupportedException(),
			AttributeType.Bool => ObjectPropertyTypes.Bool,
			AttributeType.Float => ObjectPropertyTypes.Float,
			AttributeType.Int => ObjectPropertyTypes.Int,
			AttributeType.String => ObjectPropertyTypes.String,
			AttributeType.WString => ObjectPropertyTypes.String,
			AttributeType.Vector2 => throw new NotImplementedException(),
			AttributeType.Vector3 => ObjectPropertyTypes.Vector3,
			AttributeType.Vector4 => throw new NotImplementedException(),
			AttributeType.RecordLink => throw new NotImplementedException(),
			AttributeType.Struct => throw new NotImplementedException(),
			_ => throw new NotImplementedException(),
		};
		public override string ToString()
		{
			string s = $"\"{Name}\" [{Type}] = ";
			for (int i = 0; i < 3; i++) {
				if (i >= Values.Length) break;
				if (i > 0) s += ", ";
				if (Values[i] == null) s += "NULL";
				else s += Values[i].ToString();
			}
			if (Values.Length > 3) s += ",...";
			s += "; Usage = " + Usage.ToString();
			return s;
		}
	}

	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	public struct DatabaseAttributeInfo
	{
		public int NameHash;
		public byte Bits;
		public byte ArrayLength;
		public ushort PositionInRecordData;

		public readonly AttributeUsage Usage => (AttributeUsage)((Bits & 0xC0) >> 6);
		public readonly AttributeType Type => (AttributeType)(Bits & 0x3F);
		public override readonly string ToString() => $"Name = #{GetNameFromHash(NameHash)}, Type = {Type}, RcdPos = {PositionInRecordData}, Values = {ArrayLength}, Usage = {Usage}";
	}

	public struct RecordLink
	{
		public short RecordIndex;
		public short CategoryIndex;
		public override readonly string ToString() => $"Cat = #{CategoryIndex}, Rcd = #{RecordIndex}";
	}

}
