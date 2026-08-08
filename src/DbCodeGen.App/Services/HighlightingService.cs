using System.Text.RegularExpressions;
using DbCodeGen.Core.Templates;
using ICSharpCode.AvalonEdit.Highlighting;

namespace DbCodeGen.App.Services;

/// <summary>
/// 模板编辑器高亮服务，按模板文件推导出的目标语言构建并缓存代码高亮定义。
/// 每个定义以 Scriban 指令标签 Span 叠加目标语言内置高亮规则构成：模板指令（{{ }} 与 {% %}）以标签色突出，
/// 模板正文按目标语言关键字高亮；未识别语言仅保留 Scriban 标签高亮。
/// </summary>
public sealed class HighlightingService
{
    /// <summary>
    /// 已构建高亮定义的缓存，同一目标语言只构建一次，编辑器切换文件时复用。
    /// </summary>
    private readonly Dictionary<HighlightLanguage, IHighlightingDefinition> _definitionCache = new();

    /// <summary>
    /// 获取指定目标语言的高亮定义，缓存命中直接返回。
    /// </summary>
    /// <param name="language">模板文件推导出的目标语言。</param>
    /// <returns>可直接赋给 TextEditor.SyntaxHighlighting 的高亮定义。</returns>
    public IHighlightingDefinition GetDefinition(HighlightLanguage language)
    {
        if (!_definitionCache.TryGetValue(language, out IHighlightingDefinition? definition))
        {
            definition = BuildDefinition(language);
            _definitionCache[language] = definition;
        }

        return definition;
    }

    /// <summary>
    /// 构建指定目标语言的高亮定义：先叠加 Scriban 标签 Span，再复制目标语言内置规则，不修改共享内置对象。
    /// </summary>
    /// <param name="language">目标语言。</param>
    /// <returns>构建完成的高亮定义。</returns>
    private static IHighlightingDefinition BuildDefinition(HighlightLanguage language)
    {
        var ruleSet = new HighlightingRuleSet { Name = "ScribanTemplate" };
        ruleSet.Spans.Add(CreateScribanSpan("\\{\\{", "\\}\\}"));
        ruleSet.Spans.Add(CreateScribanSpan("\\{%", "%\\}"));

        // 目标语言映射到 AvalonEdit 内置高亮定义，复制其规则与 Span，避免改动全局共享定义
        string? builtinName = language switch
        {
            HighlightLanguage.Java => "Java",
            HighlightLanguage.CSharp => "C#",
            HighlightLanguage.Xml => "XML",
            HighlightLanguage.Sql => "TSQL",
            HighlightLanguage.Json => "Json",
            _ => null
        };

        if (builtinName is not null)
        {
            IHighlightingDefinition? builtin = HighlightingManager.Instance.GetDefinition(builtinName);
            if (builtin is not null)
            {
                foreach (HighlightingRule rule in builtin.MainRuleSet.Rules)
                {
                    ruleSet.Rules.Add(rule);
                }

                foreach (HighlightingSpan span in builtin.MainRuleSet.Spans)
                {
                    ruleSet.Spans.Add(span);
                }
            }
        }

        return new ScribanHighlightingDefinition(language, ruleSet);
    }

    /// <summary>
    /// 创建 Scriban 指令标签的高亮 Span，指定起止表达式并赋标签前景色，标签整体高亮。
    /// </summary>
    /// <param name="startPattern">起始表达式正则。</param>
    /// <param name="endPattern">结束表达式正则。</param>
    /// <returns>覆盖标签文本的高亮 Span。</returns>
    private static HighlightingSpan CreateScribanSpan(string startPattern, string endPattern)
    {
        return new HighlightingSpan
        {
            StartExpression = new Regex(startPattern, RegexOptions.Compiled),
            EndExpression = new Regex(endPattern, RegexOptions.Compiled),
            SpanColor = new HighlightingColor
            {
                Foreground = new SimpleHighlightingBrush(System.Windows.Media.Color.FromRgb(0x00, 0x62, 0x8E))
            },
            SpanColorIncludesStart = true,
            SpanColorIncludesEnd = true
        };
    }

    /// <summary>
    /// 自定义高亮定义实现，仅承载定义名称与主规则集，命名颜色与子规则集暂不提供。
    /// </summary>
    private sealed class ScribanHighlightingDefinition : IHighlightingDefinition
    {
        /// <summary>
        /// 使用目标语言与主规则集构造高亮定义。
        /// </summary>
        /// <param name="language">目标语言。</param>
        /// <param name="mainRuleSet">包含 Scriban 标签与目标语言规则的主规则集。</param>
        public ScribanHighlightingDefinition(HighlightLanguage language, HighlightingRuleSet mainRuleSet)
        {
            Name = $"Scriban.{language}";
            MainRuleSet = mainRuleSet;
        }

        /// <inheritdoc />
        public string Name { get; }

        /// <inheritdoc />
        public HighlightingRuleSet MainRuleSet { get; }

        /// <inheritdoc />
        public IEnumerable<HighlightingColor> NamedHighlightingColors => Array.Empty<HighlightingColor>();

        /// <inheritdoc />
        public IDictionary<string, string> Properties { get; } = new Dictionary<string, string>();

        /// <inheritdoc />
        public HighlightingColor? GetNamedColor(string name)
        {
            return null;
        }

        /// <inheritdoc />
        public HighlightingRuleSet? GetNamedRuleSet(string name)
        {
            return null;
        }
    }
}
