using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;

namespace ConversionLib
{
	public class CustomBinaryReader : BinaryReader
	{
		public Encoding Encoding { get; set; }
		public bool InvertEndianess { get; set; } = false;

		public CustomBinaryReader(Stream input) : base(input, Encoding.Default)
		{
			Encoding = Encoding.Default;
		}
		public CustomBinaryReader(Stream input, Encoding encoding) : base(input, encoding)
		{
			Encoding = encoding;
		}
		public CustomBinaryReader(string fileName) : this(new MemoryStream(File.ReadAllBytes(fileName)))
		{
		}
		public CustomBinaryReader(string fileName, Encoding encoding) : this(fileName)
		{
			Encoding = encoding;
		}

		public long Position {
			get => BaseStream.Position;
			set => BaseStream.Position = value;
		}
		public long Length => BaseStream.Length;
		public bool EOS => Position >= Length;

		public int AlignPosition(int bytes)
		{
			int padding = bytes - ((int)Position % bytes);
			if (padding != bytes) {
				Position += padding;
				return padding;
			} else {
				return 0;
			}
		}

		public override byte[] ReadBytes(int count)
		{
			if (BaseStream.Position + count > BaseStream.Length) throw new EndOfStreamException($"Stream is at position {BaseStream.Position}/{BaseStream.Length} - cannot read {count} bytes");
			return base.ReadBytes(count);
		}
		/// <summary>Reads all remaining bytes.</summary>
		public byte[] ReadBytes() => ReadBytes((int)(BaseStream.Length - BaseStream.Position));
		public void ReadBytes(byte[] buffer, int offset, int count)
		{
			if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "Non-negative number required.");
			while (count > 0) {
				int bytesRead;
				if ((bytesRead = BaseStream.Read(buffer, offset, count)) == 0) throw new EndOfStreamException();
				offset += bytesRead;
				count -= bytesRead;
			}
		}
		private static byte[] sharedReadBuffer;
		public byte[] ReadBytesLong(long count)
		{
			if (count < 0) throw new ArgumentOutOfRangeException(nameof(count), "Non-negative number required.");
			sharedReadBuffer ??= new byte[1024 * 1024];
			var outBuffer = new byte[count];
			long offset = 0;
			long remaining = count;
			while (remaining > 0) {
				int bytesRead;
				int max = sharedReadBuffer.Length;
				if (remaining < max) max = (int)remaining;
				if ((bytesRead = BaseStream.Read(sharedReadBuffer, 0, max)) == 0) throw new EndOfStreamException();
				Array.Copy(sharedReadBuffer, 0, outBuffer, offset, bytesRead);
				offset += bytesRead;
				remaining -= bytesRead;
			}
			return outBuffer;
		}

		/// <param name="length">Length of the string to read in <b>BYTES</b>, not characters! The length is <b>INCLUDING</b> the 0 character, i.e. a length of 0 will not read any bytes.</param>
		public string ReadStringNullTerminated(int length = -1, Encoding encoding = null, bool singleByteNull = false)
		{
			if (length == 0) return string.Empty;
			encoding ??= Encoding;
			bool isUtf8 = encoding.HeaderName == "utf-8";
			var bytes = new List<byte>();
			int i = 0;
			do {
				byte b = ReadByte();
				i++;
				if (encoding.IsSingleByte || isUtf8 || singleByteNull) {
					if (b == 0) break;
					bytes.Add(b);
				} else {
					byte b2 = ReadByte();
					i++;
					if (b == 0 && b2 == 0) break;
					bytes.Add(b);
					bytes.Add(b2);
				}
			} while (length <= 0 || i < length);
			if (length > 0) BaseStream.Position += (length - i);
			return encoding.GetString(bytes.ToArray());
			//string s = "";
			//int i = 0;
			//do {
			//	char c = ReadChar();
			//	i++;
			//	if (c == '\0') break;
			//	s += c;
			//} while (length <= 0 || s.Length < length);
			//if (length > 0) BaseStream.Position += (length - i);
			//return s;
		}
		public List<byte> ReadStringNullTerminatedAsBytes()
		{
			var bytes = new List<byte>();
			do {
				byte b = ReadByte();
				if (b == 0) break;
				bytes.Add(b);
			} while (true);
			return bytes;
		}
		/// <summary>
		/// Reads a string of exactly <paramref name="length"/> ASCII characters (including NULLs). If you want to omit the NULLs, use <see cref="ReadStringNullTerminated(int, Encoding, bool)"/> instead.
		/// Returns an empty string if <paramref name="length"/> is 0.
		/// </summary>
		public string ReadString(int length, Encoding encoding = null, bool trimNulls = true, bool lengthIsChars = false, bool nullOnZeroLength = false)
		{
			//return new string(ReadChars(length));
			if (length < 0) throw new ArgumentException("Cannot read string of length < 0", nameof(length));
			if (length == 0) return nullOnZeroLength ? null : "";
			encoding ??= Encoding;
			if (lengthIsChars && !encoding.IsSingleByte) length *= 2; // only useful for fixed-size encodings!
			var bytes = ReadBytes(length);
			var s = encoding.GetString(bytes);
			if (trimNulls) s = s.TrimEnd('\0');
			return s;
		}
		public string ReadStringPrefixedInt32(Encoding encoding = null, bool trimNulls = true, bool lengthIsChars = false, bool nullOnZeroLength = false)
		{
			int length = ReadInt32();
			if (length < 0) throw new InvalidOperationException("Cannot read string of length < 0");
			return ReadString(length, encoding, trimNulls, lengthIsChars, nullOnZeroLength);
		}
		public string ReadStringPrefixedInt16(Encoding encoding = null, bool trimNulls = true, bool lengthIsChars = false, bool nullOnZeroLength = false)
		{
			int length = ReadInt16();
			if (length < 0) throw new InvalidOperationException("Cannot read string of length < 0");
			return ReadString(length, encoding, trimNulls, lengthIsChars, nullOnZeroLength);
		}
		public byte ReadByte6bit()
		{
			return (byte)(Math.Min(ReadByte(), (byte)63) * 4);
		}
		public string ReadFlagAsString(int count = 1)
		{
			string s = "";
			for (int i = 0; i < count; i++) {
				var b = ReadByte();
				s += ConvTools.FlagsToString(b);
				if (i < count - 1) s += " ";
			}
			return s;
		}

		public override byte ReadByte()
		{
			var b = BaseStream.ReadByte();
			if (b == -1) throw new EndOfStreamException();
			return (byte)b;
		}

		public new int Read7BitEncodedInt()
		{
			return base.Read7BitEncodedInt();
		}

		public void ReadSpecificBytes(params byte[] bytes)
		{
			if (bytes == null) return;
			for (int i = 0; i < bytes.Length; i++) Trace.Assert(ReadByte() == bytes[i]);
		}



		#region Little Endian

		public override int ReadInt32() => InvertEndianess ? ReadInt32_BE2() : base.ReadInt32();
		public override uint ReadUInt32() => InvertEndianess ? ReadUInt32_BE2() : base.ReadUInt32();
		public override short ReadInt16() => InvertEndianess ? ReadInt16_BE2() : base.ReadInt16();
		public override ushort ReadUInt16() => InvertEndianess ? ReadUInt16_BE2() : base.ReadUInt16();
		public override long ReadInt64() => InvertEndianess ? ReadInt64_BE2() : base.ReadInt64();
		public override ulong ReadUInt64() => InvertEndianess ? ReadUInt64_BE2() : base.ReadUInt64();
		public override float ReadSingle() => InvertEndianess ? ReadSingle_BE2() : base.ReadSingle();

		#endregion

		#region Big Endian

		public int ReadInt32_BE() => InvertEndianess ? base.ReadInt32() : ReadInt32_BE2();
		public uint ReadUInt32_BE() => InvertEndianess ? base.ReadUInt32() : ReadUInt32_BE2();
		public short ReadInt16_BE() => InvertEndianess ? base.ReadInt16() : ReadInt16_BE2();
		public ushort ReadUInt16_BE() => InvertEndianess ? base.ReadUInt16() : ReadUInt16_BE2();
		public long ReadInt64_BE() => InvertEndianess ? base.ReadInt64() : ReadInt64_BE2();
		public ulong ReadUInt64_BE() => InvertEndianess ? base.ReadUInt64() : ReadUInt64_BE2();

		private int ReadInt32_BE2()
		{
			var data = base.ReadBytes(4);
			Array.Reverse(data);
			return BitConverter.ToInt32(data, 0);
		}
		private uint ReadUInt32_BE2()
		{
			var data = base.ReadBytes(4);
			Array.Reverse(data);
			return BitConverter.ToUInt32(data, 0);
		}
		private short ReadInt16_BE2()
		{
			var data = base.ReadBytes(2);
			Array.Reverse(data);
			return BitConverter.ToInt16(data, 0);
		}
		private ushort ReadUInt16_BE2()
		{
			var data = base.ReadBytes(2);
			Array.Reverse(data);
			return BitConverter.ToUInt16(data, 0);
		}
		private long ReadInt64_BE2()
		{
			var data = base.ReadBytes(8);
			Array.Reverse(data);
			return BitConverter.ToInt64(data, 0);
		}
		private ulong ReadUInt64_BE2()
		{
			var data = base.ReadBytes(8);
			Array.Reverse(data);
			return BitConverter.ToUInt64(data, 0);
		}
		private float ReadSingle_BE2()
		{
			var data = base.ReadBytes(4);
			Array.Reverse(data);
			return BitConverter.ToSingle(data, 0);
		}

		#endregion

		/// <summary>Reads a 16-byte / 128-bit GUID.</summary>
		public Guid ReadGuid()
		{
			var b = ReadBytes(16);
			return new Guid(b);
		}

		public T ReadStruct<T>(int size = -1) where T: struct
		{
			if (size < 0) size = Marshal.SizeOf(typeof(T));
			var bytes = ReadBytes(size);
			var handle = GCHandle.Alloc(bytes, GCHandleType.Pinned);
			T ret;
			try {
				ret = (T)Marshal.PtrToStructure(handle.AddrOfPinnedObject(), typeof(T));
			} finally {
				handle.Free();
			}
			return ret;
		}
		[DllImport("msvcrt.dll", EntryPoint = "memcpy", CallingConvention = CallingConvention.Cdecl, SetLastError = false)]
		extern static unsafe IntPtr memcpy(IntPtr dest, IntPtr src, uint count);
		private static unsafe T[] MarshalTArray<T>(byte[] bytes, int count)
		{
			fixed (byte* src = bytes) {
				var r = new T[count];
				var hr = GCHandle.Alloc(r, GCHandleType.Pinned);
				memcpy(hr.AddrOfPinnedObject(), new IntPtr(src), (uint)bytes.Length);
				hr.Free();
				return r;
			}
		}
		public T[] ReadTArray<T>(int length, int count) => MarshalTArray<T>(ReadBytes(length), count);
		public T[] ReadTArray<T>(int count) => MarshalTArray<T>(ReadBytes(Marshal.SizeOf(typeof(T)) * count), count);

		public byte[] ReadUntilEnd()
		{
			var ms = new MemoryStream((int)(BaseStream.Length - BaseStream.Position));
			BaseStream.CopyTo(ms);
			return ms.ToArray();
		}

		public Vector2 ReadVector2() => new(ReadSingle(), ReadSingle());
		public Vector3 ReadVector3() => new(ReadSingle(), ReadSingle(), ReadSingle());
		public Vector4 ReadVector4() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());

		public SharpDX.Vector2 ReadVector2DX() => new(ReadSingle(), ReadSingle());
		public SharpDX.Vector3 ReadVector3DX() => new(ReadSingle(), ReadSingle(), ReadSingle());
		public SharpDX.Vector4 ReadVector4DX() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
		public SharpDX.Color4 ReadColor4DX_Rgba() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
		public Rgba32 ReadRgba32() => new(ReadUInt32());
		public SharpDX.BoundingBox ReadBoundingBoxDX() => new(new SharpDX.Vector3(ReadSingle(), ReadSingle(), ReadSingle()), new SharpDX.Vector3(ReadSingle(), ReadSingle(), ReadSingle()));
		/// <summary>Reads a quaternion in order X, Y, Z, W.</summary>
		public SharpDX.Quaternion ReadQuaternionDX() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
		/// <summary>Reads a quaternion in order W, X, Y, Z.</summary>
		public SharpDX.Quaternion ReadQuaternionDX_WXYZ()
		{
			float w = ReadSingle(), x = ReadSingle(), y = ReadSingle(), z = ReadSingle();
			return new(x, y, z, w);
		}

		/// <summary>Reads a quaternion in order M11, M12, M13... If you need a column matrix, use <see cref="SharpDX.Matrix.Transpose()"/>.</summary>
		public SharpDX.Matrix ReadRowMatrix4x4DX() => new(ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle(), ReadSingle());
		public SharpDX.Matrix ReadColMatrix4x4DX()
		{
			float m11 = ReadSingle(), m21 = ReadSingle(), m31 = ReadSingle(), m41 = ReadSingle();
			float m12 = ReadSingle(), m22 = ReadSingle(), m32 = ReadSingle(), m42 = ReadSingle();
			float m13 = ReadSingle(), m23 = ReadSingle(), m33 = ReadSingle(), m43 = ReadSingle();
			float m14 = ReadSingle(), m24 = ReadSingle(), m34 = ReadSingle(), m44 = ReadSingle();
			return new(m11, m12, m13, m14, m21, m22, m23, m24, m31, m32, m33, m34, m41, m42, m43, m44);
		}

		public int Peek()
		{
			var b = BaseStream.ReadByte();
			if (b == -1) return -1;
			BaseStream.Seek(-1, SeekOrigin.Current);
			return (byte)b;
		}
	}
}
