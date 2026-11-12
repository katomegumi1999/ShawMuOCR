using MuClip.Models;
using MuClip.Services;
using MuClip.ViewModels;

namespace ShawMuOCR.Tests;

/// <summary>
/// 常用语「规则模板管理」编辑能力的回归守卫。
///
/// 背景：模板卡片原来只有「套用」和「删除」，没有编辑入口，想改一个模板只能删掉重建。
/// 加编辑时最容易踩的两个坑，正是这里守的两件事：
///   1. 编辑后保存若换了新 Id，会变成**新增一条**而不是覆盖原模板；
///   2. 模板表单只能表达「开头 / 末尾」，若用户没改位置模式，保存不能把原来的
///      精确字符位置（如"第 5 字前"）压成 0。
///
/// 依赖真实的 MuClipService（Rust core + SQLite），故打 Slow。
/// </summary>
[Trait("Category", "Slow")]
public class ClipboardTemplateEditingTests
{
    private static (MuClipService Service, string Dir) CreateService()
    {
        string dir = Path.Combine(Path.GetTempPath(), "muclip_tpl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var service = new MuClipService();
        service.Initialize(Path.Combine(dir, "test.db"), startSync: false, startListener: false);
        return (service, dir);
    }

    private static void Cleanup(MuClipService service, string dir)
    {
        try { service.Dispose(); } catch { }
        try { Directory.Delete(dir, recursive: true); } catch { }
    }

    [Fact]
    public void EditRuleTemplate_LoadsValuesIntoFormAndSwitchesToEditMode()
    {
        var (service, dir) = CreateService();
        try
        {
            var vm = new ClipboardViewModel(service);
            var tpl = new PhraseRuleTemplate
            {
                Name = "晚班值守",
                StartTime = "18:00",
                EndTime = "22:00",
                AppendText = "【值班中】",
                PositionMode = 1,
                InsertPosition = 9999
            };
            service.SaveRuleTemplate(tpl);
            vm.LoadRuleTemplates();

            var loaded = vm.SavedRuleTemplates.First(t => t.Id == tpl.Id);
            vm.EditRuleTemplateCommand.Execute(loaded);

            Assert.True(vm.IsEditingRuleTemplate);
            Assert.Equal("晚班值守", vm.ManagerTemplateName);
            Assert.Equal("18:00", vm.ManagerTemplateStartTime);
            Assert.Equal("22:00", vm.ManagerTemplateEndTime);
            Assert.Equal("【值班中】", vm.ManagerTemplateAppendText);
            Assert.Equal(1, vm.ManagerTemplatePositionMode);

            // 表单标题与保存按钮必须切换成编辑语义，否则用户不知道自己在改已有模板
            Assert.Contains("编辑", vm.TemplateFormTitle);
            Assert.Contains("保存修改", vm.TemplateSaveButtonText);
        }
        finally
        {
            Cleanup(service, dir);
        }
    }

    [Fact]
    public void SaveWhileEditing_OverwritesOriginalInsteadOfAddingNew()
    {
        var (service, dir) = CreateService();
        try
        {
            var vm = new ClipboardViewModel(service);
            var tpl = new PhraseRuleTemplate
            {
                Name = "改名前",
                StartTime = "09:00",
                EndTime = "18:00",
                AppendText = "【旧后缀】",
                PositionMode = 1,
                InsertPosition = 9999
            };
            service.SaveRuleTemplate(tpl);
            vm.LoadRuleTemplates();

            int countBefore = vm.SavedRuleTemplates.Count;

            vm.EditRuleTemplateCommand.Execute(vm.SavedRuleTemplates.First(t => t.Id == tpl.Id));
            vm.ManagerTemplateName = "改名后";
            vm.ManagerTemplateAppendText = "【新后缀】";
            vm.AddTemplateFromManagerCommand.Execute(null);

            Assert.True(countBefore == vm.SavedRuleTemplates.Count, "编辑保存不得新增模板");

            var updated = vm.SavedRuleTemplates.Single(t => t.Id == tpl.Id);
            Assert.Equal("改名后", updated.Name);
            Assert.Equal("【新后缀】", updated.AppendText);

            // 保存后必须退出编辑态，否则下一次「新建」会误覆盖这条
            Assert.False(vm.IsEditingRuleTemplate);
            Assert.Null(vm.EditingRuleTemplateId);
        }
        finally
        {
            Cleanup(service, dir);
        }
    }

    [Fact]
    public void SaveWhileEditing_PreservesPreciseInsertPositionWhenModeUnchanged()
    {
        var (service, dir) = CreateService();
        try
        {
            var vm = new ClipboardViewModel(service);
            var tpl = new PhraseRuleTemplate
            {
                Name = "精确位置模板",
                StartTime = "08:00",
                EndTime = "18:00",
                AppendText = "XXX",
                PositionMode = 0,
                InsertPosition = 5 // 第 5 字前插入，表单无法表达
            };
            service.SaveRuleTemplate(tpl);
            vm.LoadRuleTemplates();

            vm.EditRuleTemplateCommand.Execute(vm.SavedRuleTemplates.First(t => t.Id == tpl.Id));
            // 只改追加文字，不动位置模式
            vm.ManagerTemplateAppendText = "YYY";
            vm.AddTemplateFromManagerCommand.Execute(null);

            var updated = vm.SavedRuleTemplates.Single(t => t.Id == tpl.Id);
            Assert.Equal("YYY", updated.AppendText);
            Assert.Equal(5, updated.InsertPosition);
            Assert.Equal(0, updated.PositionMode);
        }
        finally
        {
            Cleanup(service, dir);
        }
    }

    [Fact]
    public void CancelEdit_ReturnsToCreateModeAndSavingAddsNewTemplate()
    {
        var (service, dir) = CreateService();
        try
        {
            var vm = new ClipboardViewModel(service);
            var tpl = new PhraseRuleTemplate
            {
                Name = "原有模板",
                StartTime = "09:00",
                EndTime = "18:00",
                AppendText = "【原】",
                PositionMode = 1,
                InsertPosition = 9999
            };
            service.SaveRuleTemplate(tpl);
            vm.LoadRuleTemplates();

            int countBefore = vm.SavedRuleTemplates.Count;

            vm.EditRuleTemplateCommand.Execute(vm.SavedRuleTemplates.First(t => t.Id == tpl.Id));
            Assert.True(vm.IsEditingRuleTemplate);

            vm.CancelEditRuleTemplateCommand.Execute(null);
            Assert.False(vm.IsEditingRuleTemplate);
            Assert.Equal(string.Empty, vm.ManagerTemplateName);
            Assert.Equal(string.Empty, vm.ManagerTemplateAppendText);

            // 退出编辑后保存应当是新增
            vm.ManagerTemplateName = "全新模板";
            vm.ManagerTemplateAppendText = "【新】";
            vm.AddTemplateFromManagerCommand.Execute(null);

            Assert.Equal(countBefore + 1, vm.SavedRuleTemplates.Count);
            Assert.Contains(vm.SavedRuleTemplates, t => t.Name == "全新模板");
            // 原模板不受影响
            Assert.Equal("【原】", vm.SavedRuleTemplates.Single(t => t.Id == tpl.Id).AppendText);
        }
        finally
        {
            Cleanup(service, dir);
        }
    }
}
