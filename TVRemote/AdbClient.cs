using System.Net.Sockets;
using System.Text;

namespace TVRemote;

/// <summary>
/// A speaks-the-protocol ADB client over the TV's TCP port 5555 (same wire protocol as USB adb):
/// CNXN handshake with RSA SHA-1 token signing, then one at a time: "shell" (text), "exec"
/// (binary-safe output) and "sync:" pushes. The TV address is hard-wired to this launcher's TV.
/// </summary>
public sealed class AdbClient : IAsyncDisposable {

    public const string TvHost = "192.168.1.232";
    public const int TvPort = 5555;

    const uint A_CNXN = 0x4e584e43, A_OPEN = 0x4e45504f, A_OKAY = 0x59414b4f,
               A_CLSE = 0x45534c43, A_WRTE = 0x45545257, A_AUTH = 0x48545541;
    const uint S_OKAY = 0x59414b4f, S_FAIL = 0x4c494146, S_SEND = 0x444e4553,
               S_DATA = 0x41544144, S_DONE = 0x454e4f44;
    const int SYNC_MODE = 0x01a4;   // 0100664 octal, matches adb's pushed-file mode

    TcpClient? _tcp;
    NetworkStream _s = null!;
    uint _maxPayload = 4096;
    uint _remoteId, _localId = 0x0aa1;
    readonly SemaphoreSlim _lock = new(1, 1);
    public bool Connected { get; private set; }

    /// <summary>Opens the TCP connection and plays the adb handshake (token auth).</summary>
    public async Task EnsureConnectedAsync() {
        if (Connected) return;
        _tcp?.Dispose();
        _tcp = new TcpClient();
        await _tcp.ConnectAsync(TvHost, TvPort).ConfigureAwait(false);
        _s = _tcp.GetStream();
        Send(A_CNXN, 0x01000000, 4096, Encoding.UTF8.GetBytes("host::features=shell_v2,hw"));

        for (int round = 0; round < 8; round++) {
            (uint cmd, uint a0, uint a1, byte[] data) m = await ReadMessageAsync().ConfigureAwait(false);
            if (m.cmd == A_CNXN) {
                _maxPayload = m.a1 == 0 ? 4096 : Math.Min(m.a1, 262144);
                Connected = true;
                return;
            }
            if (m.cmd == A_AUTH && m.a0 == 1) {                       // TOKEN → sign it
                Send(A_AUTH, 2, 0, RsaSha.SignToken(m.data));
                continue;
            }
            if (m.cmd == A_AUTH) {                                    // key unknown → hand it over
                byte[] pub = Encoding.UTF8.GetBytes(RsaSha.PublicKeyBlob());
                Send(A_AUTH, 3, 0, pub);
                continue;
            }
            throw new IOException($"Unexpected handshake 0x{m.cmd:x}");
        }
        throw new IOException("Handshake loops did not settle");
    }

    public async ValueTask DisposeAsync() {
        Connected = false;
        try { await _lock.WaitAsync(50).ConfigureAwait(false); } catch { }
        _tcp?.Dispose();
        _lock.Release();
        _lock.Dispose();
    }

    /// <summary>shell:<cmd> — line-oriented text output (CRLF folded to LF).</summary>
    public async Task<string> ShellAsync(string cmd) {
        await EnsureConnectedAsync().ConfigureAwait(false);
        await _lock.WaitAsync().ConfigureAwait(false);
        try {
            uint id = _localId;
            await OpenAsync("shell:" + cmd).ConfigureAwait(false);
            string text = await ReadStreamAsync(id).ConfigureAwait(false);
            return text.Replace("\r\n", "\n").Trim();
        } catch {
            Connected = false;
            throw;
        } finally { _lock.Release(); }
    }

    /// <summary>exec:<cmd> — binary-safe output (no CRLF rewriting), for screencap etc.</summary>
    public async Task<byte[]> ExecAsync(string cmd) {
        await EnsureConnectedAsync().ConfigureAwait(false);
        await _lock.WaitAsync().ConfigureAwait(false);
        try {
            uint id = _localId;
            await OpenAsync("exec:" + cmd).ConfigureAwait(false);
            var buffer = new MemoryStream();
            while (true) {
                (uint cmd, uint a0, uint a1, byte[] data) m = await ReadMessageAsync().ConfigureAwait(false);
                if (m.cmd == A_WRTE) { Send(A_OKAY, id, m.a0, Array.Empty<byte>()); await buffer.WriteAsync(m.data).ConfigureAwait(false); }
                else if (m.cmd == A_CLSE || m.cmd == A_OKAY) break;
            }
            ConnectedTrack();
            return buffer.ToArray();
        } catch {
            Connected = false;
            throw;
        } finally { _lock.Release(); }
    }

    /// <summary>sync:<path> push of one file (adb's wire protocol, block-by-block).</summary>
    public async Task PushAsync(byte[] file, string remotePath) {
        await EnsureConnectedAsync().ConfigureAwait(false);
        await _lock.WaitAsync().ConfigureAwait(false);
        try {
            uint id = _localId;
            await OpenAsync("sync:").ConfigureAwait(false);
            byte[] path = Encoding.UTF8.GetBytes(remotePath);
            var send = new MemoryStream();
            send.Write(path); send.WriteByte(0);
            send.Write(ToLE(SYNC_MODE));
            Send(A_WRTE, id, _remoteId, send.ToArray());

            int chunk = 64 * 1024;
            for (int off = 0; off < file.Length; off += chunk) {
                int take = Math.Min(chunk, file.Length - off);
                var data = new MemoryStream();
                data.Write(ToLE(S_DATA)); data.Write(ToLE((uint)take));
                data.Write(file, off, take);
                SendChunks(A_WRTE, id, _remoteId, data.ToArray());
            }
            var done = new MemoryStream();
            done.Write(ToLE(S_DONE)); done.Write(ToLE((uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds()));
            Send(A_WRTE, id, _remoteId, done.ToArray());

            string status = await ReadSyncStatusAsync().ConfigureAwait(false);
            Send(A_CLSE, id, _remoteId, Array.Empty<byte>());
            if (status != null) throw new IOException("push: " + status);
            ConnectedTrack();
        } catch {
            Connected = false;
            throw;
        } finally { _lock.Release(); }
    }

    // ---------- plumbing ----------

    void ConnectedTrack() { /* connection stays alive between commands */ }

    async Task OpenAsync(string service) {
        byte[] payload = Encoding.UTF8.GetBytes(service + "\0");
        Send(A_OPEN, _localId, 0, payload);
        while (true) {
            (uint cmd, uint a0, uint a1, byte[] _) m = await ReadMessageAsync().ConfigureAwait(false);
            if (m.cmd == A_OKAY && m.a1 == _localId) { _remoteId = m.a0; return; }
            if (m.cmd == A_CLSE) throw new IOException("adb refused open of " + service);
        }
    }

    async Task<string> ReadStreamAsync(uint id) {
        var buffer = new MemoryStream();
        while (true) {
            (uint cmd, uint a0, uint a1, byte[] data) m = await ReadMessageAsync().ConfigureAwait(false);
            if (m.cmd == A_WRTE && m.a1 == id) {
                Send(A_OKAY, id, m.a0, Array.Empty<byte>());
                await buffer.WriteAsync(m.data).ConfigureAwait(false);
            } else if (m.cmd == A_CLSE) {
                break;
            }
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Sync file status: OKAY or FAIL+message, as one adbd WRTE.</summary>
    async Task<string?> ReadSyncStatusAsync() {
        (uint cmd, uint a0, uint a1, byte[] data) m = await ReadMessageAsync().ConfigureAwait(false);
        if (m.cmd != A_WRTE || m.data.Length < 4) return null;
        uint s = ToUInt32(m.data, 0);
        if (s == S_OKAY) {
            Send(A_OKAY, _localId, _remoteId, Array.Empty<byte>());
            return null;
        }
        if (s == S_FAIL && m.data.Length >= 8) {
            int len = (int)ToUInt32(m.data, 4);
            int take = Math.Min(len, m.data.Length - 8);
            return Encoding.UTF8.GetString(m.data, 8, take);
        }
        return null;
    }

    /// <summary>ADB frames larger than the negotiated payload go out in cuts.</summary>
    void SendChunks(uint cmd, uint a0, uint a1, byte[] data) {
        for (int off = 0; off < data.Length; off += (int)_maxPayload) {
            int take = Math.Min((int)_maxPayload, data.Length - off);
            byte[] piece = new byte[take];
            Array.Copy(data, off, piece, 0, take);
            Send(cmd, a0, a1, piece);
        }
    }

    void Send(uint cmd, uint a0, uint a1, byte[] data) {
        var header = new byte[24];
        Put(cmd, header, 0); Put(a0, header, 4); Put(a1, header, 8);
        Put((uint)data.Length, header, 12); Put(Crc(data), header, 16); Put(cmd ^ 0xffffffffu, header, 20);
        _s.Write(header, 0, 24);
        if (data.Length > 0) _s.Write(data, 0, data.Length);
        _s.Flush();
    }

    async Task<(uint cmd, uint a0, uint a1, byte[] data)> ReadMessageAsync() {
        byte[] header = await ReadExactlyAsync(24).ConfigureAwait(false);
        uint cmd = ToUInt32(header, 0), a0 = ToUInt32(header, 4), a1 = ToUInt32(header, 8), len = ToUInt32(header, 12);
        if (len > 1 << 20) throw new IOException($"adb sent a stream too big: {len}");
        byte[] data = len > 0 ? await ReadExactlyAsync((int)len).ConfigureAwait(false) : Array.Empty<byte>();
        return (cmd, a0, a1, data);
    }

    async Task<byte[]> ReadExactlyAsync(int count) {
        byte[] buffer = new byte[count];
        int read = 0;
        while (read < count) {
            int n = await _s.ReadAsync(buffer, read, count - read).ConfigureAwait(false);
            if (n <= 0) throw new IOException("adb: connection dropped while reading");
            read += n;
        }
        return buffer;
    }

    static uint Crc(byte[] data) {
        uint sum = 0;
        for (int i = 0; i < data.Length; i++) sum += data[i];
        return sum & 0xffff;
    }

    static uint ToUInt32(byte[] b, int off) => b[off] | (uint)(b[off + 1] << 8) | (uint)(b[off + 2] << 16) | (uint)(b[off + 3] << 24);

    static byte[] ToLE(uint v) => BitConverter.GetBytes(v);

    static void Put(uint v, byte[] dst, int off) {
        dst[off] = (byte)v; dst[off + 1] = (byte)(v >> 8);
        dst[off + 2] = (byte)(v >> 16); dst[off + 3] = (byte)(v >> 24);
    }
}
