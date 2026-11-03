using AiTranslate.Ai;
using AiTranslate.Core;

namespace AiTranslate.Vocabulary;

/// <summary>
/// Seeds default Vocabulary Notebooks and high-value initial entries for out-of-the-box experience.
/// </summary>
internal static class VocabularySeeder
{
    public static VocabularyStoreSnapshot CreateDefaultSeededStore()
    {
        var now = DateTimeOffset.UtcNow;
        var notebooks = new List<VocabularyNotebook>
        {
            new()
            {
                Id = "default",
                Name = "日常阅读与划词生词本",
                Description = "通过划词翻译、剪贴板或词典查询随时收录的高频生词与短语",
                IconName = "book-open-page-variant",
                AccentColorHex = "#3B82F6",
                IsSystemDefault = true,
                CreatedAt = now.AddDays(-10)
            },
            new()
            {
                Id = "tech-ai",
                Name = "AI 与软件工程核心术语",
                Description = "阅读论文、GitHub 源码与技术文档时积累的专业词汇与表达",
                IconName = "code-braces",
                AccentColorHex = "#10B981",
                CreatedAt = now.AddDays(-8)
            },
            new()
            {
                Id = "academic-writing",
                Name = "雅思 / GRE 学术高分表达",
                Description = "写作与外刊精读中的高阶词汇、神仙搭配与金句摘录",
                IconName = "school",
                AccentColorHex = "#8B5CF6",
                CreatedAt = now.AddDays(-5)
            },
            new()
            {
                Id = "sentences",
                Name = "外刊长难句与地道金句库",
                Description = "截图翻译与阅读中收藏的精彩长难句及 AI 语法拆解",
                IconName = "format-quote-close",
                AccentColorHex = "#F59E0B",
                CreatedAt = now.AddDays(-3)
            }
        };

        var entries = new List<VocabularyEntry>();
        // Extra lookup examples should be collected by the reader before they enter the review queue.
        foreach (var item in BuiltInShowcaseLexicon.All.Where(e => e.SourceText is "serendipity" or "ephemeral" or "ubiquitous"))
        {
            var nbId = item.SourceText == "ephemeral" ? "tech-ai"
                : item.SourceText == "ubiquitous" ? "academic-writing"
                : "default";
            entries.Add(new VocabularyEntry
            {
                Id = $"voc_seed_{item.SourceText}",
                Headword = item.SourceText,
                Translation = item.PrimaryTranslation,
                NotebookId = nbId,
                ItemType = item.ItemType,
                UkPhonetic = item.UkPhonetic ?? "",
                UsPhonetic = item.UsPhonetic ?? "",
                Senses = item.Senses,
                Inflections = item.Inflections,
                WordFamily = item.WordFamily,
                UsageContrasts = item.UsageContrasts,
                Collocations = item.Collocations,
                Examples = item.Examples,
                Synonyms = item.Synonyms,
                Antonyms = item.Antonyms,
                EtymologyAndRoot = item.EtymologyAndRoot ?? "",
                MemoryMnemonic = item.MemoryMnemonic ?? "",
                GrammarOrUsageNote = item.GrammarBreakdown ?? "",
                ContextSentence = item.Examples.FirstOrDefault()?.SourceSentence ?? "",
                ContextTranslation = item.Examples.FirstOrDefault()?.TranslatedSentence ?? "",
                CaptureSource = TranslationTriggerSource.SelectionPopup,
                SourceTitle = "The Economist / ArXiv Paper",
                Tags = item.SuggestedTags,
                IsStarred = item.SourceText == "serendipity",
                LookupCount = 3,
                Stage = item.SourceText == "ubiquitous" ? MemoryStage.Reviewing : MemoryStage.Learning,
                MasteryLevel = item.SourceText == "ubiquitous" ? 3 : 2,
                StabilityDays = item.SourceText == "ubiquitous" ? 6.5 : 1.5,
                Difficulty = 4.8,
                TotalReviews = 2,
                CorrectStreak = 2,
                CreatedAt = now.AddDays(-3),
                UpdatedAt = now.AddHours(-6),
                LastReviewedAt = now.AddDays(-2),
                NextReviewAt = now.AddMinutes(-15)
            });
        }

        entries.Add(new VocabularyEntry
        {
            Id = "voc_seed_paradigm_shift",
            Headword = "paradigm shift",
            Translation = "n. 范式转移；思维模式或核心架构的根本性变革",
            NotebookId = "tech-ai",
            ItemType = VocabularyItemType.Phrase,
            UkPhonetic = "/ˈpær.ə.daɪm ʃɪft/",
            UsPhonetic = "/ˈper.ə.daɪm ʃɪft/",
            Senses = [new("phrase", "范式转移；根本性变革", "A fundamental change in approach or underlying assumptions.")],
            Collocations = [new("undergo a paradigm shift", "经历一场范式转移"), new("trigger a paradigm shift", "引发范式变革")],
            Examples =
            [
                new("Generative AI represents a fundamental paradigm shift in human-computer interaction.", "生成式人工智能代表了人机交互领域的一场根本性范式转移。", "paradigm shift")
            ],
            EtymologyAndRoot = "由科学哲学家托马斯·库恩（Thomas Kuhn）在《科学革命的结构》中提出，指科学共同体基础理论框架的跃迁。",
            MemoryMnemonic = "paradigm（范式/典范，注意 g 不发音）+ shift（转移）→ 从旧时代逻辑切换到新纪元标准。",
            ContextSentence = "Generative AI represents a fundamental paradigm shift in human-computer interaction.",
            ContextTranslation = "生成式人工智能代表了人机交互领域的一场根本性范式转移。",
            CaptureSource = TranslationTriggerSource.ScreenshotOcr,
            SourceTitle = "截图原位翻译 · 论文阅读",
            Tags = ["学术核心", "AI架构", "高频短语"],
            IsStarred = true,
            LookupCount = 4,
            Stage = MemoryStage.New,
            MasteryLevel = 1,
            Difficulty = 4.5,
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddHours(-2),
            NextReviewAt = now
        });

        entries.Add(new VocabularyEntry
        {
            Id = "voc_seed_sentence_1",
            Headword = "Premature optimization is the root of all evil in software engineering.",
            Translation = "过早的优化是软件工程中的万恶之源。",
            NotebookId = "sentences",
            ItemType = VocabularyItemType.Sentence,
            Senses =
            [
                new("重点词汇", "premature /ˈprem.ə.tʃər/ adj. 过早的，不成熟的", "Occurring or done before the usual or proper time."),
                new("核心名言", "the root of all evil 万恶之源", "The fundamental cause of problems.")
            ],
            Examples =
            [
                new("As Donald Knuth famously observed, premature optimization is the root of all evil.", "正如高德纳那句著名论断所言：过早优化是万恶之源。", "premature optimization")
            ],
            EtymologyAndRoot = "计算机泰斗 Donald Knuth（高德纳）在《计算机程序设计艺术》中的经典箴言。",
            MemoryMnemonic = "先保证架构清晰可维护（Make it work, make it right），再针对真实瓶颈优化（make it fast）。",
            GrammarOrUsageNote = "主系表结构：Premature optimization (主语) + is (系动词) + the root of all evil (表语) + in software engineering (地点/领域状语)。",
            ContextSentence = "Premature optimization is the root of all evil in software engineering.",
            ContextTranslation = "过早的优化是软件工程中的万恶之源。",
            CaptureSource = TranslationTriggerSource.ClipboardMonitor,
            SourceTitle = "剪贴板监听摘录",
            Tags = ["金句摘录", "软件工程", "名言"],
            IsStarred = false,
            LookupCount = 2,
            Stage = MemoryStage.Reviewing,
            MasteryLevel = 4,
            StabilityDays = 14.0,
            Difficulty = 3.5,
            TotalReviews = 3,
            CorrectStreak = 3,
            CreatedAt = now.AddDays(-5),
            UpdatedAt = now.AddDays(-1),
            LastReviewedAt = now.AddDays(-1),
            NextReviewAt = now.AddDays(13)
        });

        return new VocabularyStoreSnapshot
        {
            Notebooks = notebooks,
            Entries = entries,
            ReviewLogs = []
        };
    }
}
