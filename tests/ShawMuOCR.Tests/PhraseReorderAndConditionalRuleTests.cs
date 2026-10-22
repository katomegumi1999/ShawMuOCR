using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using MuClip.Interop;
using MuClip.Models;
using MuClip.Services;
using Xunit;

namespace ShawMuOCR.Tests;

public class PhraseReorderAndConditionalRuleTests
{


    [Fact]
    public void PhraseConditionalRule_PositionsAndModes_InsertsCorrectly()
    {
        // 1. 在第 0 字符前插入 (即文本开头)
        var rulePrepend = new PhraseConditionalRule
        {
            IsEnabled = true,
            InsertPosition = 0,
            PositionMode = 0, // 在字符前插入
            StartTime = "00:00",
            EndTime = "23:59",
            AppendText = "【开头】"
        };
        string resultPrepend = rulePrepend.Apply("你好世界");
        Assert.Equal("【开头】你好世界", resultPrepend);

        // 2. 在指定字符后面追加 (例如在第 1 字符 '好' 后面追加)
        var ruleAfter = new PhraseConditionalRule
        {
            IsEnabled = true,
            InsertPosition = 1, // '好' 索引为 1
            PositionMode = 1,   // 在字符后追加
            StartTime = "00:00",
            EndTime = "23:59",
            AppendText = "，早上好！"
        };
        string resultAfter = ruleAfter.Apply("你好世界");
        Assert.Equal("你好，早上好！世界", resultAfter);

        // 3. 在文本最末尾追加 (索引超过长度或指定为最后字符后)
        var ruleAppend = new PhraseConditionalRule
        {
            IsEnabled = true,
            InsertPosition = 3, // 最后一个字符 '界'
            PositionMode = 1,
            StartTime = "00:00",
            EndTime = "23:59",
            AppendText = "【末尾】"
        };
        string resultAppend = ruleAppend.Apply("你好世界");
        Assert.Equal("你好世界【末尾】", resultAppend);
    }

    [Fact]
    public void PhraseConditionalRule_TimeWindow_NormalAndOvernight()
    {
        // 常规工作时段：09:00 ~ 18:00
        var workRule = new PhraseConditionalRule
        {
            IsEnabled = true,
            InsertPosition = 3, // 最后一个字符 '容'
            PositionMode = 1,   // 后面追加
            StartTime = "09:00",
            EndTime = "18:00",
            AppendText = " [工作时段]"
        };

        var matchedTime = new DateTime(2026, 10, 17, 10, 30, 0);
        var unmatchedTimeMorning = new DateTime(2026, 10, 17, 8, 30, 0);
        var unmatchedTimeEvening = new DateTime(2026, 10, 17, 19, 0, 0);

        Assert.True(workRule.IsTimeMatched(matchedTime));
        Assert.False(workRule.IsTimeMatched(unmatchedTimeMorning));
        Assert.False(workRule.IsTimeMatched(unmatchedTimeEvening));

        Assert.Equal("报告内容 [工作时段]", workRule.Apply("报告内容", matchedTime));
        Assert.Equal("报告内容", workRule.Apply("报告内容", unmatchedTimeMorning));

        // 跨午夜夜间时段：22:00 ~ 06:00
        var nightRule = new PhraseConditionalRule
        {
            IsEnabled = true,
            InsertPosition = 3, // 最后一个字符 '班'
            PositionMode = 1,   // 后面追加
            StartTime = "22:00",
            EndTime = "06:00",
            AppendText = " [夜间值班]"
        };

        var matchedLateNight = new DateTime(2026, 10, 17, 23, 15, 0);
        var matchedEarlyMorning = new DateTime(2026, 10, 17, 4, 30, 0);
        var unmatchedNoon = new DateTime(2026, 10, 17, 12, 0, 0);

        Assert.True(nightRule.IsTimeMatched(matchedLateNight));
        Assert.True(nightRule.IsTimeMatched(matchedEarlyMorning));
        Assert.False(nightRule.IsTimeMatched(unmatchedNoon));

        Assert.Equal("值班巡检 [夜间值班]", nightRule.Apply("值班巡检", matchedLateNight));
        Assert.Equal("值班巡检", nightRule.Apply("值班巡检", unmatchedNoon));
    }

    [Fact]
    public void MuClipService_PhraseRuleAndProcessedContent_WorkAsExpected()
    {
        var service = new MuClipService();
        long phraseId = 999;

        // 初始无规则
        Assert.Null(service.GetPhraseRule(phraseId));
        Assert.Equal("纯文本内容", service.GetProcessedPhraseContent(phraseId, "纯文本内容"));

        // 设置全天有效规则
        var rule = new PhraseConditionalRule
        {
            IsEnabled = true,
            InsertPosition = 0,
            PositionMode = 0,
            StartTime = "00:00",
            EndTime = "23:59",
            AppendText = "【动态前缀】"
        };
        service.SetPhraseRule(phraseId, rule);

        var loadedRule = service.GetPhraseRule(phraseId);
        Assert.NotNull(loadedRule);
        Assert.True(loadedRule.IsEnabled);
        Assert.Equal("【动态前缀】", loadedRule.AppendText);

        string processed = service.GetProcessedPhraseContent(phraseId, "测试内容");
        Assert.Equal("【动态前缀】测试内容", processed);

        // 停用规则
        rule.IsEnabled = false;
        service.SetPhraseRule(phraseId, rule);
        Assert.Equal("测试内容", service.GetProcessedPhraseContent(phraseId, "测试内容"));
    }

    [Fact]
    public void MuClipCore_ReorderPhrases_NativeFfiWorks()
    {
        string tempDb = Path.Combine(Path.GetTempPath(), $"muclip_reorder_ffi_test_{Guid.NewGuid():N}.db");
        try
        {
            using var core = new MuClipCore(tempDb);
            long p1 = core.AddPhrase("Phrase 1");
            long p2 = core.AddPhrase("Phrase 2");
            long p3 = core.AddPhrase("Phrase 3");

            bool ok = core.ReorderPhrases(new[] { p3, p1, p2 });
            Assert.True(ok);

            var list = core.QueryPhrases();
            Assert.Equal(3, list.Count);
            Assert.Equal(p3, list[0].Id);
            Assert.Equal(p1, list[1].Id);
            Assert.Equal(p2, list[2].Id);
        }
        finally
        {
            if (File.Exists(tempDb))
            {
                try { File.Delete(tempDb); } catch { }
            }
        }
    }

    [Fact]
    public void PhraseConditionalRule_MultiRules_EvaluatesTimeSlotsSequentially()
    {
        var morningRule = new PhraseConditionalRule
        {
            IsEnabled = true,
            InsertPosition = 0,
            PositionMode = 0,
            StartTime = "06:00",
            EndTime = "12:00",
            AppendText = "【上午好】"
        };

        var afternoonRule = new PhraseConditionalRule
        {
            IsEnabled = true,
            InsertPosition = 0,
            PositionMode = 0,
            StartTime = "12:00",
            EndTime = "18:00",
            AppendText = "【下午好】"
        };

        var allDaySignatureRule = new PhraseConditionalRule
        {
            IsEnabled = true,
            InsertPosition = 9999, // 末尾
            PositionMode = 1,
            StartTime = "00:00",
            EndTime = "23:59",
            AppendText = " ——客服部"
        };

        var rules = new List<PhraseConditionalRule> { morningRule, afternoonRule, allDaySignatureRule };

        // 1. 上午 09:30 测试：应命中 morningRule 与 allDaySignatureRule
        var morningTime = new DateTime(2026, 10, 17, 9, 30, 0);
        string morningResult = PhraseConditionalRule.ApplyRules("欢迎光临", rules, morningTime);
        Assert.Equal("【上午好】欢迎光临 ——客服部", morningResult);

        // 2. 下午 15:30 测试：应命中 afternoonRule 与 allDaySignatureRule
        var afternoonTime = new DateTime(2026, 10, 17, 15, 30, 0);
        string afternoonResult = PhraseConditionalRule.ApplyRules("欢迎光临", rules, afternoonTime);
        Assert.Equal("【下午好】欢迎光临 ——客服部", afternoonResult);

        // 3. 停用签名规则
        allDaySignatureRule.IsEnabled = false;
        string disabledSignResult = PhraseConditionalRule.ApplyRules("欢迎光临", rules, morningTime);
        Assert.Equal("【上午好】欢迎光临", disabledSignResult);
    }

    [Fact]
    public void MuClipService_SetAndGetPhraseRules_MultiRulesWork()
    {
        var service = new MuClipService();
        long phraseId = 10086;

        var r1 = new PhraseConditionalRule
        {
            IsEnabled = true,
            InsertPosition = 0,
            PositionMode = 0,
            StartTime = "09:00",
            EndTime = "12:00",
            AppendText = "[早班]"
        };

        var r2 = new PhraseConditionalRule
        {
            IsEnabled = true,
            InsertPosition = 0,
            PositionMode = 0,
            StartTime = "13:00",
            EndTime = "18:00",
            AppendText = "[中班]"
        };

        service.SetPhraseRules(phraseId, new[] { r1, r2 });

        var fetched = service.GetPhraseRules(phraseId);
        Assert.Equal(2, fetched.Count);
        Assert.Equal("[早班]", fetched[0].AppendText);
        Assert.Equal("[中班]", fetched[1].AppendText);

        // 测试 GetPhraseRule 向下兼容返回第一条
        var first = service.GetPhraseRule(phraseId);
        Assert.NotNull(first);
        Assert.Equal("[早班]", first.AppendText);

        // 测试删除一条规则后重新保存
        service.SetPhraseRules(phraseId, new[] { r2 });
        var updated = service.GetPhraseRules(phraseId);
        Assert.Single(updated);
        Assert.Equal("[中班]", updated[0].AppendText);
    }

    [Fact]
    public void ClipboardViewModel_RuleFormAndListOperations_WorkCorrectly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "muclip_vm_rule_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            using var service = new MuClipService();
            service.Initialize(Path.Combine(tempDir, "test.db"), startSync: false, startListener: false);
            var vm = new MuClip.ViewModels.ClipboardViewModel(service);

            // 模拟新建常用语
            vm.PhraseEditorText = "尊敬的用户您好";
            vm.RuleStartTime = "08:00";
            vm.RuleEndTime = "12:00";
            vm.RuleInsertPosition = 0;
            vm.RulePositionMode = 0;
            vm.RuleAppendText = "【上午】";

            // 点击添加规则
            vm.AddOrUpdateRule();
            Assert.Single(vm.CurrentPhraseRules);
            Assert.True(vm.IsPhraseRuleEnabled);
            Assert.True(vm.HasRules);
            Assert.Equal("【上午】", vm.CurrentPhraseRules[0].AppendText);
            Assert.Equal(string.Empty, vm.RuleAppendText); // 表单自动清空

            // 再添加第二条规则
            vm.RuleStartTime = "14:00";
            vm.RuleEndTime = "18:00";
            vm.RuleInsertPosition = 0;
            vm.RulePositionMode = 0;
            vm.RuleAppendText = "【下午】";
            vm.AddOrUpdateRule();
            Assert.Equal(2, vm.CurrentPhraseRules.Count);

            // 编辑第一条规则
            var firstRule = vm.CurrentPhraseRules[0];
            vm.EditRule(firstRule);
            Assert.True(vm.IsEditingRule);
            Assert.Equal("【上午】", vm.RuleAppendText);

            // 修改第一条规则内容并保存
            vm.RuleAppendText = "【上午好】";
            vm.AddOrUpdateRule();
            Assert.False(vm.IsEditingRule);
            Assert.Equal("【上午好】", firstRule.AppendText);

            // 切换停用第一条规则
            vm.ToggleRule(firstRule);
            Assert.False(firstRule.IsEnabled);

            // 删除第二条规则
            var secondRule = vm.CurrentPhraseRules[1];
            vm.DeleteRule(secondRule);
            Assert.Single(vm.CurrentPhraseRules);

            // 快捷时段预设测试 (06-10早上，10-12上午，12-14中午，14-18下午，18-22晚上，22-06夜间)
            vm.SetPresetTimeRange("early_morning");
            Assert.Equal("06:00", vm.RuleStartTime);
            Assert.Equal("10:00", vm.RuleEndTime);

            vm.SetPresetTimeRange("morning");
            Assert.Equal("10:00", vm.RuleStartTime);
            Assert.Equal("12:00", vm.RuleEndTime);

            vm.SetPresetTimeRange("noon");
            Assert.Equal("12:00", vm.RuleStartTime);
            Assert.Equal("14:00", vm.RuleEndTime);

            vm.SetPresetTimeRange("afternoon");
            Assert.Equal("14:00", vm.RuleStartTime);
            Assert.Equal("18:00", vm.RuleEndTime);

            vm.SetPresetTimeRange("evening");
            Assert.Equal("18:00", vm.RuleStartTime);
            Assert.Equal("22:00", vm.RuleEndTime);

            vm.SetPresetTimeRange("night");
            Assert.Equal("22:00", vm.RuleStartTime);
            Assert.Equal("06:00", vm.RuleEndTime);

            // ========================= 规则模板测试：自定义名称保存、直接复用与删除 =========================
            // 1. 保存当前规则输入为自定义模板
            vm.RuleInsertPosition = 5;
            vm.RulePositionMode = 1;
            vm.RuleStartTime = "14:00";
            vm.RuleEndTime = "18:00";
            vm.RuleAppendText = "【下午工作汇报】";
            vm.NewTemplateCustomName = "下午汇报模板";

            vm.SaveCurrentAsTemplate();

            // 验证模板已持久化并加载到列表中
            var savedTpl = vm.SavedRuleTemplates.FirstOrDefault(t => t.Name == "下午汇报模板");
            Assert.NotNull(savedTpl);
            Assert.Equal(5, savedTpl.InsertPosition);
            Assert.Equal(1, savedTpl.PositionMode);
            Assert.Equal("14:00", savedTpl.StartTime);
            Assert.Equal("18:00", savedTpl.EndTime);
            Assert.Equal("【下午工作汇报】", savedTpl.AppendText);
            Assert.Contains("下午汇报模板", savedTpl.DisplayTitle);

            // 2. 清空当前输入表单后，套用刚才保存的模板直接复用
            vm.RuleInsertPosition = 0;
            vm.RulePositionMode = 0;
            vm.RuleStartTime = "00:00";
            vm.RuleEndTime = "01:00";
            vm.RuleAppendText = string.Empty;

            vm.SelectedRuleTemplate = savedTpl;
            vm.ApplySelectedTemplate();

            Assert.Equal(5, vm.RuleInsertPosition);
            Assert.Equal(1, vm.RulePositionMode);
            Assert.Equal("14:00", vm.RuleStartTime);
            Assert.Equal("18:00", vm.RuleEndTime);
            Assert.Equal("【下午工作汇报】", vm.RuleAppendText);

            // 3. 将现有时段规则直接一键另存为模板
            var existingRule = vm.CurrentPhraseRules[0];
            vm.SaveRuleAsTemplate(existingRule);
            Assert.Contains(vm.SavedRuleTemplates, t => t.AppendText == existingRule.AppendText);

            // 4. 删除指定模板
            int countBefore = vm.SavedRuleTemplates.Count;
            vm.DeleteRuleTemplate(savedTpl);
            Assert.Equal(countBefore - 1, vm.SavedRuleTemplates.Count);
            Assert.DoesNotContain(vm.SavedRuleTemplates, t => t.Id == savedTpl.Id);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                try { Directory.Delete(tempDir, true); } catch { }
            }
        }
    }

    [Fact]
    public void PhraseRuleTemplate_ModelAndSerialization_WorksCorrectly()
    {
        var template = new PhraseRuleTemplate
        {
            Name = "早间问候模板",
            InsertPosition = 2,
            PositionMode = 1,
            StartTime = "06:00",
            EndTime = "10:00",
            AppendText = "【早安】"
        };

        Assert.Contains("早间问候模板", template.DisplayTitle);
        Assert.Contains("06:00~10:00", template.SummaryText);

        var rule = template.ToRule();
        Assert.True(rule.IsEnabled);
        Assert.Equal(2, rule.InsertPosition);
        Assert.Equal(1, rule.PositionMode);
        Assert.Equal("06:00", rule.StartTime);
        Assert.Equal("10:00", rule.EndTime);
        Assert.Equal("【早安】", rule.AppendText);

        var fromRule = PhraseRuleTemplate.FromRule(rule, "来自规则的模板");
        Assert.Equal("来自规则的模板", fromRule.Name);
        Assert.Equal(rule.InsertPosition, fromRule.InsertPosition);
        Assert.Equal(rule.AppendText, fromRule.AppendText);

        var list = new List<PhraseRuleTemplate> { template, fromRule };
        string json = System.Text.Json.JsonSerializer.Serialize(list, PhraseRuleJsonContext.Default.ListPhraseRuleTemplate);
        Assert.False(string.IsNullOrEmpty(json));
        var deserialized = System.Text.Json.JsonSerializer.Deserialize(json, PhraseRuleJsonContext.Default.ListPhraseRuleTemplate);
        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.Count);
        Assert.Equal("早间问候模板", deserialized[0].Name);
        Assert.Equal("【早安】", deserialized[0].AppendText);
    }

    [Fact]
    public void RustCoreNative_PhraseRulesAndTemplatesTables_WorkDirectly()
    {
        // 验证原生 Rust muclip_core.dll 新导出的 SQLite 独立表操作 (phrase_rules 与 phrase_rule_templates)
        using var core = MuClipCore.CreateInMemory();

        // 0. 先添加常用语以满足外键约束 (phrase_rules.phrase_id -> phrases.id)
        long phraseId = core.AddPhrase("测试常用语内容");
        Assert.True(phraseId > 0);

        // 1. 添加时段规则到 phrase_rules 表
        var rule = new PhraseConditionalRule
        {
            PhraseId = phraseId,
            IsEnabled = true,
            InsertPosition = 3,
            PositionMode = 1,
            StartTime = "09:00",
            EndTime = "18:00",
            AppendText = " [工作时间]"
        };

        string? ruleId = core.AddPhraseRule(rule);
        Assert.False(string.IsNullOrEmpty(ruleId));

        // 2. 查询 phrase_rules 表
        var rules = core.QueryPhraseRules(phraseId);
        Assert.Single(rules);
        Assert.Equal(phraseId, rules[0].PhraseId);
        Assert.Equal("09:00", rules[0].StartTime);
        Assert.Equal("18:00", rules[0].EndTime);
        Assert.Equal(" [工作时间]", rules[0].AppendText);

        // 3. 删除规则
        bool deletedRule = core.DeletePhraseRule(ruleId!);
        Assert.True(deletedRule);
        Assert.Empty(core.QueryPhraseRules(phraseId));

        // 3.1 再次添加规则，测试外键级联删除 (ON DELETE CASCADE)
        string? ruleId2 = core.AddPhraseRule(rule);
        Assert.False(string.IsNullOrEmpty(ruleId2));
        Assert.Single(core.QueryPhraseRules(phraseId));
        core.DeletePhrase(phraseId); // 删除常用语
        Assert.Empty(core.QueryPhraseRules(phraseId)); // 关联的 phrase_rules 被 SQLite 级联自动删除！

        // 4. 保存模板到 phrase_rule_templates 表
        var tpl = new PhraseRuleTemplate
        {
            Name = "原生模板测试",
            InsertPosition = 0,
            PositionMode = 0,
            StartTime = "07:00",
            EndTime = "11:00",
            AppendText = "【早安】"
        };

        string? tplId = core.SaveRuleTemplate(tpl);
        Assert.False(string.IsNullOrEmpty(tplId));

        // 5. 查询 phrase_rule_templates 表
        var tpls = core.QueryRuleTemplates();
        Assert.Contains(tpls, t => t.Name == "原生模板测试" && t.AppendText == "【早安】");

        // 6. 删除模板
        bool deletedTpl = core.DeleteRuleTemplate(tplId!);
        Assert.True(deletedTpl);
        var tplsAfter = core.QueryRuleTemplates();
        Assert.DoesNotContain(tplsAfter, t => t.Id == tplId);
    }
}


