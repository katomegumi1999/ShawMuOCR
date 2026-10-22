using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using ShawMuOCR.Core.Batch;
using ShawMuOCR.Core.OCR;
using ShawMuOCR.Core.Settings;
using ShawMuOCR.Services.Batch;
using ShawMuOCR.ViewModels;
using SkiaSharp;
using Xunit;

namespace ShawMuOCR.Tests;

public class BatchOcrTests
{
    private sealed class FakeEngine : IOcrEngine
    {
        public string ModelName => "FakeModel";
        private readonly string _returnedText;
        private readonly IReadOnlyList<OcrTextBlock> _blocks;

        public FakeEngine(string returnedText = "第一行测试内容\r\n第二行测试内容\t列2", IReadOnlyList<OcrTextBlock>? blocks = null)
        {
            _returnedText = returnedText;
            _blocks = blocks ?? Array.Empty<OcrTextBlock>();
        }

        public Task<OcrResult> RecognizeAsync(OcrImage image, CancellationToken cancellationToken = default)
        {
            return Task.FromResult(new OcrResult
            {
                Text = _returnedText,
                Blocks = _blocks,
                Elapsed = TimeSpan.FromMilliseconds(20),
                IsSuccess = true
            });
        }

        public Task WarmupAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public void Dispose() { }
    }

    private sealed class FakeEngineFactory : IOcrEngineFactory
    {
        private readonly Func<IOcrEngine>? _creator;
        public FakeEngineFactory(Func<IOcrEngine>? creator = null) => _creator = creator;

        public IOcrEngine Create(string modelName) => _creator != null ? _creator() : new FakeEngine();
        public IOcrEngine Create(string modelName, bool enableAngle, int maxSideLen) => _creator != null ? _creator() : new FakeEngine();
        public IOcrEngine Create(AppSettings settings) => _creator != null ? _creator() : new FakeEngine();
    }

    private sealed class FakeSettingsService : ISettingsService
    {
        public AppSettings Current { get; set; } = new();
        public string SettingsFilePath => "test_settings.json";
        public event Action<AppSettings>? SettingsChanged;

        public AppSettings Load() => Current;
        public void Save(AppSettings settings)
        {
            Current = settings;
            SettingsChanged?.Invoke(Current);
        }
    }

    [Fact]
    public void BatchOcrViewModel_AddFiles_FiltersSupportedAndDeduplicates()
    {
        var factory = new FakeEngineFactory();
        var settings = new FakeSettingsService();
        var vm = new BatchOcrViewModel(factory, settings);

        string tempPng1 = Path.Combine(Path.GetTempPath(), $"batch_test_{Guid.NewGuid():N}.png");
        string tempJpg2 = Path.Combine(Path.GetTempPath(), $"batch_test_{Guid.NewGuid():N}.jpg");
        string tempTxtUnsupported = Path.Combine(Path.GetTempPath(), $"batch_test_{Guid.NewGuid():N}.txt");

        try
        {
            File.WriteAllText(tempPng1, "test");
            File.WriteAllText(tempJpg2, "test");
            File.WriteAllText(tempTxtUnsupported, "test");

            // 1. 添加支持的图片文件及不支持的文件
            vm.AddFiles(new[] { tempPng1, tempJpg2, tempTxtUnsupported });
            Assert.Equal(2, vm.TotalCount);

            // 2. 查重测试：重复添加相同的文件
            vm.AddFiles(new[] { tempPng1 });
            Assert.Equal(2, vm.TotalCount); // 数量应保持 2
        }
        finally
        {
            if (File.Exists(tempPng1)) File.Delete(tempPng1);
            if (File.Exists(tempJpg2)) File.Delete(tempJpg2);
            if (File.Exists(tempTxtUnsupported)) File.Delete(tempTxtUnsupported);
        }
    }

    [Fact]
    public void BatchOcrViewModel_AddDirectory_RecursivelyFindsFiles()
    {
        var factory = new FakeEngineFactory();
        var settings = new FakeSettingsService();
        var vm = new BatchOcrViewModel(factory, settings);

        string tempDir = Path.Combine(Path.GetTempPath(), $"batch_dir_{Guid.NewGuid():N}");
        string subDir = Path.Combine(tempDir, "sub");
        Directory.CreateDirectory(subDir);

        string file1 = Path.Combine(tempDir, "1.png");
        string file2 = Path.Combine(subDir, "2.webp");
        string file3 = Path.Combine(subDir, "3.log"); // 不支持

        try
        {
            File.WriteAllText(file1, "png");
            File.WriteAllText(file2, "webp");
            File.WriteAllText(file3, "log");

            vm.AddDirectory(tempDir);

            Assert.Equal(2, vm.TotalCount);
            Assert.Contains(vm.Items, x => x.FileName == "1.png");
            Assert.Contains(vm.Items, x => x.FileName == "2.webp");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BatchOcrViewModel_RemoveSelectedAndClearAll_WorksAsExpected()
    {
        var factory = new FakeEngineFactory();
        var settings = new FakeSettingsService();
        var vm = new BatchOcrViewModel(factory, settings);

        string tempPng1 = Path.Combine(Path.GetTempPath(), $"batch_1_{Guid.NewGuid():N}.png");
        string tempPng2 = Path.Combine(Path.GetTempPath(), $"batch_2_{Guid.NewGuid():N}.png");
        string tempPng3 = Path.Combine(Path.GetTempPath(), $"batch_3_{Guid.NewGuid():N}.png");

        try
        {
            File.WriteAllText(tempPng1, "1");
            File.WriteAllText(tempPng2, "2");
            File.WriteAllText(tempPng3, "3");

            vm.AddFiles(new[] { tempPng1, tempPng2, tempPng3 });
            Assert.Equal(3, vm.TotalCount);

            // 加入队列后默认全部自动勾选
            Assert.All(vm.Items, x => Assert.True(x.IsSelected));

            // 仅保留第二个勾选并移除选中
            vm.Items[0].IsSelected = false;
            vm.Items[2].IsSelected = false;
            vm.Items[1].IsSelected = true;
            vm.RemoveSelectedCommand.Execute(null);
            Assert.Equal(2, vm.TotalCount);
            Assert.DoesNotContain(vm.Items, x => x.FilePath == tempPng2);

            // 清空所有
            vm.ClearAllCommand.Execute(null);
            Assert.Equal(0, vm.TotalCount);
            Assert.False(vm.HasItems);
        }
        finally
        {
            if (File.Exists(tempPng1)) File.Delete(tempPng1);
            if (File.Exists(tempPng2)) File.Delete(tempPng2);
            if (File.Exists(tempPng3)) File.Delete(tempPng3);
        }
    }

    [Fact]
    public async Task BatchOcrProcessor_ProcessesImageAndExportsTxtXlsxDocx()
    {
        var factory = new FakeEngineFactory();
        var settings = new FakeSettingsService();
        var processor = new BatchOcrProcessor(factory, settings);

        string tempDir = Path.Combine(Path.GetTempPath(), $"batch_proc_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        string testPng = Path.Combine(tempDir, "sample_invoice.png");

        try
        {
            // 创建真实有效位图
            using (var bmp = new SKBitmap(100, 40))
            {
                using var canvas = new SKCanvas(bmp);
                canvas.Clear(SKColors.White);
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                using var fs = File.OpenWrite(testPng);
                data.SaveTo(fs);
            }

            var item = BatchOcrItemViewModel.FromFilePath(testPng);

            var options = new BatchOcrOptions
            {
                ExportTxt = true,
                ExportXlsx = true,
                ExportDocx = true,
                IsSuffixEnabled = true,
                SuffixText = "_converted",
                UseOriginalFolder = true,
                MaxCores = 2,
                CpuLimitPercent = 90
            };

            await processor.ProcessBatchAsync(
                new[] { item },
                options,
                null,
                CancellationToken.None);

            Assert.Equal(BatchItemStatus.Completed, item.Status);
            Assert.NotEmpty(item.RecognizedText);

            string expectedTxt = Path.Combine(tempDir, "sample_invoice_converted.txt");
            string expectedXlsx = Path.Combine(tempDir, "sample_invoice_converted.xlsx");
            string expectedDocx = Path.Combine(tempDir, "sample_invoice_converted.docx");

            Assert.True(File.Exists(expectedTxt), "TXT 文件未生成");
            Assert.True(File.Exists(expectedXlsx), "XLSX 文件未生成");
            Assert.True(File.Exists(expectedDocx), "DOCX 文件未生成");

            // 验证 XLSX 内容：遵循换行填入行
            using (var archive = ZipFile.OpenRead(expectedXlsx))
            {
                var sheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml");
                Assert.NotNull(sheetEntry);
                using var stream = sheetEntry.Open();
                var doc = XDocument.Load(stream);
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

                var rows = doc.Descendants(ns + "row").ToList();
                Assert.True(rows.Count >= 2); // 至少包含第一行与第二行

                // 第二行包含制表符拆分的列
                var row2Cells = rows[1].Elements(ns + "c").ToList();
                Assert.True(row2Cells.Count >= 2);
                Assert.Equal("A2", row2Cells[0].Attribute("r")?.Value);
                Assert.Equal("B2", row2Cells[1].Attribute("r")?.Value);
            }

            // 验证 DOCX 段落
            using (var archive = ZipFile.OpenRead(expectedDocx))
            {
                var docEntry = archive.GetEntry("word/document.xml");
                Assert.NotNull(docEntry);
                using var stream = docEntry.Open();
                var doc = XDocument.Load(stream);
                XNamespace w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

                var paragraphs = doc.Descendants(w + "p").ToList();
                Assert.True(paragraphs.Count >= 2);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public async Task BatchOcrViewModel_StartBatch_ValidatesFormatSelection()
    {
        var factory = new FakeEngineFactory();
        var settings = new FakeSettingsService();
        var vm = new BatchOcrViewModel(factory, settings);

        string tempPng = Path.Combine(Path.GetTempPath(), $"batch_{Guid.NewGuid():N}.png");
        try
        {
            File.WriteAllText(tempPng, "png");
            vm.AddFiles(new[] { tempPng });

            // 将所有格式取消勾选
            vm.ExportTxt = false;
            vm.ExportXlsx = false;
            vm.ExportDocx = false;

            await vm.StartBatchAsync();

            // 应该阻止开始并提示
            Assert.False(vm.IsProcessing);
            Assert.Contains("请至少选择一种导出格式", vm.StatusMessage);
        }
        finally
        {
            if (File.Exists(tempPng)) File.Delete(tempPng);
        }
    }

    [Fact]
    public async Task BatchOcrProcessor_RespectsCancellation()
    {
        var factory = new FakeEngineFactory();
        var settings = new FakeSettingsService();
        var processor = new BatchOcrProcessor(factory, settings);

        string tempPng = Path.Combine(Path.GetTempPath(), $"batch_{Guid.NewGuid():N}.png");
        try
        {
            using (var bmp = new SKBitmap(50, 50))
            {
                using var canvas = new SKCanvas(bmp);
                canvas.Clear(SKColors.White);
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                using var fs = File.OpenWrite(tempPng);
                data.SaveTo(fs);
            }

            var item = BatchOcrItemViewModel.FromFilePath(tempPng);
            var options = new BatchOcrOptions { ExportTxt = true, ExportXlsx = false };

            using var cts = new CancellationTokenSource();
            cts.Cancel(); // 提前取消

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                processor.ProcessBatchAsync(new[] { item }, options, null, cts.Token));

            Assert.Equal(BatchItemStatus.Cancelled, item.Status);
        }
        finally
        {
            if (File.Exists(tempPng)) File.Delete(tempPng);
        }
    }

    [Fact]
    public async Task BatchOcrProcessor_SplitColumnsByBlocks_PlacesMultipleHorizontalBoxesIntoColumnsInXlsx()
    {
        // 构造同一水平行上的两个独立框
        var blocks = new[]
        {
            new OcrTextBlock { Text = "商品名称", BoundingBox = new SKRectI(10, 10, 80, 40) },
            new OcrTextBlock { Text = "100元", BoundingBox = new SKRectI(150, 10, 220, 40) }
        };

        var factory = new FakeEngineFactory(() => new FakeEngine(returnedText: "商品名称 100元", blocks: blocks));
        var settings = new FakeSettingsService();
        var processor = new BatchOcrProcessor(factory, settings);

        string tempDir = Path.Combine(Path.GetTempPath(), $"batch_col_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        string testPng = Path.Combine(tempDir, "table_row.png");

        try
        {
            using (var bmp = new SKBitmap(300, 60))
            {
                using var canvas = new SKCanvas(bmp);
                canvas.Clear(SKColors.White);
                using var img = SKImage.FromBitmap(bmp);
                using var data = img.Encode(SKEncodedImageFormat.Png, 100);
                using var fs = File.OpenWrite(testPng);
                data.SaveTo(fs);
            }

            var item = BatchOcrItemViewModel.FromFilePath(testPng);

            // 开启按检测框分列开关
            var options = new BatchOcrOptions
            {
                ExportTxt = false,
                ExportXlsx = true,
                ExportDocx = false,
                SplitColumnsByBlocks = true,
                UseOriginalFolder = true
            };

            await processor.ProcessBatchAsync(new[] { item }, options, null, CancellationToken.None);

            Assert.Equal(BatchItemStatus.Completed, item.Status);
            Assert.Contains("\t", item.RecognizedText); // 文本中间包含制表符

            string expectedXlsx = Path.Combine(tempDir, "table_row_ocr.xlsx");
            Assert.True(File.Exists(expectedXlsx));

            // 验证在 Excel 中生成了 A1 和 B1 两个独立列单元格
            using (var archive = ZipFile.OpenRead(expectedXlsx))
            {
                var sheetEntry = archive.GetEntry("xl/worksheets/sheet1.xml");
                Assert.NotNull(sheetEntry);
                using var stream = sheetEntry.Open();
                var doc = XDocument.Load(stream);
                XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

                var row1 = doc.Descendants(ns + "row").FirstOrDefault();
                Assert.NotNull(row1);

                var cells = row1.Elements(ns + "c").ToList();
                Assert.Equal(2, cells.Count);
                Assert.Equal("A1", cells[0].Attribute("r")?.Value);
                Assert.Equal("B1", cells[1].Attribute("r")?.Value);
                Assert.Equal("商品名称", cells[0].Element(ns + "is")?.Element(ns + "t")?.Value);
                Assert.Equal("100元", cells[1].Element(ns + "is")?.Element(ns + "t")?.Value);
            }
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void BatchOcrViewModel_And_HttpApiViewModel_DefaultsAndAutoSavesSettingsToJson()
    {
        string tempJsonPath = Path.Combine(Path.GetTempPath(), $"settings_autosave_{Guid.NewGuid():N}.json");
        try
        {
            var settingsService = new JsonSettingsService(tempJsonPath);
            var factory = new FakeEngineFactory();

            // 1. 验证默认调度配置：GPU 模式 1 路，CPU 模式半核，利用率默认 50%
            var batchVm = new BatchOcrViewModel(factory, settingsService);
            int expectedBatchCores = batchVm.IsGpuAccelerated ? 1 : Math.Max(1, Environment.ProcessorCount / 2);
            Assert.Equal(expectedBatchCores, batchVm.MaxCores);
            Assert.Equal(50, batchVm.CpuLimitPercent);
            Assert.True(batchVm.HasLogs);

            using var httpVm = new HttpApiViewModel(factory, settingsService);
            int expectedHttpCores = httpVm.IsGpuAccelerated ? 1 : Math.Max(1, Environment.ProcessorCount / 2);
            Assert.Equal(expectedHttpCores, httpVm.MaxCores);
            Assert.Equal(50, httpVm.CpuLimitPercent);
            Assert.Equal(8080, httpVm.Port);
            Assert.False(httpVm.AllowRemote);

            // 2. 修改批量转换设置，验证自动保存到 JSON
            batchVm.ExportTxt = false;
            batchVm.ExportDocx = true;
            batchVm.SuffixText = "_custom";
            batchVm.ImageRotationIndex = 2; // Rotate90
            batchVm.CpuLimitPercent = 65;

            // 3. 修改 HTTP 调用设置，验证自动保存到 JSON
            httpVm.Port = 9095;
            httpVm.AllowRemote = true;
            httpVm.CpuLimitPercent = 70;

            Assert.True(File.Exists(tempJsonPath));

            // 4. 重新从磁盘加载 JsonSettingsService 并实例化 ViewModel，验证配置完整恢复
            var reloadedService = new JsonSettingsService(tempJsonPath);
            var batchVm2 = new BatchOcrViewModel(factory, reloadedService);
            Assert.False(batchVm2.ExportTxt);
            Assert.True(batchVm2.ExportDocx);
            Assert.Equal("_custom", batchVm2.SuffixText);
            Assert.Equal(2, batchVm2.ImageRotationIndex);
            Assert.Equal(65, batchVm2.CpuLimitPercent);

            using var httpVm2 = new HttpApiViewModel(factory, reloadedService);
            Assert.Equal(9095, httpVm2.Port);
            Assert.True(httpVm2.AllowRemote);
            Assert.Equal(70, httpVm2.CpuLimitPercent);
        }
        finally
        {
            if (File.Exists(tempJsonPath)) File.Delete(tempJsonPath);
        }
    }
}
