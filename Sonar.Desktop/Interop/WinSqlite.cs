using System.IO;
using System.Runtime.InteropServices;

namespace Sonar.Interop;

/// <summary>
/// 通过 Windows 自带的 winsqlite3.dll 读取浏览器 Cookie 数据库，
/// 避免引入 Microsoft.Data.Sqlite / System.Data.SQLite 等额外依赖。
/// </summary>
internal static class WinSqlite
{
    private const string Dll = "winsqlite3.dll";
    public const int Ok = 0;
    public const int Row = 100;
    public const int Done = 101;

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_open_v2(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string filename,
        out IntPtr database, int flags, IntPtr vfs);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_close_v2(IntPtr database);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_prepare_v2(
        IntPtr database, [MarshalAs(UnmanagedType.LPUTF8Str)] string sql,
        int byteCount, out IntPtr statement, IntPtr tail);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_step(IntPtr statement);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_finalize(IntPtr statement);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_bytes(IntPtr statement, int column);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int sqlite3_column_type(IntPtr statement, int column);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr sqlite3_errmsg(IntPtr database);

    public sealed class Database : IDisposable
    {
        private IntPtr _handle;

        private Database(IntPtr handle) => _handle = handle;

        public static Database OpenReadOnly(string path)
        {
            var result = sqlite3_open_v2(path, out var handle, 1 /* SQLITE_OPEN_READONLY */, IntPtr.Zero);
            if (result != Ok || handle == IntPtr.Zero)
            {
                if (handle != IntPtr.Zero)
                {
                    var message = Marshal.PtrToStringUTF8(sqlite3_errmsg(handle)) ?? "打开失败";
                    sqlite3_close_v2(handle);
                    throw new IOException(message);
                }
                throw new IOException($"sqlite3_open_v2 失败（code={result}）");
            }
            return new Database(handle);
        }

        public Statement Prepare(string sql)
        {
            var result = sqlite3_prepare_v2(_handle, sql, -1, out var statement, IntPtr.Zero);
            if (result != Ok || statement == IntPtr.Zero)
            {
                var message = Marshal.PtrToStringUTF8(sqlite3_errmsg(_handle)) ?? "prepare 失败";
                throw new IOException(message);
            }
            return new Statement(statement);
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            sqlite3_close_v2(_handle);
            _handle = IntPtr.Zero;
        }
    }

    public sealed class Statement : IDisposable
    {
        private IntPtr _handle;

        internal Statement(IntPtr handle) => _handle = handle;

        public bool Step()
        {
            var result = sqlite3_step(_handle);
            if (result == Row) return true;
            if (result == Done) return false;
            throw new IOException($"sqlite3_step 返回 {result}");
        }

        public bool IsNull(int column) => sqlite3_column_type(_handle, column) == 5;

        public string ReadText(int column)
        {
            var pointer = sqlite3_column_text(_handle, column);
            if (pointer == IntPtr.Zero) return "";
            var length = sqlite3_column_bytes(_handle, column);
            return Marshal.PtrToStringUTF8(pointer, length) ?? "";
        }

        public byte[]? ReadBlob(int column)
        {
            var pointer = sqlite3_column_blob(_handle, column);
            if (pointer == IntPtr.Zero) return null;
            var length = sqlite3_column_bytes(_handle, column);
            if (length <= 0) return Array.Empty<byte>();
            var buffer = new byte[length];
            Marshal.Copy(pointer, buffer, 0, length);
            return buffer;
        }

        public void Dispose()
        {
            if (_handle == IntPtr.Zero) return;
            sqlite3_finalize(_handle);
            _handle = IntPtr.Zero;
        }
    }
}
