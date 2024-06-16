using ConversionLib;
using SharpDX;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace AllOtherResourceImporters.LithTech;

// https://github.com/PMArkive/quickbms/blob/0ccdc959ac78228778fbcb3961902b29e35dffb4/__quickbms_scripts/fear.bms#L4
/// <summary>
/// Covers LithTech 5 .bndl archives. For .lvbndl, see <see cref="LithtechLvBndlFile"/>.
/// </summary>
public class LithtechBndlFile : IBundleFile
{
	private CustomBinaryReader br;

	public IBundleFile.FileEntry[] Files { get; }

	public LithtechBndlFile(string fileName)
	{
		var fs = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
		br = new CustomBinaryReader(fs, Encoding.ASCII);
		var magic = br.ReadString(4);
		if (magic != "BNDL") {
			br.Position -= 4;
			if (br.ReadInt32() == 15) {
				// Some strange invalid bundle type, seems always the same contents... silently ignore
				Files = [];
				return;
			} else if (fs.Length < 100) {
				Files = [];
				Debug.WriteLine("! Ignoring too-short invalid bundle file: " + fileName);
				return;
			}
			throw new FormatException("Not a bundle file.");
		}
		if (br.ReadInt32() != 3) throw new FormatException("Unsupported file version.");
		var nameTableLen = br.ReadInt32();
		var unk1 = br.ReadInt32();
		var filesToSkip = br.ReadInt32();
		var numFiles = br.ReadInt32();
		var nameTable = br.ReadBytes(nameTableLen);
		var nbr = new CustomBinaryReader(new MemoryStream(nameTable, false));
		string GetTableString(int offset = -1, Encoding encoding = null)
		{
			nbr.Position = offset < 0 ? br.ReadInt32() : offset;
			return nbr.ReadStringNullTerminated(encoding: encoding);
			//nbr.AlignPosition(4);
		}
		Files = new IBundleFile.FileEntry[filesToSkip + numFiles];
		int f = 0;
		for (int i = 0; i < filesToSkip; i++) {
			Files[f++] = new IBundleFile.FileEntry() {
				Index = f,
				Path = GetTableString()
			};
		}
		for (int i = 0; i < numFiles; i++) {
			Files[f++] = new IBundleFile.FileEntry() {
				Index = f,
				Path = GetTableString(),
				Size = br.ReadUInt32(), // last item may overshoot
				Offset = br.Position,
			};
			br.Position += Files[f - 1].Size;
		}
	}

	public void Dispose()
	{
		br?.Dispose();
		br = null;
	}

	public byte[] GetData(IBundleFile.FileEntry file)
	{
		ObjectDisposedException.ThrowIf(br == null, this);
		if (file.Offset == 0) return null;
		br.Position = file.Offset;
		return br.ReadBytesLong(file.Size);
	}

	public string Dump()
	{
		StringBuilder sb = new();
		for (int i = 0; i < Files.Length; i++) {
			if (string.IsNullOrEmpty(Files[i].Path)) continue;
			sb.AppendLine($"#{i}: {Files[i].Path}   ({Files[i].Size} b)");
		}
		return sb.ToString();
	}
}



/// <summary>
/// Covers LithTech 5 .lvbndl archives.
/// </summary>
public class LithtechLvBndlFile : IBundleFile
{

	private CustomBinaryReader br;

	public IBundleFile.FileEntry[] Files { get; }

	public LithtechLvBndlFile(string fileName)
	{
		var fs = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
		br = new CustomBinaryReader(fs, Encoding.ASCII);
		if (br.ReadString(4) != "LVRS") throw new FormatException("Not a level bundle file.");
		if (br.ReadInt32() != 1) throw new FormatException("Unsupported file version.");
		var numFiles = br.ReadInt32();
		var nameTableLen = br.ReadInt32();
		var nameTable = br.ReadBytes(nameTableLen);
		var nbr = new CustomBinaryReader(new MemoryStream(nameTable, false));
		// Index file names
		var fileNames = new List<string>(numFiles);
		int lastStart = 0;
		for (int i = 0; i < nameTable.Length; i++) {
			if (nameTable[i] == 0) {
				fileNames.Add(Encoding.ASCII.GetString(nameTable[lastStart..i]));
				if ((i + 1) % 4 != 0) i += 4 - ((i + 1) % 4);
				lastStart = i + 1;
			}
		}
		if (lastStart < nameTable.Length) fileNames.Add(Encoding.ASCII.GetString(nameTable[lastStart..]));
		Debug.Assert(fileNames.Count == numFiles);

		Files = new IBundleFile.FileEntry[numFiles];
		for (int i = 0; i < numFiles; i++) {
			var size = br.ReadUInt32();
			Files[i].Index = i;
			Files[i].Path = fileNames[i];
			Files[i].Offset = br.Position;
			Files[i].Size = size;
			br.Position += size;
		}
	}

	public void Dispose()
	{
		br?.Dispose();
		br = null;
	}

	public byte[] GetData(IBundleFile.FileEntry file)
	{
		ObjectDisposedException.ThrowIf(br == null, this);
		br.Position = file.Offset;
		return br.ReadBytesLong(file.Size);
	}

	public string Dump()
	{
		StringBuilder sb = new();
		for (int i = 0; i < Files.Length; i++) {
			if (string.IsNullOrEmpty(Files[i].Path)) continue;
			sb.AppendLine($"#{i}: {Files[i].Path}   ({Files[i].Size} b)");
		}
		return sb.ToString();
	}
}


/// <summary>
/// Covers LithTech 5 .matlib material libraries.
/// </summary>
public class LithtechMatLibFile
{
	public enum DataTypes : short
	{
		FileName = 1,
		Vector3 = 2,
		Color4 = 3,
		Integer = 4,
		Float = 5,
	}
	public record struct NameInfo(string Name, int ParentOffset);
	public readonly record struct PropertyInfo(string Name, DataTypes Type, ushort Unk);
	public class Material
	{
		public string FileName;
		public string ShaderName;
		public Dictionary<string, object> Properties;
	}

	public FearMaterial[] Materials { get; }

	public LithtechMatLibFile(string fileName)
	{
		using var br = File.ReadAllBytes(fileName).AsCustomBinaryReader();
		br.Encoding = Encoding.ASCII;
		if (br.ReadString(4) != "MTLB") throw new FormatException("Not a matlib file.");
		if (br.ReadInt32() != 4) throw new FormatException("Unsupported file version.");
		var count1 = br.ReadInt32();
		var materialCount = br.ReadInt32();
		var count3 = br.ReadInt32();
		var count4 = br.ReadInt32();
		var proptableOffset = br.ReadInt32();
		var assignmentsOffset = br.ReadInt32();
		const int hdrSize = 32; // all offsets are conted from there

		NameInfo ReadNameInfo(int offset = -1)
		{
			if (offset < 0) offset = br.ReadInt32();
			var oldPos = br.Position; br.Position = hdrSize + offset;
			try {
				var result = new NameInfo() { ParentOffset = br.ReadUInt16() | (br.ReadByte() << 16), Name = br.ReadStringNullTerminated() };
				if (result.ParentOffset < 16777215) {
					result.Name = ReadNameInfo(result.ParentOffset).Name + "\\" + result.Name;
				}
				return result;
			} finally {
				br.Position = oldPos;
			}
		}
		PropertyInfo ReadPropertyInfo(int offset = -1)
		{
			if (offset < 0) offset = br.ReadInt32();
			var oldPos = br.Position; br.Position = hdrSize + offset;
			try {
				// unk could also be 2 bytes... they are often 0 or 11, and often used in conjunction with similar properties, e.g. tVideoTextureRPlane / tVideoTextureYPlane.
				return new PropertyInfo() {
					Type = (DataTypes)br.ReadInt16(),
					Unk = br.ReadUInt16(),
					Name = br.ReadStringNullTerminated().Replace("k_", ""), // for compatibility
				};
			} finally {
				br.Position = oldPos;
			}
		}
		Vector3 ReadVector3(int offset = -1)
		{
			if (offset < 0) offset = br.ReadInt32();
			var oldPos = br.Position; br.Position = hdrSize + offset;
			try {
				return br.ReadVector3DX();
			} finally {
				br.Position = oldPos;
			}
		}
		Color4 ReadColor(int offset = -1)
		{
			if (offset < 0) offset = br.ReadInt32();
			var oldPos = br.Position; br.Position = hdrSize + offset;
			try {
				return br.ReadColor4DX_Rgba();
			} finally {
				br.Position = oldPos;
			}
		}

		br.Position = hdrSize;
		// Following is the name table @ hdrSize...
		br.Position = hdrSize + proptableOffset;
		// Then the property table @ hdrSize + proptableOffset...
		br.Position = hdrSize + assignmentsOffset;
		Materials = new FearMaterial[materialCount];
		for (int i = 0; i < materialCount; i++) {
			var matName = ReadNameInfo();
			var shader = ReadNameInfo();
			uint something = br.ReadUInt32();
			//ushort something2 = br.ReadUInt16(); ushort something3 = br.ReadUInt16();
			int propertyCount = br.ReadInt32();
			var mat = new FearMaterial {
				MaterialName = matName.Name,
				ShaderName = shader.Name,
				Settings = new(propertyCount),
			};
			//Debug.WriteLine($"Material #{i}: \"{matName.Name}\", Shader = \"{shader.Name}\"");
			for (int j = 0; j < propertyCount; j++) {
				var propInfo = ReadPropertyInfo();
				object value = propInfo.Type switch {
					DataTypes.FileName => ReadNameInfo().Name,
					DataTypes.Integer => br.ReadInt32(),
					DataTypes.Float => br.ReadSingle(),
					DataTypes.Vector3 => ReadVector3(),
					DataTypes.Color4 => ReadColor(),
					_ => throw new NotImplementedException()
				};
				//if (propInfo.Unk != 0 && propInfo.Unk != 11) Debug.WriteLine($"   Prop #{j}: \"{propInfo.Name}\" = {value}\tType = {propInfo.Type}\tUnk = {propInfo.Unk}");
				mat.Settings.Add(propInfo.Name, value);
			}
			Materials[i] = mat;
		}
	}

}
