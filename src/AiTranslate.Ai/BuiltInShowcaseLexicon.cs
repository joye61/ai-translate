using AiTranslate.Core;

namespace AiTranslate.Ai;

/// <summary>
/// Built-in high-value dictionary entries so first-time users can immediately experience the full power
/// of the dictionary and vocabulary notebook even before entering their personal API Key.
/// </summary>
public static class BuiltInShowcaseLexicon
{
    private static readonly Dictionary<string, DictionaryEntryResult> Entries = new(StringComparer.OrdinalIgnoreCase)
    {
        ["serendipity"] = new DictionaryEntryResult
        {
            SourceText = "serendipity",
            PrimaryTranslation = "n. 意外发现珍奇事物的本领；机缘巧合；不期而遇的美好",
            DetectedSourceLanguage = "en",
            TargetLanguage = "zh-Hans",
            ItemType = VocabularyItemType.Word,
            UkPhonetic = "/ˌser.ənˈdɪp.ə.ti/",
            UsPhonetic = "/ˌser.ənˈdɪp.ə.t̬i/",
            ContextMeaning = "指在寻找某物时，意外发现更有价值或更令人惊喜的事物的机缘与敏锐洞察力。",
            EtymologyAndRoot = "源自波斯童话《锡兰三王子》(The Three Princes of Serendip)，古称斯里兰卡为 Serendip，三位王子在旅途中总能凭智慧与巧合意外发现未曾寻找的珍宝。",
            MemoryMnemonic = "拆分联想：seren (联想到 serene 宁静晴朗) + dip (浸入) + ity (名词后缀) → 在宁静的心态中浸入生活，不期而遇意外的惊喜。",
            GrammarBreakdown = "不可数抽象名词，常与 by 搭配（by pure serendipity 纯属机缘巧合），形容词形式为 serendipitous。",
            UsageNotes = "高频高分书面与学术词汇，在科技史（如青霉素的发现）与文学作品中极为常见。",
            Senses =
            [
                new("n.", "意外发现珍宝的运道；机缘巧合；小确幸", "The occurrence and development of events by chance in a happy or beneficial way.")
            ],
            Inflections =
            [
                new("形容词", "serendipitous"),
                new("副词", "serendipitously")
            ],
            Collocations =
            [
                new("pure serendipity", "纯属巧合 / 意外之喜"),
                new("serendipitous discovery", "意外的重大科学发现"),
                new("a moment of serendipity", "机缘巧合的瞬间")
            ],
            Synonyms = ["chance", "fluke", "fortuity", "blessing", "happy accident"],
            Antonyms = ["misfortune", "calamity", "plan", "design"],
            Examples =
            [
                new("Many breakthroughs in medicine, including penicillin, were the result of pure serendipity.", "医学上的许多重大突破（包括青霉素），都是纯属机缘巧合的意外发现。", "serendipity"),
                new("Meeting my co-founder at that delayed flight gate was a stroke of serendipity.", "在延误航班登机口遇到我的联合创始人，真是一场奇妙的机缘巧合。", "serendipity")
            ],
            SuggestedTags = ["IELTS", "GRE", "高频核心", "科研写作"]
        },
        ["ubiquitous"] = new DictionaryEntryResult
        {
            SourceText = "ubiquitous",
            PrimaryTranslation = "adj. 无所不在的；十分普遍的；随处可见的",
            DetectedSourceLanguage = "en",
            TargetLanguage = "zh-Hans",
            ItemType = VocabularyItemType.Word,
            UkPhonetic = "/juːˈbɪk.wɪ.təs/",
            UsPhonetic = "/juːˈbɪk.wə.t̬əs/",
            ContextMeaning = "形容某事物在现代社会或特定环境中普遍存在，几乎同时出现在所有地方。",
            EtymologyAndRoot = "源自拉丁语 ubique（意为 everywhere 到处，ubi 'where' + que 'any/ever'）+ -ous（形容词后缀）。",
            MemoryMnemonic = "词根助记：ubique（到处）+ ous → 到处都有的 → 无所不在的。常用来替代太简单的 everywhere / common。",
            GrammarBreakdown = "作定语或表语，名词形式为 ubiquity（普遍存在；无处不在）。",
            UsageNotes = "雅思/托福写作大作文绝对王牌词，用于描述 AI、智能手机、塑料污染等现代事物的普及。",
            Senses =
            [
                new("adj.", "无所不在的；随处可见的；普遍存在的", "Present, appearing, or found everywhere.")
            ],
            Inflections =
            [
                new("名词", "ubiquity"),
                new("副词", "ubiquitously")
            ],
            Collocations =
            [
                new("become ubiquitous", "变得无处不在 / 极其普及"),
                new("ubiquitous computing", "普适计算（无处不在的计算）"),
                new("ubiquitous presence", "随处可见的身影")
            ],
            Synonyms = ["omnipresent", "pervasive", "universal", "all-pervasive"],
            Antonyms = ["rare", "scarce", "infrequent"],
            Examples =
            [
                new("Large language models have rapidly become ubiquitous in modern software engineering workflows.", "大语言模型已在现代软件工程工作流中迅速变得无处不在。", "ubiquitous"),
                new("Coffee shops are ubiquitous in this vibrant metropolitan district.", "在这个充满活力的都市街区，咖啡馆随处可见。", "ubiquitous")
            ],
            SuggestedTags = ["TOEFL", "IELTS", "GRE", "写作高分"]
        },
        ["ephemeral"] = new DictionaryEntryResult
        {
            SourceText = "ephemeral",
            PrimaryTranslation = "adj. 短暂的；转瞬即逝的；朝生暮死的",
            DetectedSourceLanguage = "en",
            TargetLanguage = "zh-Hans",
            ItemType = VocabularyItemType.Word,
            UkPhonetic = "/ɪˈfem.ər.əl/",
            UsPhonetic = "/ɪˈfem.ər.əl/",
            ContextMeaning = "强调事物持续时间极短（如互联网潮流、露水、短暂的艺术或临时容器环境）。",
            EtymologyAndRoot = "epi- (在…之上 / 持续) + hemera (希腊语：一天 day) + -al → 只持续一天的 → 转瞬即逝的。",
            MemoryMnemonic = "在云原生与计算机领域也极为常用：ephemeral storage（临时存储）、ephemeral port（临时端口）。",
            GrammarBreakdown = "常修饰 nature, fame, beauty, storage, environment。",
            UsageNotes = "既可用于文学抒情（青春、名利转瞬即逝），也可用于软件架构（无状态临时节点）。",
            Senses =
            [
                new("adj.", "转瞬即逝的；短暂的；临时的", "Lasting for a very short time.")
            ],
            Inflections =
            [
                new("副词", "ephemerally"),
                new("名词", "ephemerality")
            ],
            Collocations =
            [
                new("ephemeral nature", "短暂的本质"),
                new("ephemeral environment", "临时环境（用完即毁的测试/沙箱环境）"),
                new("ephemeral fame", "过眼云烟的名声")
            ],
            Synonyms = ["transient", "fleeting", "short-lived", "transitory"],
            Antonyms = ["permanent", "eternal", "perpetual", "enduring"],
            Examples =
            [
                new("Each build job runs inside an isolated, ephemeral container that is destroyed upon completion.", "每个构建任务都在一个隔离的临时容器中运行，完成后即被销毁。", "ephemeral"),
                new("Social media trends are notoriously ephemeral, often fading within a few days.", "社交媒体潮流出了名地转瞬即逝，往往几天内就会消退。", "ephemeral")
            ],
            SuggestedTags = ["GRE", "计算机术语", "架构核心"]
        }
    };

    public static bool TryGet(string query, out DictionaryEntryResult result) =>
        Entries.TryGetValue(query.Trim(), out result!);

    public static IReadOnlyList<DictionaryEntryResult> All => Entries.Values.ToArray();
}
