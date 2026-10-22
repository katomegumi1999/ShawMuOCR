using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MuClip.Interop;
using MuClip.Models;

namespace MuClip.Services;

public partial class MuClipService
{
    public IReadOnlyList<MuClipPhraseItem> Phrases
    {
        get
        {
            lock (_lock)
            {
                return _phrases.ToList();
            }
        }
    }

    public event Action? PhrasesChanged;

    public void RefreshPhrases()
    {
        if (_core == null) return;
        var records = _core.QueryPhrases();
        lock (_lock)
        {
            _phrases.Clear();
            foreach (var rec in records)
            {
                _phrases.Add(MuClipPhraseItem.FromRecord(rec));
            }
        }

        PhrasesChanged?.Invoke();
    }

    public List<MuClipPhraseItem> SearchPhrases(string query)
    {
        if (_core == null) return new List<MuClipPhraseItem>();
        if (string.IsNullOrWhiteSpace(query))
        {
            return Phrases.ToList();
        }
        var records = _core.SearchPhrases(query);
        return records.Select(MuClipPhraseItem.FromRecord).ToList();
    }

    public long AddPhrase(string content)
    {
        if (_core == null || string.IsNullOrWhiteSpace(content)) return -1;
        long id = _core.AddPhrase(content);
        if (id > 0)
        {
            RefreshPhrases();
        }
        return id;
    }

    public bool EditPhrase(long id, string newContent)
    {
        if (_core == null || string.IsNullOrWhiteSpace(newContent)) return false;
        bool ok = _core.EditPhrase(id, newContent);
        if (ok)
        {
            RefreshPhrases();
        }
        return ok;
    }

    public bool DeletePhrase(long id)
    {
        if (_core == null) return false;
        bool ok = _core.DeletePhrase(id);
        if (ok)
        {
            RemovePhraseRuleInternal(id);
            RefreshPhrases();
        }
        return ok;
    }

    public void UsePhrase(long id, string content)
    {
        if (_core == null) return;
        _core.UpdatePhraseUsed(id);
        string finalContent = GetProcessedPhraseContent(id, content);
        CopyToSystemClipboard(finalContent);
        RefreshPhrases();
    }

    public void ReorderPhrases(IReadOnlyList<long> orderedIds)
    {
        if (orderedIds == null || orderedIds.Count == 0) return;
        lock (_lock)
        {
            var dict = _phrases.ToDictionary(p => p.Id);
            var seen = new HashSet<long>();
            var newPhrases = new List<MuClipPhraseItem>();
            foreach (var id in orderedIds)
            {
                if (dict.TryGetValue(id, out var phrase))
                {
                    newPhrases.Add(phrase);
                    seen.Add(id);
                }
            }
            foreach (var phrase in _phrases)
            {
                if (!seen.Contains(phrase.Id))
                {
                    newPhrases.Add(phrase);
                }
            }
            _phrases.Clear();
            _phrases.AddRange(newPhrases);
        }

        // 核心更新：可根据开关选择调用 Rust Core 原生事务接口，或回退到 WinSqliteInterop
        if (UseNativeCoreReorder && _core != null)
        {
            try
            {
                _core.ReorderPhrases(orderedIds);
            }
            catch { }
        }
        else if (!string.IsNullOrEmpty(_dbPath) && File.Exists(_dbPath))
        {
            try
            {
                WinSqliteInterop.UpdatePhraseSortOrders(_dbPath, orderedIds);
            }
            catch { }
        }

        PhrasesChanged?.Invoke();
    }

    public IReadOnlyList<PhraseConditionalRule> GetPhraseRules(long phraseId)
    {
        lock (_lock)
        {
            if (_phraseRules.TryGetValue(phraseId, out var list))
            {
                return list.Select(r => r.Clone()).ToList();
            }
            return Array.Empty<PhraseConditionalRule>();
        }
    }

    public void SetPhraseRules(long phraseId, IEnumerable<PhraseConditionalRule> rules)
    {
        lock (_lock)
        {
            var list = rules?.Where(r => r != null).Select(r => r.Clone()).ToList() ?? new List<PhraseConditionalRule>();

            // 同步到底层 SQLite phrase_rules 独立关系表
            if (_core != null)
            {
                try
                {
                    var oldRules = _core.QueryPhraseRules(phraseId);
                    var newIds = new HashSet<string>(list.Select(r => r.Id));
                    foreach (var old in oldRules)
                    {
                        if (!newIds.Contains(old.Id))
                        {
                            _core.DeletePhraseRule(old.Id);
                        }
                    }
                    foreach (var r in list)
                    {
                        r.PhraseId = phraseId;
                        _core.AddPhraseRule(r);
                    }
                }
                catch { }
            }

            if (list.Count == 0)
            {
                _phraseRules.Remove(phraseId);
            }
            else
            {
                _phraseRules[phraseId] = list;
            }
            SavePhraseRules();
        }
    }

    public PhraseConditionalRule? GetPhraseRule(long phraseId)
    {
        lock (_lock)
        {
            if (_phraseRules.TryGetValue(phraseId, out var list) && list.Count > 0)
            {
                return list[0].Clone();
            }
            return null;
        }
    }

    public void SetPhraseRule(long phraseId, PhraseConditionalRule? rule)
    {
        if (rule == null || (!rule.IsEnabled && string.IsNullOrEmpty(rule.AppendText)))
        {
            SetPhraseRules(phraseId, Array.Empty<PhraseConditionalRule>());
        }
        else
        {
            SetPhraseRules(phraseId, new[] { rule });
        }
    }

    private void RemovePhraseRuleInternal(long phraseId)
    {
        lock (_lock)
        {
            if (_core != null)
            {
                try
                {
                    var oldRules = _core.QueryPhraseRules(phraseId);
                    foreach (var old in oldRules)
                    {
                        _core.DeletePhraseRule(old.Id);
                    }
                }
                catch { }
            }
            if (_phraseRules.Remove(phraseId))
            {
                SavePhraseRules();
            }
        }
    }

    public string GetProcessedPhraseContent(long phraseId, string rawContent)
    {
        if (string.IsNullOrEmpty(rawContent)) return rawContent;
        List<PhraseConditionalRule>? rules;
        lock (_lock)
        {
            _phraseRules.TryGetValue(phraseId, out rules);
        }
        if (rules != null && rules.Count > 0)
        {
            return PhraseConditionalRule.ApplyRules(rawContent, rules);
        }
        return rawContent;
    }

    private void LoadPhraseRules()
    {
        try
        {
            if (_core != null)
            {
                var nativeRules = _core.QueryPhraseRules(0);
                lock (_lock)
                {
                    _phraseRules.Clear();
                    foreach (var rule in nativeRules)
                    {
                        if (!_phraseRules.TryGetValue(rule.PhraseId, out var list))
                        {
                            list = new List<PhraseConditionalRule>();
                            _phraseRules[rule.PhraseId] = list;
                        }
                        list.Add(rule);
                    }
                }
            }
        }
        catch { }
    }

    private void SavePhraseRules()
    {
        try
        {
            var dict = new Dictionary<string, List<PhraseConditionalRule>>();
            lock (_lock)
            {
                foreach (var (k, v) in _phraseRules)
                {
                    dict[k.ToString()] = v;
                }
            }
            string json = System.Text.Json.JsonSerializer.Serialize(dict, PhraseRuleJsonContext.Default.DictionaryStringListPhraseConditionalRule);
            _core?.SetConfig("phrase_conditional_rules", json);
        }
        catch { }
    }

    public IReadOnlyList<PhraseRuleTemplate> GetRuleTemplates()
    {
        lock (_lock)
        {
            if (_ruleTemplates.Count == 0)
            {
                EnsureDefaultRuleTemplates();
            }
            return _ruleTemplates.Select(t => t.Clone()).ToList();
        }
    }

    public void SaveRuleTemplate(PhraseRuleTemplate template)
    {
        if (template == null) return;
        lock (_lock)
        {
            int idx = _ruleTemplates.FindIndex(t => t.Id == template.Id);
            if (idx >= 0)
            {
                _ruleTemplates[idx] = template.Clone();
            }
            else
            {
                _ruleTemplates.Add(template.Clone());
            }

            try
            {
                _core?.SaveRuleTemplate(template);
            }
            catch { }

            SaveRuleTemplates();
        }
    }

    public bool DeleteRuleTemplate(string templateId)
    {
        if (string.IsNullOrEmpty(templateId)) return false;
        lock (_lock)
        {
            try
            {
                _core?.DeleteRuleTemplate(templateId);
            }
            catch { }

            int removed = _ruleTemplates.RemoveAll(t => t.Id == templateId);
            if (removed > 0)
            {
                SaveRuleTemplates();
                return true;
            }
            return false;
        }
    }

    private void EnsureDefaultRuleTemplates()
    {
        if (_ruleTemplates.Count == 0)
        {
            _ruleTemplates.Add(new PhraseRuleTemplate
            {
                Name = "早间问候",
                StartTime = "06:00",
                EndTime = "10:00",
                PositionMode = 1,
                InsertPosition = 9999,
                AppendText = "【早上好！】"
            });
            _ruleTemplates.Add(new PhraseRuleTemplate
            {
                Name = "夜间非工作时段",
                StartTime = "22:00",
                EndTime = "06:00",
                PositionMode = 1,
                InsertPosition = 9999,
                AppendText = "【夜间离线留言】"
            });
        }
    }

    private void LoadRuleTemplates()
    {
        try
        {
            if (_core != null)
            {
                var nativeTemplates = _core.QueryRuleTemplates();
                if (nativeTemplates.Count > 0)
                {
                    lock (_lock)
                    {
                        _ruleTemplates.Clear();
                        _ruleTemplates.AddRange(nativeTemplates);
                    }
                    return;
                }
            }
        }
        catch { }

        lock (_lock)
        {
            EnsureDefaultRuleTemplates();
            if (_core != null)
            {
                foreach (var tpl in _ruleTemplates)
                {
                    _core.SaveRuleTemplate(tpl);
                }
            }
        }
    }

    private void SaveRuleTemplates()
    {
        try
        {
            List<PhraseRuleTemplate> list;
            lock (_lock)
            {
                list = _ruleTemplates.Select(t => t.Clone()).ToList();
            }
            string json = System.Text.Json.JsonSerializer.Serialize(list, PhraseRuleJsonContext.Default.ListPhraseRuleTemplate);
            _core?.SetConfig("phrase_rule_templates", json);
        }
        catch { }
    }
}
