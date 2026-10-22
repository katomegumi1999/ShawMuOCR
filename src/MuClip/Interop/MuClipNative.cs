using System.Runtime.InteropServices;

namespace MuClip.Interop;

internal static unsafe partial class MuClipNative
{
    public const string DllName = "muclip_core.dll";

    // -------------------------------------------------------------------------
    // Manual Leased Memory Pool FFI (手动租借内存池核心接口)
    // -------------------------------------------------------------------------

    [LibraryImport(DllName, EntryPoint = "muclip_pool_acquire")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial ulong PoolAcquire(nuint minCapacity, out IntPtr outPtr, out nuint outCap);

    [LibraryImport(DllName, EntryPoint = "muclip_pool_release")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int PoolRelease(ulong leaseId);

    [LibraryImport(DllName, EntryPoint = "muclip_pool_clear")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial void PoolClear();

    // -------------------------------------------------------------------------
    // MuClip Core Engine & Pooled Data Queries
    // -------------------------------------------------------------------------

    [LibraryImport(DllName, EntryPoint = "muclip_core_new")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial IntPtr CoreNew(byte* dbPath);

    [LibraryImport(DllName, EntryPoint = "muclip_core_new_with_key")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial IntPtr CoreNewWithKey(byte* dbPath, byte* keyHex);

    [LibraryImport(DllName, EntryPoint = "muclip_core_new_in_memory")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial IntPtr CoreNewInMemory();

    [LibraryImport(DllName, EntryPoint = "muclip_core_free")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial void CoreFree(IntPtr handle);

    [LibraryImport(DllName, EntryPoint = "muclip_core_add_history")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial long CoreAddHistory(IntPtr handle, byte* content);

    [LibraryImport(DllName, EntryPoint = "muclip_core_add_history_buffer")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial long CoreAddHistoryBuffer(IntPtr handle, byte* dataPtr, nuint dataLen);

    [LibraryImport(DllName, EntryPoint = "muclip_core_get_cached_items_pooled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial ulong CoreGetCachedItemsPooled(IntPtr handle, out IntPtr outPtr, out nuint outLen);

    [LibraryImport(DllName, EntryPoint = "muclip_core_query_history_pooled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial ulong CoreQueryHistoryPooled(IntPtr handle, nuint limit, out IntPtr outPtr, out nuint outLen);

    [LibraryImport(DllName, EntryPoint = "muclip_core_query_history_page_pooled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial ulong CoreQueryHistoryPagePooled(IntPtr handle, nuint offset, nuint limit, out IntPtr outPtr, out nuint outLen);

    [LibraryImport(DllName, EntryPoint = "muclip_core_count_history")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial long CoreCountHistory(IntPtr handle);

    [LibraryImport(DllName, EntryPoint = "muclip_core_search_history_pooled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial ulong CoreSearchHistoryPooled(IntPtr handle, byte* query, nuint limit, out IntPtr outPtr, out nuint outLen);

    [LibraryImport(DllName, EntryPoint = "muclip_core_delete_history")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int CoreDeleteHistory(IntPtr handle, long id);

    [LibraryImport(DllName, EntryPoint = "muclip_core_clear_history")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int CoreClearHistory(IntPtr handle);

    [LibraryImport(DllName, EntryPoint = "muclip_core_analyze_content_pooled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial ulong CoreAnalyzeContentPooled(byte* text, out IntPtr outPtr, out nuint outLen);

    [LibraryImport(DllName, EntryPoint = "muclip_core_query_phrases_pooled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial ulong CoreQueryPhrasesPooled(IntPtr handle, out IntPtr outPtr, out nuint outLen);

    [LibraryImport(DllName, EntryPoint = "muclip_core_search_phrases_pooled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial ulong CoreSearchPhrasesPooled(IntPtr handle, byte* query, out IntPtr outPtr, out nuint outLen);

    [LibraryImport(DllName, EntryPoint = "muclip_core_add_phrase")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial long CoreAddPhrase(IntPtr handle, byte* content);

    [LibraryImport(DllName, EntryPoint = "muclip_core_edit_phrase")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int CoreEditPhrase(IntPtr handle, long id, byte* newContent);

    [LibraryImport(DllName, EntryPoint = "muclip_core_delete_phrase")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int CoreDeletePhrase(IntPtr handle, long id);

    [LibraryImport(DllName, EntryPoint = "muclip_core_update_phrase_used")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int CoreUpdatePhraseUsed(IntPtr handle, long id);

    [LibraryImport(DllName, EntryPoint = "muclip_core_reorder_phrases")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int CoreReorderPhrases(IntPtr handle, long* ids, nuint count);

    [LibraryImport(DllName, EntryPoint = "muclip_core_set_config")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int CoreSetConfig(IntPtr handle, byte* key, byte* val);

    [LibraryImport(DllName, EntryPoint = "muclip_core_get_config")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial IntPtr CoreGetConfig(IntPtr handle, byte* key);

    [LibraryImport(DllName, EntryPoint = "muclip_core_add_phrase_rule")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial IntPtr CoreAddPhraseRule(IntPtr handle, byte* ruleJson);

    [LibraryImport(DllName, EntryPoint = "muclip_core_delete_phrase_rule")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int CoreDeletePhraseRule(IntPtr handle, byte* id);

    [LibraryImport(DllName, EntryPoint = "muclip_core_query_phrase_rules")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial IntPtr CoreQueryPhraseRules(IntPtr handle, long phraseId);

    [LibraryImport(DllName, EntryPoint = "muclip_core_save_template")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial IntPtr CoreSaveTemplate(IntPtr handle, byte* templateJson);

    [LibraryImport(DllName, EntryPoint = "muclip_core_delete_template")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int CoreDeleteTemplate(IntPtr handle, byte* id);

    [LibraryImport(DllName, EntryPoint = "muclip_core_query_templates")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial IntPtr CoreQueryTemplates(IntPtr handle);

    [LibraryImport(DllName, EntryPoint = "muclip_core_export_db")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial IntPtr CoreExportDb(IntPtr handle, byte* exportPath, byte* password);

    [LibraryImport(DllName, EntryPoint = "muclip_core_import_db")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial IntPtr CoreImportDb(IntPtr handle, byte* importPath, byte* password, int replaceAll);

    // -------------------------------------------------------------------------
    // MuClip P2P Sync Engine
    // -------------------------------------------------------------------------

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void NativeClipboardCallback(
        IntPtr content,
        IntPtr contentType,
        IntPtr deviceId,
        IntPtr deviceName,
        IntPtr userData
    );

    [LibraryImport(DllName, EntryPoint = "muclip_sync_start")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial IntPtr SyncStart(
        byte* deviceId,
        byte* deviceName,
        byte* deviceType,
        ushort syncPort,
        ushort discoveryPort,
        byte* pairingPin
    );

    [LibraryImport(DllName, EntryPoint = "muclip_sync_stop")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial void SyncStop(IntPtr handle);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_get_bound_port")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial ushort SyncGetBoundPort(IntPtr handle);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_get_active_peers_pooled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial ulong SyncGetActivePeersPooled(IntPtr handle, out IntPtr outPtr, out nuint outLen);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_get_all_peers_pooled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial ulong SyncGetAllPeersPooled(IntPtr handle, out IntPtr outPtr, out nuint outLen);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_broadcast")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial nuint SyncBroadcast(IntPtr handle, byte* text, byte* contentType);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_set_callback")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial void SyncSetCallback(IntPtr handle, IntPtr callbackPtr, IntPtr userData);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_set_pairing_pin")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int SyncSetPairingPin(IntPtr handle, byte* pin);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_add_manual_peer")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int SyncAddManualPeer(IntPtr handle, byte* addr);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_remove_manual_peer")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int SyncRemoveManualPeer(IntPtr handle, byte* addr);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_get_manual_peers_pooled")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial ulong SyncGetManualPeersPooled(IntPtr handle, out IntPtr outPtr, out nuint outLen);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_set_auto_discovery")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int SyncSetAutoDiscovery(IntPtr handle, int enabled);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_is_auto_discovery")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int SyncIsAutoDiscovery(IntPtr handle);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_trigger_scan")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int SyncTriggerScan(IntPtr handle);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_test_peer")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int SyncTestPeer(IntPtr handle, byte* peerAddr);

    [StructLayout(LayoutKind.Sequential)]
    public unsafe struct MuClipPairResult
    {
        public int Success; // 0=ok, 1=rejected, 2=wrong pin, 3=timeout, -1=network/internal error
        public fixed byte ResponderDeviceId[128];
        public fixed byte ResponderDeviceName[128];
        public fixed byte ErrorMessage[256];
        public fixed byte LongTermKey[65];

        public string GetResponderDeviceId()
        {
            fixed (byte* p = ResponderDeviceId)
            {
                return Marshal.PtrToStringUTF8((IntPtr)p) ?? string.Empty;
            }
        }

        public string GetResponderDeviceName()
        {
            fixed (byte* p = ResponderDeviceName)
            {
                return Marshal.PtrToStringUTF8((IntPtr)p) ?? string.Empty;
            }
        }

        public string GetErrorMessage()
        {
            fixed (byte* p = ErrorMessage)
            {
                return Marshal.PtrToStringUTF8((IntPtr)p) ?? string.Empty;
            }
        }

        public string GetLongTermKey()
        {
            fixed (byte* p = LongTermKey)
            {
                return Marshal.PtrToStringUTF8((IntPtr)p) ?? string.Empty;
            }
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void NativePairRequestCallback(
        IntPtr requestId,
        IntPtr deviceId,
        IntPtr deviceName,
        IntPtr peerIp,
        IntPtr userData
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void NativePairSuccessCallback(
        IntPtr deviceId,
        IntPtr deviceName,
        IntPtr peerIp,
        IntPtr pin,
        IntPtr userData
    );

    [LibraryImport(DllName, EntryPoint = "muclip_sync_set_pair_request_callback")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial void SyncSetPairRequestCallback(IntPtr handle, IntPtr callbackPtr, IntPtr userData);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_set_pair_success_callback")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial void SyncSetPairSuccessCallback(IntPtr handle, IntPtr callbackPtr, IntPtr userData);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_respond_pair")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int SyncRespondPair(IntPtr handle, byte* requestId, byte* pin, int accept);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_request_pair")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int SyncRequestPair(IntPtr handle, byte* peerAddr, byte* pin, uint timeoutSecs, out MuClipPairResult outResult);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_add_paired_device")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int SyncAddPairedDevice(IntPtr handle, byte* deviceId);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_remove_paired_device")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int SyncRemovePairedDevice(IntPtr handle, byte* deviceId);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void NativePairCallback(
        IntPtr deviceId,
        IntPtr deviceName,
        IntPtr pin,
        IntPtr userData
    );

    [LibraryImport(DllName, EntryPoint = "muclip_sync_set_pair_callback")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial void SyncSetPairCallback(IntPtr handle, IntPtr callbackPtr, IntPtr userData);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_send_pair_request")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int SyncSendPairRequest(IntPtr handle, byte* peerAddr, byte* pin);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void NativeUnpairCallback(
        IntPtr deviceId,
        IntPtr userData
    );

    [LibraryImport(DllName, EntryPoint = "muclip_sync_set_unpair_callback")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial void SyncSetUnpairCallback(IntPtr handle, IntPtr callbackPtr, IntPtr userData);

    // -------------------------------------------------------------------------
    // Memory Free & High-Speed Encrypted File Transfer FFI
    // -------------------------------------------------------------------------

    [LibraryImport(DllName, EntryPoint = "muclip_core_free_string")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial void FreeString(IntPtr s);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void NativeFileProgressCallback(
        IntPtr transferId,
        ulong transferredBytes,
        ulong totalBytes,
        ulong speedBps,
        int isSend,
        IntPtr userData
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void NativeFileCompletedCallback(
        IntPtr transferId,
        IntPtr filePath,
        int isSend,
        int success,
        IntPtr errorMsg,
        IntPtr userData
    );

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void NativeFileOfferCallback(
        IntPtr transferId,
        IntPtr fromDeviceId,
        IntPtr fromDeviceName,
        IntPtr fileName,
        ulong fileSize,
        IntPtr userData
    );

    [LibraryImport(DllName, EntryPoint = "muclip_sync_send_file")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial IntPtr SyncSendFile(IntPtr handle, byte* peerAddr, byte* filePath);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_cancel_file_transfer")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int SyncCancelFileTransfer(IntPtr handle, byte* transferId);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_set_file_save_dir")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial int SyncSetFileSaveDir(IntPtr handle, byte* saveDir);

    [LibraryImport(DllName, EntryPoint = "muclip_sync_set_file_callbacks")]
    [UnmanagedCallConv(CallConvs = new[] { typeof(System.Runtime.CompilerServices.CallConvCdecl) })]
    public static partial void SyncSetFileCallbacks(
        IntPtr handle,
        IntPtr progressCb,
        IntPtr completedCb,
        IntPtr offerCb,
        IntPtr userData
    );
}
