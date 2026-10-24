using System.Numerics;

namespace TVRemote;

/// <summary>
/// Minimal RSA utilities: import an ADB private key (PKCS#8) and sign ADB auth tokens
/// (RSASSA-PKCS1-v1_5 with SHA-1) with pure BigInteger math. The iOS runtime ships limited
/// System.Security.Cryptography support, so the math is done here explicitly.
/// </summary>
public static class RsaSha {

    static BigInteger _n, _e, _d, _p, _q, _dp, _dq, _qinv = BigInteger.Zero;
    static int _keyBytes;
    static bool _loaded;
    static readonly object LoadLock = new();

    /// <summary>Loads the RSA parameters once from the embedded PKCS#8 base64 blob.</summary>
    public static void Load(string pkcs8Base64) {
        lock (LoadLock) {
            if (_loaded) return;
            byte[] der = Convert.FromBase64String(pkcs8Base64.Trim());
            // PrivateKeyInfo ::= SEQUENCE { version, AlgorithmIdentifier, privateKey OCTET STRING }
            int pos = 0;
            byte[] _ = ReadTlvValue(der, ref pos);          // outer SEQUENCE
            byte[] version = ReadTlvValue(der, ref pos);    // INTEGER
            byte[] alg = ReadTlvValue(der, ref pos);        // AlgorithmIdentifier SEQ (skipped)
            byte[] pkcs1 = ReadTlvValue(der, ref pos);      // OCTET STRING → RSAPrivateKey DER
            // RSAPrivateKey ::= SEQUENCE { version, n, e, d, p, q, dp, dq, qinv }
            pos = 0;
            byte[] inner = ReadTlvValue(pkcs1, ref pos);    // inner SEQUENCE
            pos = 0;
            ReadTlvValue(inner, ref pos);                   // version
            _n = ReadInt(inner, ref pos);
            _e = ReadInt(inner, ref pos);
            _d = ReadInt(inner, ref pos);
            _p = ReadInt(inner, ref pos);
            _q = ReadInt(inner, ref pos);
            _dp = ReadInt(inner, ref pos);
            _dq = ReadInt(inner, ref pos);
            _qinv = ReadInt(inner, ref pos);
            _keyBytes = ((int)_n.GetBitLength() + 7) / 8;
            _loaded = true;
        }
    }

    /// <summary>Signs an ADB auth token (RSASSA-PKCS1-v1_5 / SHA-1), like the adb client does.</summary>
    public static byte[] SignToken(byte[] token) {
        if (!_loaded) throw new InvalidOperationException("Key not loaded");
        byte[] digestInfo = {
            0x30, 0x21, 0x30, 0x09, 0x06, 0x05, 0x2b, 0x0e, 0x03, 0x02, 0x1a,
            0x05, 0x00, 0x04, 0x14 };
        byte[] digest = Sha1(token);
        int tLen = digestInfo.Length + digest.Length;
        int psLen = _keyBytes - tLen - 3;
        byte[] em = new byte[_keyBytes];
        em[0] = 0x00; em[1] = 0x01;
        for (int i = 0; i < psLen; i++) em[2 + i] = 0xFF;
        em[2 + psLen] = 0x00;
        Array.Copy(digestInfo, 0, em, 3 + psLen, digestInfo.Length);
        Array.Copy(digest, 0, em, 3 + psLen + digestInfo.Length, digest.Length);

        BigInteger m = new(em.Reverse().ToArray());
        BigInteger s = Crt(m);
        return ToBytesUnsignedLE(s, _keyBytes);
    }

    /// <summary>Android adb public key blob: base64( 0x00 + DER { modulus, exponent } ) + " name\0".
    /// Used only when the TV answers a SIGNATURE with another TOKEN, i.e. the key is unknown.</summary>
    public static string PublicKeyBlob() {
        byte[] mod = ToDerInteger(ToBytesUnsignedLE(_n, _keyBytes));
        byte[] exp = ToDerInteger(ToBytesUnsignedLE(_e, ToBytesUnsignedLE(_e).Length));
        byte[] body = Der(0x02, mod).Concat(Der(0x02, exp)).ToArray();
        byte[] der = Der(0x30, body);
        byte[] blob = new byte[der.Length + 1];
        blob[0] = 0x00;
        Array.Copy(der, 0, blob, 1, der.Length);
        return Convert.ToBase64String(blob) + " android-tv-remote\x00";
    }

    static BigInteger Crt(BigInteger m) {
        BigInteger s1 = BigInteger.ModPow(m, _dp, _p);
        BigInteger s2 = BigInteger.ModPow(m, _dq, _q);
        BigInteger h = ((s2 - s1) * _qinv) % _q;
        if (h.Sign < 0) h += _q;
        return s1 + _p * h;
    }

    // ---------- ASN.1 ----------

    /// <summary>Reads one TLV at pos and returns its contents; pos advances past it.</summary>
    static byte[] ReadTlvValue(byte[] data, ref int pos) {
        pos++;                                       // tag byte (0x30 or 0x02/0x04)
        int len = data[pos++];
        if ((len & 0x80) != 0) {
            int count = len & 0x7F, real = 0;
            for (int i = 0; i < count; i++) real = (real << 8) | data[pos++];
            len = real;
        }
        byte[] value = new byte[len];
        Array.Copy(data, pos, value, 0, len);
        pos += len;
        return value;
    }

    static BigInteger ReadInt(byte[] data, ref int pos) {
        byte[] value = ReadTlvValue(data, ref pos);
        int start = 0;
        while (start < value.Length - 1 && value[start] == 0) start++;
        if ((value[start] & 0x80) != 0 && start > 0) start--;  // reattach, BigInteger is signed
        byte[] le = value[start..].Reverse().ToArray();
        return new BigInteger(le);
    }

    static byte[] ToDerInteger(byte[] be) {
        int start = 0;
        while (start < be.Length - 1 && be[start] == 0) start++;
        byte[] body = be[start..];
        if ((body[0] & 0x80) != 0) body = new byte[] { 0 }.Concat(body).ToArray();
        return Der(0x02, body);
    }

    static byte[] Der(byte tag, byte[] contents) {
        var bytes = new List<byte> { tag };
        if (contents.Length < 0x80) {
            bytes.Add((byte)contents.Length);
        } else {
            byte[] big = new List<byte>().Concat(LengthBytes(contents.Length)).ToArray();
            bytes.Add((byte)(0x80 | big.Length));
            bytes.AddRange(big);
        }
        bytes.AddRange(contents);
        return bytes.ToArray();
    }

    static byte[] LengthBytes(int v) {
        var stack = new Stack<byte>();
        while (v > 0) { stack.Push((byte)(v & 0xFF)); v >>= 8; }
        return stack.ToArray();
    }

    static byte[] ToBytesUnsignedLE(BigInteger value) => value.ToByteArray();

    static byte[] ToBytesUnsignedLE(BigInteger value, int length) {
        byte[] raw = value.ToByteArray();
        byte[] result = new byte[length];
        Array.Copy(raw, result, Math.Min(raw.Length, length));
        return result;
    }

    // ---------- SHA-1 (FIPS 180-1) ----------

    public static byte[] Sha1(byte[] data) {
        uint h0 = 0x67452301, h1 = 0xEFCDAB89, h2 = 0x98BADCFE, h3 = 0x10325476, h4 = 0xC3D2E1F0;
        long bitLen = (long)data.Length * 8;
        var msg = new List<byte>(data) { 0x80 };
        while (msg.Count % 64 != 56) msg.Add(0);
        for (int i = 7; i >= 0; i--) msg.Add((byte)((bitLen >> (8 * i)) & 0xFF));

        var w = new uint[80];
        for (int off = 0; off < msg.Count; off += 64) {
            for (int i = 0; i < 16; i++) {
                w[i] = ((uint)msg[off + 4 * i] << 24) | ((uint)msg[off + 4 * i + 1] << 16)
                     | ((uint)msg[off + 4 * i + 2] << 8) | msg[off + 4 * i + 3];
            }
            for (int i = 16; i < 80; i++) {
                uint x = w[i - 3] ^ w[i - 8] ^ w[i - 14] ^ w[i - 16];
                w[i] = (x << 1) | (x >> 31);
            }
            uint a = h0, b = h1, c = h2, d = h3, e = h4;
            for (int i = 0; i < 80; i++) {
                uint f, k;
                if (i < 20) { f = (b & c) | (~b & d); k = 0x5A827999; }
                else if (i < 40) { f = b ^ c ^ d; k = 0x6ED9EBA1; }
                else if (i < 60) { f = (b & c) | (b & d) | (c & d); k = 0x8F1BBCDC; }
                else { f = b ^ c ^ d; k = 0xCA62C1D6; }
                uint t = ((a << 5) | (a >> 27)) + f + e + k + w[i];
                e = d; d = c;
                c = (b << 30) | (b >> 2);
                b = a; a = t;
            }
            h0 += a; h1 += b; h2 += c; h3 += d; h4 += e;
        }
        var result = new byte[20];
        Put(h0, result, 0); Put(h1, result, 4); Put(h2, result, 8); Put(h3, result, 12); Put(h4, result, 16);
        return result;
        static void Put(uint v, byte[] dst, int off) {
            dst[off] = (byte)(v >> 24); dst[off + 1] = (byte)(v >> 16);
            dst[off + 2] = (byte)(v >> 8); dst[off + 3] = (byte)v;
        }
    }
}
