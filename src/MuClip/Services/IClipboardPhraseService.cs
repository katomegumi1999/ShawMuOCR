using System;
using System.Collections.Generic;
using MuClip.Models;

namespace MuClip.Services;

/// <summary>
/// 常用语与规则服务契约
/// 职责：提供常用语 CRUD、拖拽排序、时段与条件规则解析、规则模板库管理
/// </summary>
public interface IClipboardPhraseService
{
    IReadOnlyList<MuClipPhraseItem> Phrases { get; }
    event Action? PhrasesChanged;
    bool UseNativeCoreReorder { get; set; }

    void RefreshPhrases();
    List<MuClipPhraseItem> SearchPhrases(string query);
    long AddPhrase(string content);
    bool EditPhrase(long id, string newContent);
    bool DeletePhrase(long id);
    void UsePhrase(long id, string content);
    void ReorderPhrases(IReadOnlyList<long> orderedIds);
    PhraseConditionalRule? GetPhraseRule(long phraseId);
    void SetPhraseRule(long phraseId, PhraseConditionalRule? rule);
    IReadOnlyList<PhraseConditionalRule> GetPhraseRules(long phraseId);
    void SetPhraseRules(long phraseId, IEnumerable<PhraseConditionalRule> rules);
    IReadOnlyList<PhraseRuleTemplate> GetRuleTemplates();
    void SaveRuleTemplate(PhraseRuleTemplate template);
    bool DeleteRuleTemplate(string templateId);
    string GetProcessedPhraseContent(long phraseId, string rawContent);
}
