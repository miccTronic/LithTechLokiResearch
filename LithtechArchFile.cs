using ConversionLib;
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
/// Covers LithTech Jupiter EX archives.
/// .arch00: FEAR, Condemned
/// .arch01: FEAR 2, Gotham City Impostors
/// .arch02: Condemned 2 [NOT IMPLEMENTED]
/// .arch04: Guardians of Middle Earth [NOT IMPLEMENTED]
/// .arch05: Shadow of Mordor [NOT IMPLEMENTED]
/// .arch06: Shadow of War [NOT IMPLEMENTED]
/// </summary>
public class LithtechArchFile : IDisposable
{
	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	public struct FileEntry
	{
		// 4 - Filename Offset (relative to the start of the names directory)
		public int NameOffset;
		public long DataOffset;
		// We need to keep the empty files here, because they're used in the directory name calculation later on.
		public long CompressedLength;
		public long UncompressedLength;
		// 4 - Compression Flag (0=Uncompressed, 9=Chunked ZLib Compression)
		public int Flags;

		public FileEntry(CustomBinaryReader br)
		{
			NameOffset = br.ReadInt32();
			DataOffset = br.ReadInt64();
			CompressedLength = br.ReadInt64();
			UncompressedLength = br.ReadInt64();
			Flags = br.ReadInt32();
		}
		public override readonly string ToString() => $"Offset = {DataOffset}, CLength = {CompressedLength}, ULength = {UncompressedLength}, Flags = {Flags}, NameOffset = {NameOffset}";
	}
	[StructLayout(LayoutKind.Sequential, Pack = 1)]
	public struct FolderEntry
	{
		// 4 - Folder Name Offset (relative to the start of the names directory)
		public int NameOffset;
		public int FirstSubfolderIndex;
		public int NextSiblingIndex;
		public int FilesCount;

		public FolderEntry(CustomBinaryReader br)
		{
			NameOffset = br.ReadInt32();
			FirstSubfolderIndex = br.ReadInt32();
			NextSiblingIndex = br.ReadInt32();
			FilesCount = br.ReadInt32();
		}
		public override readonly string ToString() => $"#FilesCount = {FilesCount}, ChildIdx = {FirstSubfolderIndex}, NexIdx = {NextSiblingIndex}, NameOffset = {NameOffset}";
	}

	public struct ActualFolderEntry
	{
		public FolderEntry Entry;
		public string Name;
		//public string Path;
		public ActualFileEntry[] Files;

		public override string ToString() => $"Folder \"{Name}\", {Entry}";
	}
	public struct ActualFileEntry
	{
		public FileEntry Entry;
		public string Name;
		public string Path;

		public override string ToString() => $"File \"{Name}\", {Entry}";
	}


	private CustomBinaryReader br;

	public ActualFolderEntry[] Folders { get; }
	public ActualFileEntry[] Files { get; }

	public LithtechArchFile(string fileName)
	{
		int aver = int.Parse(fileName[^2..]);
		var fs = File.Open(fileName, FileMode.Open, FileAccess.Read, FileShare.Read);
		br = new CustomBinaryReader(fs, Encoding.ASCII);
		//if (Path.GetExtension(fileName).Equals(".arch00", StringComparison.InvariantCultureIgnoreCase)) {
		//	if (br.ReadString(4) != "LTAR") throw new FormatException("Not an arch00 file.");
		//} else if (Path.GetExtension(fileName).Equals(".arch01", StringComparison.InvariantCultureIgnoreCase)) {
		//	throw new NotImplementedException();
		//} else {
		//	throw new FormatException("Unknown file extension.");
		//}
		// NOTE: See QuickBMS script if magic is any of the following: unzip_dynamic . LTAR . RATL . LTAX . xmemdecompress . XATL . PTAR . RATP . PSCA . ACSP . HBEK . KEBH . CRC/ . md5 . calldll 
		string magic = br.ReadString(4);
		if (magic == "RATL") br.InvertEndianess = true;
		else if (magic != "LTAR") throw new FormatException("Not an archXX file.");
		int version = br.ReadInt32();
		if (version != 3) throw new FormatException($"Unsupported file version {version}.");

		var nameTableLen = br.ReadInt32();
		var numFolders = br.ReadInt32();
		var numFiles = br.ReadInt32();
		var iUnk1 = br.ReadInt32(); // often 1 or 0
		var iUnk2 = br.ReadInt32(); // often 0
		var iUnk3 = br.ReadInt32(); // often 1 or 0
		var crc_or_hash = br.ReadGuid();
		// name table
		var nameTable = br.ReadBytes(nameTableLen);
		var nbr = new CustomBinaryReader(new MemoryStream(nameTable, false)) { InvertEndianess = br.InvertEndianess };
		//var files = br.ReadTArray<FileEntry>(numFiles); // <-- does not work unfortunately if the endianess is inverted :-(
		//var folders = br.ReadTArray<FolderEntry>(numFolders);
		var files = new FileEntry[numFiles];
		for (int i = 0; i < files.Length; i++) files[i] = new(br);
		var folders = new FolderEntry[numFolders];
		for (int i = 0; i < folders.Length; i++) folders[i] = new(br);

		// Build folder tree
		var rootFolder = folders.GetIndexWhere(f => f.NameOffset == 0);
		if (rootFolder < 0) throw new FormatException("Root folder not found");
		Folders = new ActualFolderEntry[numFolders];
		void BuildFolderRecursive(int i, string path)
		{
			Folders[i].Entry = folders[i];
			Folders[i].Files = new ActualFileEntry[folders[i].FilesCount];
			if (folders[i].NameOffset != 0) {
				nbr.Position = folders[i].NameOffset;
				Folders[i].Name = nbr.ReadStringNullTerminated(); //nbr.AlignPosition(4);
			}
			if (folders[i].FirstSubfolderIndex > -1) {
				BuildFolderRecursive(folders[i].FirstSubfolderIndex, path + (path == "" ? "" : "\\") + Folders[i].Name);
			}
			if (folders[i].NextSiblingIndex > -1) {
				BuildFolderRecursive(folders[i].NextSiblingIndex, path);
			}
		}
		BuildFolderRecursive(rootFolder, "");

		// Build basic files list
		Files = new ActualFileEntry[numFiles];
		for (int i = 0; i < files.Length; i++) {
			Files[i].Entry = files[i];
			if (files[i].CompressedLength > 0) {
				nbr.Position = files[i].NameOffset;
				Files[i].Name = nbr.ReadStringNullTerminated(); //nbr.AlignPosition(4);
			}
		}

		// Build file list
		int f = 0;
		for (int i = 0; i < folders.Length; i++) {
			for (int j = 0; j < folders[i].FilesCount; j++) {
				if (files[f].CompressedLength > 0) {
					Files[f].Path = Folders[i].Name;
					if (!string.IsNullOrWhiteSpace(Files[f].Path)) Files[f].Path += "\\";
					Files[f].Path += Folders[i].Name;
				}
				Folders[i].Files[j] = Files[f];
				f++;
			}
		}
	}

	public void Dispose()
	{
		br?.Dispose();
		br = null;
	}

	public byte[] GetData(FileEntry file)
	{
		ObjectDisposedException.ThrowIf(br == null, this);
		if (file.CompressedLength == 0 || file.UncompressedLength == 0) return [];
		if (file.DataOffset <= 0 || file.CompressedLength < 0 || file.UncompressedLength < 0) throw new IndexOutOfRangeException();
		if (file.UncompressedLength > int.MaxValue) throw new NotImplementedException(); // NOTE: There is 1 file for "Condemend" for which this happens (probably wrong format)
		br.Position = file.DataOffset;
		if (file.CompressedLength == file.UncompressedLength) {
			return br.ReadBytesLong(file.CompressedLength);
		}
		// Decompress each block
		long read = 0;
		var ms = new MemoryStream();
		while (read < file.CompressedLength) {
			// 4 - Compressed Block Length
			int compLength = br.ReadInt32();
			if (br.InvertEndianess) throw new NotImplementedException("Decompression of XBox files is currently unsupported. It seems there's only a single length given here, and the compression is different. See the LTAX case in the Fear.bms script (use that instead).");
			// 4 - Decompressed Block Length
			int decompLength = br.ReadInt32();
			if (compLength < 0 || compLength > 100000 || decompLength < 0 || decompLength > 100000) {
				throw new FormatException("Bad data");
			}
			var compData = br.ReadBytes(compLength); // so Inflator doesn't overshoot
			// compressed blocks are padded to multiples of 4 bytes
			var padding = br.AlignPosition(4);
			if (compLength == decompLength) {
				ms.Write(compData);
			} else {
				// Decompress the block
				try {
					var decompData = ConvTools.DescompressZLib(compData, decompLength);
					ms.Write(decompData);
				} catch (ICSharpCode.SharpZipLib.SharpZipBaseException ex) {
					if (false) throw;
				}
			}
			read += compLength + 8 + padding;
		}
		Debug.Assert(read == file.CompressedLength);
		return ms.ToArray();
	}

	public string Dump(bool flat)
	{
		StringBuilder sb = new();
		if (flat) {
			for (int i = 0; i < Files.Length; i++) {
				if (string.IsNullOrEmpty(Files[i].Name)) continue;
				sb.AppendLine($"    {Files[i].Path}\\{Files[i].Name}   ({Files[i].Entry.CompressedLength} b)");
			}
		} else {
			for (int i = 0; i < Folders.Length; i++) {
				sb.AppendLine("FOLDER #{i}: " + Folders[i].Name);
				foreach (var file in Folders[i].Files) {
					sb.AppendLine($"    {file.Name}   ({file.Entry.CompressedLength} b)");
				}
			}
		}
		return sb.ToString();
	}
}
