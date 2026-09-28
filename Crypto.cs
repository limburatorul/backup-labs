using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Backup;

// Encrypted archives (.bkl): a zip written through AES-256-GCM in 1 MiB chunks. Each chunk has its own
// nonce (random base nonce XOR chunk number) and tag, and the chunk number plus a "last chunk" flag are
// authenticated, so reordering, truncation and tampering all fail to decrypt. The key comes from the
// password through PBKDF2-SHA256 with a random salt per archive.
//   header: "BKLENC1\0" | salt (16) | iterations (int32 LE) | base nonce (12)
//   body:   chunk ciphertext + tag (16), repeated; the last chunk may be empty
public static class Crypto
{
    static readonly byte[] Magic = "BKLENC1\0"u8.ToArray();
    internal const int ChunkSize = 1 << 20, TagSize = 16, NonceSize = 12, SaltSize = 16, HeaderSize = 8 + SaltSize + 4 + NonceSize;
    internal const int Iterations = 600_000; // OWASP's figure for PBKDF2-SHA256

    internal static byte[] Key(string password, ReadOnlySpan<byte> salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, iterations, HashAlgorithmName.SHA256, 32);

    internal static byte[] Nonce(byte[] baseNonce, long chunk)
    {
        var n = (byte[])baseNonce.Clone();
        for (int k = 0; k < 8; k++) n[NonceSize - 1 - k] ^= (byte)(chunk >> (8 * k));
        return n;
    }

    internal static byte[] Aad(long chunk, bool last)
    {
        var a = new byte[9];
        BinaryPrimitives.WriteInt64LittleEndian(a, chunk);
        a[8] = last ? (byte)1 : (byte)0;
        return a;
    }

    internal static byte[] Header(byte[] salt, int iterations, byte[] baseNonce)
    {
        var h = new byte[HeaderSize];
        Magic.CopyTo(h, 0);
        salt.CopyTo(h, 8);
        BinaryPrimitives.WriteInt32LittleEndian(h.AsSpan(8 + SaltSize), iterations);
        baseNonce.CopyTo(h, 8 + SaltSize + 4);
        return h;
    }

    internal static (byte[] Salt, int Iterations, byte[] Nonce) ReadHeader(ReadOnlySpan<byte> h)
    {
        if (!h[..8].SequenceEqual(Magic)) throw new InvalidDataException("Not a Backup Labs encrypted archive.");
        var iterations = BinaryPrimitives.ReadInt32LittleEndian(h[(8 + SaltSize)..]);
        if (iterations is < 100_000 or > 10_000_000) throw new InvalidDataException("Damaged encrypted archive header.");
        return (h.Slice(8, SaltSize).ToArray(), iterations, h.Slice(8 + SaltSize + 4, NonceSize).ToArray());
    }

    // ---- the saved password, so scheduled backups can encrypt without asking: DPAPI, current user ----

    [StructLayout(LayoutKind.Sequential)] struct Blob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    static extern bool CryptUnprotectData(ref Blob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, int flags, out Blob output);
    [DllImport("kernel32.dll")] static extern IntPtr LocalFree(IntPtr p);
    const int UiForbidden = 1;

    static byte[] Dpapi(byte[] data, bool protect)
    {
        var pin = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            var input = new Blob { Size = data.Length, Data = pin.AddrOfPinnedObject() };
            Blob output;
            var ok = protect
                ? CryptProtectData(ref input, "Backup Labs", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, UiForbidden, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            try
            {
                var result = new byte[output.Size];
                Marshal.Copy(output.Data, result, 0, output.Size);
                return result;
            }
            finally { LocalFree(output.Data); }
        }
        finally { pin.Free(); }
    }

    public static string Protect(string password) => Convert.ToBase64String(Dpapi(Encoding.UTF8.GetBytes(password), true));
    public static string Unprotect(string saved) => Encoding.UTF8.GetString(Dpapi(Convert.FromBase64String(saved), false));
}

/// Write-only: everything written is encrypted into `inner`, which is closed with this stream.
public sealed class EncryptStream : Stream
{
    readonly Stream inner;
    readonly AesGcm aes;
    readonly byte[] baseNonce = RandomNumberGenerator.GetBytes(Crypto.NonceSize);
    readonly byte[] buffer = new byte[Crypto.ChunkSize];
    int fill;
    long chunk;
    bool closed;

    public EncryptStream(Stream inner, string password, int iterations = Crypto.Iterations)
    {
        this.inner = inner;
        var salt = RandomNumberGenerator.GetBytes(Crypto.SaltSize);
        aes = new AesGcm(Crypto.Key(password, salt, iterations), Crypto.TagSize);
        inner.Write(Crypto.Header(salt, iterations, baseNonce));
    }

    public override void Write(byte[] data, int offset, int count) => Write(data.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            if (fill == buffer.Length) Seal(last: false); // only once more data proves this chunk isn't the last
            var n = Math.Min(data.Length, buffer.Length - fill);
            data[..n].CopyTo(buffer.AsSpan(fill));
            fill += n;
            data = data[n..];
        }
    }

    void Seal(bool last)
    {
        var sealedChunk = new byte[fill + Crypto.TagSize];
        aes.Encrypt(Crypto.Nonce(baseNonce, chunk), buffer.AsSpan(0, fill), sealedChunk.AsSpan(0, fill),
                    sealedChunk.AsSpan(fill), Crypto.Aad(chunk, last));
        inner.Write(sealedChunk);
        chunk++;
        fill = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !closed)
        {
            closed = true;
            Seal(last: true);
            inner.Dispose();
            aes.Dispose();
        }
        base.Dispose(disposing);
    }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override void Flush() { } // a partial chunk can't be sealed early
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}

/// Read-only and seekable (a zip is read from its end), decrypting one chunk at a time.
public sealed class DecryptStream : Stream
{
    readonly Stream inner;
    readonly AesGcm aes;
    readonly byte[] baseNonce;
    readonly long chunks, body, length;
    readonly byte[] plain = new byte[Crypto.ChunkSize], cipher = new byte[Crypto.ChunkSize + Crypto.TagSize];
    long loaded = -1;
    int loadedLength;
    long position;

    public DecryptStream(Stream inner, string password)
    {
        this.inner = inner;
        var header = new byte[Crypto.HeaderSize];
        inner.ReadExactly(header);
        var (salt, iterations, nonce) = Crypto.ReadHeader(header);
        baseNonce = nonce;
        aes = new AesGcm(Crypto.Key(password, salt, iterations), Crypto.TagSize);
        body = inner.Length - Crypto.HeaderSize;
        chunks = Math.Max(1, (body + Crypto.ChunkSize + Crypto.TagSize - 1) / (Crypto.ChunkSize + Crypto.TagSize));
        length = body - chunks * Crypto.TagSize;
        if (length < 0) throw new InvalidDataException("The encrypted backup is truncated.");
        try { Load(0); }
        catch (InvalidDataException) { Dispose(); throw new InvalidOperationException("Wrong password for this encrypted backup."); }
    }

    void Load(long index)
    {
        var size = (int)Math.Min(Crypto.ChunkSize + Crypto.TagSize, body - index * (Crypto.ChunkSize + Crypto.TagSize));
        inner.Position = Crypto.HeaderSize + index * (Crypto.ChunkSize + Crypto.TagSize);
        inner.ReadExactly(cipher, 0, size);
        var n = size - Crypto.TagSize;
        try
        {
            aes.Decrypt(Crypto.Nonce(baseNonce, index), cipher.AsSpan(0, n), cipher.AsSpan(n, Crypto.TagSize),
                        plain.AsSpan(0, n), Crypto.Aad(index, index == chunks - 1));
        }
        catch (AuthenticationTagMismatchException) { throw new InvalidDataException("The encrypted backup is damaged or was altered."); }
        loaded = index;
        loadedLength = n;
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> destination)
    {
        if (position >= length || destination.Length == 0) return 0;
        var index = position / Crypto.ChunkSize;
        if (index != loaded) Load(index);
        var at = (int)(position - index * Crypto.ChunkSize);
        var n = Math.Min(destination.Length, loadedLength - at);
        plain.AsSpan(at, n).CopyTo(destination);
        position += n;
        return n;
    }

    public override long Seek(long offset, SeekOrigin origin) => position = origin switch
    {
        SeekOrigin.Begin => offset,
        SeekOrigin.Current => position + offset,
        _ => length + offset,
    };

    protected override void Dispose(bool disposing)
    {
        if (disposing) { inner.Dispose(); aes.Dispose(); }
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => length;
    public override long Position { get => position; set => position = value; }
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
