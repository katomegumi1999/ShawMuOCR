using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace MuClip.Interop;

/// <summary>
/// 基于 Windows 操作系统内置 (winsqlite3.dll) 的轻量原生 SQLite 互操作类。
/// 无需引入任何外部第三方 NuGet 包，零依赖、零污染，原生 AOT 剪裁安全。
/// 保留该功能作为独立数据库迁移或在不调用 Rust Core 时的直接 SQLite 写入备选。
/// </summary>
internal static unsafe partial class WinSqliteInterop
{
    private const string DllName = "winsqlite3.dll";

    private const int SQLITE_OK = 0;
    private const int SQLITE_OPEN_READWRITE = 0x00000002;

    [LibraryImport(DllName, EntryPoint = "sqlite3_open_v2")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int Sqlite3OpenV2(byte* filename, out IntPtr ppDb, int flags, byte* zVfs);

    [LibraryImport(DllName, EntryPoint = "sqlite3_close_v2")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int Sqlite3CloseV2(IntPtr db);

    [LibraryImport(DllName, EntryPoint = "sqlite3_busy_timeout")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int Sqlite3BusyTimeout(IntPtr db, int ms);

    [LibraryImport(DllName, EntryPoint = "sqlite3_exec")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial int Sqlite3Exec(IntPtr db, byte* sql, IntPtr callback, IntPtr arg, out IntPtr errmsg);

    [LibraryImport(DllName, EntryPoint = "sqlite3_free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    private static partial void Sqlite3Free(IntPtr ptr);

    /// <summary>
    /// 批量按 ID 列表将顺序写回 SQLite phrases 表的 sort_order 字段。
    /// 使用原子 IMMEDIATE 事务，毫秒级快速落盘。
    /// </summary>
    public static bool UpdatePhraseSortOrders(string dbPath, IReadOnlyList<long> orderedIds)
    {
        if (string.IsNullOrWhiteSpace(dbPath) || orderedIds == null || orderedIds.Count == 0)
        {
            return false;
        }

        if (!File.Exists(dbPath))
        {
            return false;
        }

        var sb = new StringBuilder(orderedIds.Count * 48 + 32);
        sb.Append("BEGIN IMMEDIATE;\n");
        for (int i = 0; i < orderedIds.Count; i++)
        {
            sb.Append("UPDATE phrases SET sort_order = ");
            sb.Append(i);
            sb.Append(" WHERE id = ");
            sb.Append(orderedIds[i]);
            sb.Append(";\n");
        }
        sb.Append("COMMIT;");

        byte[] pathBytes = Encoding.UTF8.GetBytes(dbPath + "\0");
        byte[] sqlBytes = Encoding.UTF8.GetBytes(sb.ToString() + "\0");

        fixed (byte* pPath = pathBytes)
        fixed (byte* pSql = sqlBytes)
        {
            int openRc = Sqlite3OpenV2(pPath, out IntPtr db, SQLITE_OPEN_READWRITE, null);
            if (openRc != SQLITE_OK || db == IntPtr.Zero)
            {
                return false;
            }

            try
            {
                Sqlite3BusyTimeout(db, 3000);
                IntPtr errMsg = IntPtr.Zero;
                int execRc = Sqlite3Exec(db, pSql, IntPtr.Zero, IntPtr.Zero, out errMsg);
                if (errMsg != IntPtr.Zero)
                {
                    Sqlite3Free(errMsg);
                }
                return execRc == SQLITE_OK;
            }
            finally
            {
                Sqlite3CloseV2(db);
            }
        }
    }
}
