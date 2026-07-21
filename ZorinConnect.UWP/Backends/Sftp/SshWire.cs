using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Org.BouncyCastle.Math;

namespace ZorinConnect.Backends.Sftp
{
    /// <summary>SSH binary wire helpers (RFC 4251 data types) — reader + writer over byte buffers.</summary>
    internal sealed class SshWriter
    {
        private readonly MemoryStream _ms = new MemoryStream();

        public void Byte(byte b) => _ms.WriteByte(b);
        public void Bytes(byte[] b) => _ms.Write(b, 0, b.Length);
        public void Bytes(byte[] b, int off, int len) => _ms.Write(b, off, len);
        public void Bool(bool v) => _ms.WriteByte((byte)(v ? 1 : 0));

        public void UInt32(uint v)
        {
            _ms.WriteByte((byte)(v >> 24)); _ms.WriteByte((byte)(v >> 16));
            _ms.WriteByte((byte)(v >> 8)); _ms.WriteByte((byte)v);
        }

        public void UInt64(ulong v)
        {
            for (int i = 7; i >= 0; i--) _ms.WriteByte((byte)(v >> (i * 8)));
        }

        public void String(byte[] s) { UInt32((uint)s.Length); Bytes(s); }
        public void String(string s) => String(Encoding.UTF8.GetBytes(s));

        public void MPInt(BigInteger v)
        {
            var bytes = v.ToByteArray(); // signed, big-endian; BC gives minimal two's-complement
            String(bytes);
        }

        public byte[] ToArray() => _ms.ToArray();
    }

    internal sealed class SshReader
    {
        private readonly byte[] _buf;
        private int _pos;

        public SshReader(byte[] buf, int pos = 0) { _buf = buf; _pos = pos; }
        public int Position => _pos;
        public int Remaining => _buf.Length - _pos;

        public byte Byte() => _buf[_pos++];
        public bool Bool() => _buf[_pos++] != 0;

        public uint UInt32()
        {
            uint v = (uint)((_buf[_pos] << 24) | (_buf[_pos + 1] << 16) | (_buf[_pos + 2] << 8) | _buf[_pos + 3]);
            _pos += 4; return v;
        }

        public ulong UInt64()
        {
            ulong v = 0;
            for (int i = 0; i < 8; i++) v = (v << 8) | _buf[_pos++];
            return v;
        }

        public byte[] String()
        {
            int len = (int)UInt32();
            var s = new byte[len];
            Array.Copy(_buf, _pos, s, 0, len);
            _pos += len;
            return s;
        }

        public string Utf8String() => Encoding.UTF8.GetString(String());

        public BigInteger MPInt() => new BigInteger(String());

        public List<string> NameList()
        {
            var s = Encoding.ASCII.GetString(String());
            return string.IsNullOrEmpty(s) ? new List<string>() : new List<string>(s.Split(','));
        }
    }
}
