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
                new("n.", "意外发现珍宝的运道；机缘巧合；小确幸", "The occurrence and development of events by chance in a happy or beneficial way.",
                    GrammarLabel: "通常不可数", GrammarPattern: "by serendipity 表示某事因机缘巧合而发生",
                    Example: new("I found the letter by serendipity while sorting old maps.", "我整理旧地图时碰巧发现了那封信。", "by serendipity"))
            ],
            WordFamily =
            [
                new("形容词", "serendipitous"),
                new("副词", "serendipitously")
            ],
            Collocations =
            [
                new("pure serendipity", "纯属巧合 / 意外之喜", Example:
                    new("Finding her sketchbook in that tiny shop was pure serendipity.", "能在那家小店找到她的速写本，纯属意外之喜。", "pure serendipity")),
                new("serendipitous discovery", "意外的发现", Example:
                    new("A broken sensor led to a serendipitous discovery in the lab.", "一个坏掉的传感器让实验室有了意外发现。", "serendipitous discovery")),
                new("a moment of serendipity", "机缘巧合的瞬间", Example:
                    new("Two strangers reaching for the same book shared a moment of serendipity.", "两个陌生人同时伸手去拿同一本书，有了一场奇遇。", "a moment of serendipity"))
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
                new("adj.", "无所不在的；随处可见的；普遍存在的", "Present, appearing, or found everywhere.",
                    GrammarLabel: "可作定语或表语", GrammarPattern: "be ubiquitous in + 地点或领域",
                    Example: new("Cargo bikes are ubiquitous in this neighborhood.", "这个街区到处都能见到载货自行车。", "are ubiquitous in"))
            ],
            WordFamily =
            [
                new("名词", "ubiquity"),
                new("副词", "ubiquitously")
            ],
            Collocations =
            [
                new("become ubiquitous", "变得无处不在 / 极其普及", Example:
                    new("Contactless readers have become ubiquitous on city buses.", "市内公交车上如今到处都能见到非接触式读卡器。", "become ubiquitous")),
                new("ubiquitous computing", "普适计算", Example:
                    new("The lab studies how ubiquitous computing can help people live independently.", "这个实验室研究普适计算如何帮助人们独立生活。", "ubiquitous computing")),
                new("ubiquitous presence", "随处可见的身影", Example:
                    new("The fox is a ubiquitous presence in local folktales.", "在当地的民间故事里，狐狸的身影随处可见。", "ubiquitous presence"))
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
        ["coffee"] = new DictionaryEntryResult
        {
            SourceText = "coffee", PrimaryTranslation = "咖啡；咖啡豆或咖啡粉",
            DetectedSourceLanguage = "en", TargetLanguage = "zh-Hans", ItemType = VocabularyItemType.Word,
            UkPhonetic = "/ˈkɒf.i/", UsPhonetic = "/ˈkɑː.fi/",
            Senses =
            [
                new("n.", "咖啡（饮品）", GrammarLabel: "随计量方式变化")
                {
                    UsageCases =
                    [
                        new("泛指饮品或不说明杯数", "不可数", "some coffee", "一些咖啡"),
                        new("点单时按杯计算", "可数", "two coffees to go", "两杯咖啡，打包带走")
                    ]
                },
                new("n.", "用来冲泡的咖啡豆或咖啡粉", GrammarLabel: "通常不可数",
                    Example: new("Store ground coffee in a sealed jar.", "把咖啡粉放在密封罐里。", "ground coffee"))
            ]
        },
        ["hard"] = new DictionaryEntryResult
        {
            SourceText = "hard", PrimaryTranslation = "硬的；困难的；努力地；用力地",
            DetectedSourceLanguage = "en", TargetLanguage = "zh-Hans", ItemType = VocabularyItemType.Word,
            UkPhonetic = "/hɑːd/", UsPhonetic = "/hɑːrd/",
            Senses =
            [
                new("adj.", "坚硬的，难以按压或弯折的",
                    Example: new("The clay is too hard to shape.", "这块黏土太硬了，捏不动。", "hard")),
                new("adj.", "困难的，费力的", GrammarPattern: "形容做某件事有困难时可用 hard to + 动词原形。",
                    Example: new("Her handwriting is hard to read.", "她的字迹很难辨认。", "hard to read")),
                new("adv.", "努力地", GrammarPattern: "常放在 work、study、try 等动词后，说明投入的努力。",
                    Example: new("Sam works hard on weekends.", "萨姆在周末工作很卖力。", "works hard")),
                new("adv.", "用力地",
                    Example: new("Press hard on the lid.", "用力按压盖子。", "Press hard"))
            ],
            Inflections =
            [
                new("比较级", "harder", "adj."), new("最高级", "hardest", "adj."),
                new("比较级", "harder", "adv."), new("最高级", "hardest", "adv.")
            ],
            WordFamily =
            [
                new("几乎不", "hardly", "adv.", "confusable",
                    "works hard 说的是做事卖力。把 hardly 放到 works 前，意思就成了萨姆周末几乎不工作。", "Sam hardly works on weekends.")
            ]
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
            UsageContrasts =
            [
                new("an ephemeral display", "强调展现的事物转瞬即逝，例如用冰或烟雾构成的景象。",
                    "a temporary display", "强调展览只在一段时期内设置，展品本身可以长久保存。")
            ],
            Senses =
            [
                new("adj.", "转瞬即逝的；短暂的；临时的", "Lasting for a very short time.",
                    GrammarLabel: "可分级", GrammarPattern: "ephemeral + 名词，强调持续时间短",
                    Example: new("The sculptor makes ephemeral art from melting ice.", "这位雕塑家用正在融化的冰创作转瞬即逝的艺术品。", "ephemeral art"))
            ],
            Inflections = [new("比较级", "more ephemeral", "adj."), new("最高级", "most ephemeral", "adj.")],
            WordFamily =
            [
                new("副词", "ephemerally"),
                new("名词", "ephemerality")
            ],
            Collocations =
            [
                new("ephemeral nature", "短暂的本质", Example:
                    new("The poem captures the ephemeral nature of spring.", "这首诗写出了春光转瞬即逝的特点。", "ephemeral nature")),
                new("ephemeral environment", "临时环境", Example:
                    new("The test runs in an ephemeral environment that disappears afterward.", "测试在一个临时环境中运行，结束后该环境就会被移除。", "ephemeral environment")),
                new("ephemeral fame", "过眼云烟的名声", Example:
                    new("Her story explores the ephemeral fame of a teenage singer.", "她的故事探讨一位少年歌手昙花一现的名声。", "ephemeral fame"))
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
