using System;
using System.Linq;
using MuClip.Interop;
using MuClip.Models;
using MuClip.Services;
using MuClip.ViewModels;
using ShawMuOCR.Windows.Clipboard;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace ShawMuOCR.Tests;

public class MuClipMemoryPoolTests
{
    [Fact]
    public void Test_Manual_Lease_Memory_Pool_Rent_And_Release()
    {
        // 1. 手动从 Rust 原生内存池租借缓冲
        using var lease = MuClipMemoryPool.Rent(8192);

        Assert.True(lease.IsValid);
        Assert.True(lease.LeaseId > 0);
        Assert.True(lease.Pointer != IntPtr.Zero);
        Assert.True(lease.Length >= 8192);

        // 2. 写入数据到租借的原生内存
        var span = lease.WritableSpan;
        span[0] = 0x42;
        span[1023] = 0x99;

        Assert.Equal(0x42, lease.Span[0]);
        Assert.Equal(0x99, lease.Span[1023]);

        // 3. 退出 using 块自动调用 Dispose() 归还租借缓冲至内存池
    }

    [Fact]
    public void Test_MuClipCore_In_Memory_And_Pooled_Queries()
    {
        using var core = MuClipCore.CreateInMemory();
        Assert.True(core.Handle != IntPtr.Zero);

        // 添加测试数据（底层通过栈/内存池高效传输至 Rust）
        long id1 = core.AddHistory("https://github.com/mumu/ShawMuOCR");
        long id2 = core.AddHistory("const result = { name: 'MuClip', version: 1 };");
        long id3 = core.AddHistory("极速剪贴板 Rust 核心引擎测试");

        Assert.True(id1 > 0);
        Assert.True(id2 > 0);
        Assert.True(id3 > 0);

        // 基于手动租借内存池零拷贝读取热缓存
        var cached = core.GetCachedItems();
        Assert.NotEmpty(cached);
        Assert.Contains(cached, item => item.Content.Contains("ShawMuOCR"));

        // 基于租借内存池搜索
        var searchResults = core.SearchHistory("MuClip");
        Assert.NotEmpty(searchResults);
        Assert.Contains(searchResults, r => r.Content.Contains("MuClip"));

        // 内容特征分析
        var analysis = MuClipCore.AnalyzeContent("https://github.com");
        Assert.Equal("url", analysis.ContentType);
        Assert.True(analysis.CharCount > 0);

        // 删除单项
        bool delSuccess = core.DeleteHistory(id1);
        Assert.True(delSuccess);

        // 清空
        bool clearSuccess = core.ClearHistory();
        Assert.True(clearSuccess);
        Assert.Empty(core.GetCachedItems());
    }

    [Fact]
    public void Test_MuClipSync_Lifecycle_And_Peers()
    {
        using var sync = new MuClipSync(
            deviceId: "test-device-uuid",
            deviceName: "Test Machine",
            deviceType: "Windows",
            syncPort: 0, // 自动动态绑定可用端口
            discoveryPort: 0,
            pairingPin: "123456"
        );

        Assert.True(sync.BoundPort > 0);

        // 获取在线对端设备（零拷贝内存池反序列化）
        var peers = sync.GetActivePeers();
        Assert.NotNull(peers);

        // 广播剪贴板数据
        int sent = sync.Broadcast("Hello Peers", "text");
        Assert.True(sent >= 0);

        // 动态修改 PIN 码
        bool pinUpdated = sync.SetPairingPin("654321");
        Assert.True(pinUpdated);
    }

    [Fact]
    public void Test_MuClipService_Integration()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "muclip_integ_" + Guid.NewGuid().ToString("N"));
        string tempDb = Path.Combine(tempDir, "muclip.db");
        try
        {
            using var service = new MuClipService();
            service.Initialize(tempDb, startSync: false, startListener: false);

            service.AddRecord("Service Integration Item 1", broadcast: false);
            service.AddRecord("Service Integration Item 2", broadcast: false);

            var items = service.HistoryItems;
            Assert.True(items.Count >= 2);
            Assert.Contains(items, it => it.Content == "Service Integration Item 2");

            var search = service.Search("Item 1");
            Assert.Single(search);
            Assert.Equal("Service Integration Item 1", search[0].Content);

            service.ClearRecords();
            Assert.Empty(service.HistoryItems);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Test_MuClipService_ClipboardListening_And_EchoSuppression()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "muclip_listener_" + Guid.NewGuid().ToString("N"));
        string tempDb = Path.Combine(tempDir, "muclip.db");
        try
        {
            using var service = new MuClipService();
            service.Initialize(tempDb, startSync: false, startListener: true);

            Assert.True(service.IsListening);

            // 测试向系统剪贴板安全写入（带回环回显抑制）
            string uniqueTestText = "MuClip_Unique_Echo_Test_" + Guid.NewGuid().ToString("N");
            service.CopyToSystemClipboard(uniqueTestText);

            // 验证系统剪贴板已写入成功（带重试等待，防止 Windows 剪贴板历史进程并发争用）
            string? readText = null;
            for (int retry = 0; retry < 5; retry++)
            {
                readText = WindowsClipboardService.Shared.GetText();
                if (readText == uniqueTestText) break;
                Thread.Sleep(50);
            }
            Assert.Equal(uniqueTestText, readText);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Test_MuClip_Duplicate_Item_Moves_To_Front_And_Pushes()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "muclip_dup_" + Guid.NewGuid().ToString("N"));
        string tempDb = Path.Combine(tempDir, "muclip.db");
        try
        {
            using var service = new MuClipService();
            service.Initialize(tempDb, startSync: false, startListener: false);
            service.ClearRecords();

            service.AddRecord("Item Alpha", broadcast: false);
            service.AddRecord("Item Beta", broadcast: false);
            service.AddRecord("Item Gamma", broadcast: false);

            // 当前顺序应为：Gamma (最新), Beta, Alpha
            var items1 = service.HistoryItems;
            Assert.Equal(3, items1.Count);
            Assert.Equal("Item Gamma", items1[0].Content);
            Assert.Equal("Item Beta", items1[1].Content);
            Assert.Equal("Item Alpha", items1[2].Content);

            // 再次添加重复项 "Item Alpha"
            service.AddRecord("Item Alpha", broadcast: false);

            // 验证：总条数依然为 3（不增加冗余），但 "Item Alpha" 被移到最前 (index 0)！
            var items2 = service.HistoryItems;
            Assert.Equal(3, items2.Count);
            Assert.Equal("Item Alpha", items2[0].Content);
            Assert.Equal("Item Gamma", items2[1].Content);
            Assert.Equal("Item Beta", items2[2].Content);

            // 再次添加当前首项 "Item Alpha"
            service.AddRecord("Item Alpha", broadcast: false);
            var items3 = service.HistoryItems;
            Assert.Equal(3, items3.Count);
            Assert.Equal("Item Alpha", items3[0].Content);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Test_MuClipCore_Phrases_Pooled()
    {
        using var core = MuClipCore.CreateInMemory();
        long p1 = core.AddPhrase("SELECT * FROM users;");
        long p2 = core.AddPhrase("git status && git log -n 5");
        Assert.True(p1 > 0);
        Assert.True(p2 > 0);

        var phrases = core.QueryPhrases();
        Assert.Contains(phrases, p => p.Content == "SELECT * FROM users;");
        Assert.Contains(phrases, p => p.Content == "git status && git log -n 5");

        core.UpdatePhraseUsed(p1);

        bool deleted = core.DeletePhrase(p1);
        Assert.True(deleted);

        var phrasesAfter = core.QueryPhrases();
        Assert.DoesNotContain(phrasesAfter, p => p.Id == p1);
        Assert.Contains(phrasesAfter, p => p.Id == p2);
    }

    [Fact]
    public void Test_MuClip_Phrases_Add_Query_Use_Delete()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "muclip_phrases_" + Guid.NewGuid().ToString("N"));
        string tempDb = Path.Combine(tempDir, "muclip.db");
        try
        {
            using var service = new MuClipService();
            service.Initialize(tempDb, startSync: false, startListener: false);

            // 默认预设的常用语
            Assert.NotEmpty(service.Phrases);

            // 添加一条自定义常用语
            string customPhrase = "测试快捷常用语_" + Guid.NewGuid().ToString("N");
            service.AddPhrase(customPhrase);

            var item = service.Phrases.FirstOrDefault(p => p.Content == customPhrase);
            Assert.NotNull(item);
            Assert.True(item.Id > 0);

            // 模拟使用常用语（热度更新 + 写入剪贴板）
            service.UsePhrase(item.Id, item.Content);

            // 删除该常用语
            service.DeletePhrase(item.Id);
            Assert.DoesNotContain(service.Phrases, p => p.Id == item.Id);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Test_MuClipSync_Random_Pin_Pairing()
    {
        string pinA = MuClipService.GenerateRandomPin();
        string pinB = MuClipService.GenerateRandomPin();
        Assert.Equal(6, pinA.Length);
        Assert.Equal(6, pinB.Length);

        using var syncA = new MuClipSync("device-a", "Machine A", "Windows", 43501, 43601, pinA);
        using var syncB = new MuClipSync("device-b", "Machine B", "Windows", 43502, 43602, pinB);

        string receivedPinOnB = string.Empty;
        string senderOnB = string.Empty;
        var pairEvent = new System.Threading.ManualResetEventSlim(false);

        syncB.PairRequestReceived += (s, e) =>
        {
            senderOnB = e.DeviceName;
            receivedPinOnB = e.Pin;
            syncB.SetPairingPin(e.Pin);
            pairEvent.Set();
        };

        // Device A 发送全新随机 PIN 给 Device B 发起配对
        string randomPairPin = MuClipService.GenerateRandomPin();
        bool sent = syncA.SendPairRequest($"127.0.0.1:{syncB.BoundPort}", randomPairPin);
        Assert.True(sent);

        Assert.True(pairEvent.Wait(TimeSpan.FromSeconds(3)));
        Assert.Equal(randomPairPin, receivedPinOnB);
        Assert.Equal("Machine A", senderOnB);
    }

    [Fact]
    public void Test_MuClipService_PairedDevices_Management()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "muclip_test_" + Guid.NewGuid().ToString("N"));
        string tempDb = Path.Combine(tempDir, "muclip.db");
        try
        {
            using (var service = new MuClipService())
            {
                // 未初始化时读取 PairingPin 必须抛出 InvalidOperationException，防止弱默认值泄漏
                Assert.Throws<InvalidOperationException>(() => _ = service.PairingPin);
                // 禁止传入弱默认值 "000000"
                Assert.Throws<ArgumentException>(() => service.Initialize(tempDb, startSync: false, pairingPin: "000000", startListener: false));

                service.Initialize(tempDb, startSync: true, syncPort: 43503, pairingPin: "123456", startListener: false);
                Assert.Empty(service.PairedDevices);

                // 添加普通局域网已配对设备（不应误注册为端口映射节点）
                service.AddOrUpdatePairedDevice("dev-target-1", "MacBook Pro", "127.0.0.1", 42424);
                Assert.Single(service.PairedDevices);
                Assert.Equal("MacBook Pro", service.PairedDevices[0].DeviceName);
                Assert.Equal("dev-target-1", service.PairedDevices[0].DeviceId);
                Assert.Equal("127.0.0.1", service.PairedDevices[0].Ip);
                Assert.Equal(42424, service.PairedDevices[0].SyncPort);
                Assert.False(service.PairedDevices[0].IsManual);
                Assert.Empty(service.GetManualPeers());

                // 解除配对
                service.RemovePairedDevice("dev-target-1");
                Assert.Empty(service.PairedDevices);
            }

            // 测试重启恢复持久化
            using (var service1 = new MuClipService())
            {
                service1.Initialize(tempDb, startSync: true, syncPort: 43503, pairingPin: "123456", startListener: false);
                service1.AddOrUpdatePairedDevice("dev-workstation", "Workstation", "192.168.2.100", 42424);
                Assert.Throws<ArgumentException>(() => service1.UpdatePairingPin("000000"));
                service1.UpdatePairingPin("888888");
                Assert.Single(service1.PairedDevices);
            }

            using (var service2 = new MuClipService())
            {
                service2.Initialize(tempDb, startSync: true, syncPort: 43503, pairingPin: null, startListener: false);
                // 验证重启后已配对设备恢复
                Assert.Single(service2.PairedDevices);
                Assert.Equal("Workstation", service2.PairedDevices[0].DeviceName);
                Assert.Equal("888888", service2.PairingPin); // 验证 PIN 码也自动持久化保留
            }
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Test_ClipboardViewModel_PinMatching_Workflow()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "muclip_vm_test_" + Guid.NewGuid().ToString("N"));
        string tempDb = Path.Combine(tempDir, "muclip.db");
        try
        {
            using var service = new MuClipService();
            service.Initialize(tempDb, startSync: true, syncPort: 43504, startListener: false);
            var vm = new MuClip.ViewModels.ClipboardViewModel(service);

            // 1. 发起方发起配对：生成随机 PIN 码，进入等待确认状态
            var targetPeer = new MuClip.Models.PeerDeviceInfo
            {
                DeviceId = "remote-dev-456",
                DeviceName = "MacBook Air",
                Ip = "127.0.0.1",
                SyncPort = 59998,
                IsOnline = true
            };

            vm.PairWithPeer(targetPeer);
            Assert.Equal("MacBook Air", vm.PendingPairDeviceName);
            Assert.Equal("remote-dev-456", vm.PendingPairDeviceId);
            Assert.Equal(6, vm.PendingPairPin.Length);

            // 2. 接收方收到配对请求：弹出输入对话框
            string incomingExpectedPin = "654321";
            vm.IncomingPairDeviceId = "remote-dev-789";
            vm.IncomingPairDeviceName = "Surface Pro";
            vm.IncomingPairExpectedPin = incomingExpectedPin;
            vm.IncomingPairIp = "192.168.1.66";
            vm.IncomingPairPort = 42424;
            vm.IsIncomingPairRequestVisible = true;

            // 输入错误 PIN 码 -> 验证失败
            vm.IncomingPairInputPin = "111111";
            vm.ConfirmIncomingPair();
            Assert.True(vm.IsIncomingPairRequestVisible);
            Assert.Contains("错误", vm.IncomingPairErrorMessage);
            Assert.Empty(service.PairedDevices);

            // 输入正确 PIN 码 -> 验证通过，建立配对
            vm.IncomingPairInputPin = incomingExpectedPin;
            vm.ConfirmIncomingPair();
            Assert.False(vm.IsIncomingPairRequestVisible);
            Assert.Empty(vm.IncomingPairErrorMessage);
            Assert.Single(service.PairedDevices);
            Assert.Equal("Surface Pro", service.PairedDevices[0].DeviceName);
            Assert.Equal(incomingExpectedPin, service.PairingPin);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public async Task Test_SingleStream_PairHandshake_Integration()
    {
        string dir1 = Path.Combine(Path.GetTempPath(), "muclip_hs_test1_" + Guid.NewGuid().ToString("N"));
        string dir2 = Path.Combine(Path.GetTempPath(), "muclip_hs_test2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir1);
        Directory.CreateDirectory(dir2);

        using var s1 = new MuClipService();
        using var s2 = new MuClipService();

        try
        {
            s1.Initialize(Path.Combine(dir1, "test.db"), startSync: true, syncPort: 43505, startListener: false, customDeviceId: "test-dev-1", customDeviceName: "Device 1");
            s2.Initialize(Path.Combine(dir2, "test.db"), startSync: true, syncPort: 43506, startListener: false, customDeviceId: "test-dev-2", customDeviceName: "Device 2");

            ushort port1 = s1.SyncPort;
            Assert.True(port1 > 0);

            var pairReqTcs = new TaskCompletionSource<(string reqId, string devId, string devName, string ip)>();
            s1.IncomingPairRequested += (reqId, devId, devName, ip) =>
            {
                pairReqTcs.TrySetResult((reqId, devId, devName, ip));
            };

            string testPin = "741852";

            // S2 作为发起方向 S1 发起配对
            var requestTask = Task.Run(async () =>
            {
                return await s2.RequestPairAsync("127.0.0.1", port1, testPin, 10);
            });

            // 等待 S1 收到请求
            var incoming = await Task.WhenAny(pairReqTcs.Task, Task.Delay(5000));
            Assert.Same(pairReqTcs.Task, incoming);

            var reqData = await pairReqTcs.Task;
            Assert.False(string.IsNullOrEmpty(reqData.reqId));

            // 1. S1 先输入错误 PIN 码 -> Rust 返回 2 (密码错误)，连接保持不掐断
            int wrongCode = s1.RespondPair(reqData.reqId, "999999", true);
            Assert.Equal(2, wrongCode);

            // 2. S1 输入正确 PIN 码 -> Rust 返回 0 (成功)
            int okCode = s1.RespondPair(reqData.reqId, testPin, true);
            Assert.Equal(0, okCode);

            // 等待 S2 握手完成
            var s2Result = await requestTask;
            Assert.True(s2Result.Success, $"配对握手失败: {s2Result.Message}");

            // 验证两端长期密钥协商与配对状态均自动完成
            Assert.True(s2.PairingPin == "已信任 (长期密钥)" || s2.PairingPin == testPin);
            Assert.Single(s2.PairedDevices);

            // 等待短时间确保 S1 触发 PairEstablished 保存
            await Task.Delay(200);
            Assert.True(s1.PairingPin == "已信任 (长期密钥)" || s1.PairingPin == testPin);
            Assert.Single(s1.PairedDevices);
        }
        finally
        {
            try { if (Directory.Exists(dir1)) Directory.Delete(dir1, true); } catch { }
            try { if (Directory.Exists(dir2)) Directory.Delete(dir2, true); } catch { }
        }
    }

    [Fact]
    public void Test_FileTransfer_Models_And_ViewModel_Navigation()
    {
        // 1. 测试文件大小格式化
        Assert.Equal("500 B", FileTransferItem.FormatFileSize(500));
        Assert.Equal("1.5 KB", FileTransferItem.FormatFileSize(1536));
        Assert.Equal("10.00 MB", FileTransferItem.FormatFileSize(10 * 1024 * 1024));
        Assert.Equal("1.50 GB", FileTransferItem.FormatFileSize((ulong)(1.5 * 1024 * 1024 * 1024)));

        // 2. 测试传输任务进度与状态更新
        var item = new FileTransferItem
        {
            TransferId = "test_tid_1",
            FileName = "demo.mp4",
            FilePath = @"C:\Test\demo.mp4",
            TotalBytes = 100 * 1024 * 1024,
            IsSend = true
        };

        Assert.Equal("发送 ↑", item.DirectionBadge);
        Assert.Equal(TransferStatus.Transferring, item.Status);
        Assert.True(item.CanCancel);
        Assert.False(item.CanOpenFile);

        item.UpdateProgress(50 * 1024 * 1024, 100 * 1024 * 1024, 25 * 1024 * 1024);
        Assert.Equal(50.0, item.ProgressPercent);
        Assert.Equal("50.0%", item.FormattedPercent);
        Assert.Equal("25.00 MB/s", item.FormattedSpeed);

        item.MarkCompleted(@"C:\Test\demo.mp4", true, string.Empty);
        Assert.Equal(TransferStatus.Completed, item.Status);
        Assert.Equal(100.0, item.ProgressPercent);
        Assert.False(item.CanCancel);

        // 3. 测试 ViewModel 二级页面呼出与返回
        string tempDir = Path.Combine(Path.GetTempPath(), "muclip_vm_ft_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            using var service = new MuClipService();
            service.Initialize(Path.Combine(tempDir, "test.db"), startSync: false, startListener: false);

            string customSaveDir = Path.Combine(tempDir, "Downloads");
            service.SetFileSaveDirectory(customSaveDir);
            Assert.Equal(customSaveDir, service.FileSaveDirectory);

            var vm = new ClipboardViewModel(service);
            Assert.False(vm.IsFileTransferViewActive);

            // 点击二级页面呼出按钮
            vm.OpenFileTransferViewCommand.Execute(null);
            Assert.True(vm.IsFileTransferViewActive);

            // 点击返回剪贴板
            vm.CloseFileTransferViewCommand.Execute(null);
            Assert.False(vm.IsFileTransferViewActive);

            // Toggle
            vm.ToggleFileTransferViewCommand.Execute(null);
            Assert.True(vm.IsFileTransferViewActive);
            vm.ToggleFileTransferViewCommand.Execute(null);
            Assert.False(vm.IsFileTransferViewActive);
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
        }
    }

    [Fact]
    public void Test_Query_And_Search_Return_Structure_Upgrade()
    {
        using var core = MuClipCore.CreateInMemory();

        string urlText = "https://github.com/rust-lang";
        long hId = core.AddHistory(urlText);
        Assert.True(hId > 0);

        // 1. 验证 GetCachedItems 直接返回 content_type, char_count, line_count, byte_count, preview
        var cached = core.GetCachedItems();
        var cachedItem = Assert.Single(cached);
        Assert.Equal("url", cachedItem.ContentType);
        Assert.Equal(28, cachedItem.CharCount);
        Assert.Equal(1, cachedItem.LineCount);
        Assert.Equal(28, cachedItem.ByteCount);
        Assert.Equal(urlText, cachedItem.Preview);

        // 2. 验证 QueryHistory 與 SearchHistory 同样直接返回统计与预览字段
        var queried = Assert.Single(core.QueryHistory(10));
        Assert.Equal("url", queried.ContentType);
        Assert.Equal(28, queried.CharCount);
        Assert.Equal(1, queried.LineCount);
        Assert.Equal(28, queried.ByteCount);
        Assert.Equal(urlText, queried.Preview);

        var searched = Assert.Single(core.SearchHistory("rust-lang", 10));
        Assert.Equal("url", searched.ContentType);
        Assert.Equal(28, searched.CharCount);
        Assert.Equal(28, searched.ByteCount);
        Assert.Equal(urlText, searched.Preview);

        // 3. 验证 QueryPhrases 与 SearchPhrases 返回统计、预览与 sort_order 字段
        string codePhrase = "fn main() {\n    println!(\"Hello\");\n}";
        long pId = core.AddPhrase(codePhrase);
        Assert.True(pId > 0);

        var phrases = core.QueryPhrases();
        var phrase = Assert.Single(phrases);
        Assert.Equal("code", phrase.ContentType);
        Assert.Equal(codePhrase.Length, phrase.CharCount);
        Assert.Equal(3, phrase.LineCount);
        Assert.Equal(System.Text.Encoding.UTF8.GetByteCount(codePhrase), phrase.ByteCount);
        Assert.NotEmpty(phrase.Preview);
        Assert.True(phrase.SortOrder >= 0);

        var searchedPhrase = Assert.Single(core.SearchPhrases("println"));
        Assert.Equal("code", searchedPhrase.ContentType);
        Assert.Equal(3, searchedPhrase.LineCount);
        Assert.Equal(phrase.Preview, searchedPhrase.Preview);
    }

    [Fact]
    public async Task Test_ManualPeer_ImmediateDisplay_HeartbeatProbe_And_Removal()
    {
        string dirA = Path.Combine(Path.GetTempPath(), "muclip_manual_a_" + Guid.NewGuid().ToString("N"));
        string dirB = Path.Combine(Path.GetTempPath(), "muclip_manual_b_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        using var sA = new MuClipService();
        using var sB = new MuClipService();

        try
        {
            sA.Initialize(Path.Combine(dirA, "test.db"), startSync: true, syncPort: 43507, startListener: false, customDeviceId: "dev-a-client", customDeviceName: "Client Workstation");
            sB.Initialize(Path.Combine(dirB, "test.db"), startSync: true, syncPort: 43508, startListener: false, customDeviceId: "dev-b-mapped", customDeviceName: "Mapped Remote Server");
            sA.AutoDiscoveryEnabled = false;
            sB.AutoDiscoveryEnabled = false;

            var vmA = new ClipboardViewModel(sA);

            // 1. 添加离线映射节点 -> 立即出现在 PeerDevices 列表中且 IsManual = true
            vmA.ManualPeerInput = "127.0.0.1:59999";
            vmA.AddManualPeerCommand.Execute(null);

            Assert.Contains("127.0.0.1:59999", sA.GetManualPeers());
            var placeholderPeer = vmA.PeerDevices.FirstOrDefault(p => p.Endpoint == "127.0.0.1:59999");
            Assert.NotNull(placeholderPeer);
            Assert.True(placeholderPeer.IsManual);
            Assert.Contains("映射节点", placeholderPeer.DeviceName);

            // 移除该占位映射节点 -> 立即从 PeerDevices 中移除
            vmA.RemoveManualPeerCommand.Execute(placeholderPeer);
            Assert.DoesNotContain(vmA.PeerDevices, p => p.Endpoint == "127.0.0.1:59999");

            // 2. 添加对端 B 的真实监听端口（模拟端口映射，支持未配对 TCP MSG_HEARTBEAT 双向探测）
            string mappedEndpoint = $"127.0.0.1:{sB.SyncPort}";
            vmA.ManualPeerInput = mappedEndpoint;
            vmA.AddManualPeerCommand.Execute(null);

            // 等待异步 TCP MSG_HEARTBEAT 探测完成并升级为真实设备信息（且保留映射地址 127.0.0.1 与 IsManual = true）
            PeerDeviceInfo? probedPeer = null;
            for (int i = 0; i < 15; i++)
            {
                sA.RefreshPeers();
                probedPeer = sA.ActivePeers.FirstOrDefault(p =>
                    p.DeviceId == "dev-b-mapped" && p.IsManual && p.Ip == "127.0.0.1");
                if (probedPeer != null)
                    break;
                await Task.Delay(150);
            }

            Assert.NotNull(probedPeer);
            Assert.Equal("dev-b-mapped", probedPeer.DeviceId);
            Assert.Equal("Mapped Remote Server", probedPeer.DeviceName);
            Assert.Equal("127.0.0.1", probedPeer.Ip);
            Assert.Equal(sB.SyncPort, probedPeer.SyncPort);
            Assert.True(probedPeer.IsManual);

            // 3. 支持通过解析后的 device_id 移除映射节点
            vmA.RemoveManualPeerCommand.Execute("dev-b-mapped");
            Assert.DoesNotContain(sA.ActivePeers, p => p.DeviceId == "dev-b-mapped");
        }
        finally
        {
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch { }
            try { if (Directory.Exists(dirB)) Directory.Delete(dirB, true); } catch { }
        }
    }

    [Fact]
    public async Task Test_Bidirectional_Unpair_Removes_Peer_On_Both_Sides()
    {
        string dirA = Path.Combine(Path.GetTempPath(), "muclip_unpair_a_" + Guid.NewGuid().ToString("N"));
        string dirB = Path.Combine(Path.GetTempPath(), "muclip_unpair_b_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dirA);
        Directory.CreateDirectory(dirB);

        using var sA = new MuClipService();
        using var sB = new MuClipService();

        try
        {
            sA.Initialize(Path.Combine(dirA, "test.db"), startSync: true, syncPort: 43517, startListener: false, customDeviceId: "dev-unpair-a", customDeviceName: "Device Alpha");
            sB.Initialize(Path.Combine(dirB, "test.db"), startSync: true, syncPort: 43518, startListener: false, customDeviceId: "dev-unpair-b", customDeviceName: "Device Beta");
            sA.AutoDiscoveryEnabled = false;
            sB.AutoDiscoveryEnabled = false;

            // 模拟双端已完成配对
            sA.AddOrUpdatePairedDevice("dev-unpair-b", "Device Beta", "127.0.0.1", sB.SyncPort);
            sB.AddOrUpdatePairedDevice("dev-unpair-a", "Device Alpha", "127.0.0.1", sA.SyncPort);

            Assert.Contains(sA.PairedDevices, p => p.DeviceId == "dev-unpair-b");
            Assert.Contains(sB.PairedDevices, p => p.DeviceId == "dev-unpair-a");

            string? unpairedDeviceIdOnB = null;
            sB.RemotePeerUnpaired += (devId, devName) =>
            {
                unpairedDeviceIdOnB = devId;
            };

            // 在 A 端点击解除配对 -> B 端应自动收到 MSG_UNPAIR_NOTICE 并从 PairedDevices 中移除 A
            sA.RemovePairedDevice("dev-unpair-b");
            Assert.DoesNotContain(sA.PairedDevices, p => p.DeviceId == "dev-unpair-b");

            for (int i = 0; i < 20; i++)
            {
                if (!sB.PairedDevices.Any(p => p.DeviceId == "dev-unpair-a"))
                    break;
                await Task.Delay(100);
            }

            Assert.DoesNotContain(sB.PairedDevices, p => p.DeviceId == "dev-unpair-a");
            Assert.Equal("dev-unpair-a", unpairedDeviceIdOnB);
        }
        finally
        {
            try { if (Directory.Exists(dirA)) Directory.Delete(dirA, true); } catch { }
            try { if (Directory.Exists(dirB)) Directory.Delete(dirB, true); } catch { }
        }
    }
}



