using System.Text;
using System.Text.RegularExpressions;
using PulseIsland.Core;

namespace PulseIsland.Translation;

/// <summary>
/// Zero-dependency built-in local phrase & morphological translator (EN <-> RU)
/// used as an instant offline fallback when network sockets are blocked by firewall/sandbox
/// or when no external ONNX model is installed.
/// </summary>
public static class BuiltInLocalTranslator
{
    private static readonly (string Pattern, string Replacement)[] EnToRuPhrases =
    {
        ("show all packets sent and received", "показать все отправленные и полученные пакеты"),
        ("print host interfaces and routes", "вывести интерфейсы хоста и маршруты"),
        ("for debugging", "для отладки"),
        ("append to rather than clobber specified output files", "дописывать, а не перезаписывать указанные выходные файлы"),
        ("rather than clobber", "вместо перезаписи"),
        ("specified output files", "указанные выходные файлы"),
        ("output files", "выходные файлы"),
        ("input files", "входные файлы"),
        ("send using raw ethernet frames or ip packets", "отправлять с использованием сырых кадров Ethernet или IP-пакетов"),
        ("assume that the user is fully privileged", "предполагать, что пользователь имеет полные привилегии"),
        ("assume the user lacks raw socket privileges", "предполагать, что у пользователя нет прав на сырые сокеты"),
        ("print version number", "вывести номер версии"),
        ("print this help summary page", "вывести эту страницу краткой справки"),
        ("see the man page", "смотрите руководство (man page)"),
        ("for more options and examples", "для дополнительных опций и примеров"),
        ("sent and received", "отправленные и полученные"),
        ("rather than", "вместо того чтобы"),
        ("as well as", "а также"),
        ("in order to", "для того чтобы"),
        ("by default", "по умолчанию"),
        ("such as", "такие как"),
        ("at least", "как минимум"),
        ("at most", "не более"),
        ("up to", "вплоть до"),
        ("according to", "согласно"),
        ("depending on", "в зависимости от"),
        ("because of", "из-за"),
        ("instead of", "вместо"),
        ("due to", "вследствие"),
        ("make sure", "убедитесь"),
        ("for example", "например"),
        ("in addition", "кроме того"),
        ("on the screen", "на экране"),
        ("command line", "командная строка"),
        ("operating system", "операционная система"),
        ("ip address", "IP-адрес"),
        ("port scanning", "сканирование портов"),
        ("host discovery", "обнаружение хостов"),
        ("service/version detection", "определение служб и версий"),
        ("os detection", "определение ОС"),
        ("timing and performance", "тайминги и производительность"),
        ("firewall/ids evasion and spoofing", "обход брандмауэра/IDS и спуфинг"),
        ("target specification", "указание целей"),
        ("scan techniques", "методы сканирования")
    };

    private static readonly Dictionary<string, string> EnToRuWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // Pronouns, articles, prepositions, conjunctions
        ["the"] = "",
        ["a"] = "",
        ["an"] = "",
        ["this"] = "этот",
        ["that"] = "что",
        ["these"] = "эти",
        ["those"] = "те",
        ["and"] = "и",
        ["or"] = "или",
        ["but"] = "но",
        ["if"] = "если",
        ["then"] = "тогда",
        ["else"] = "иначе",
        ["when"] = "когда",
        ["where"] = "где",
        ["while"] = "пока",
        ["with"] = "с",
        ["without"] = "без",
        ["from"] = "из",
        ["into"] = "в",
        ["to"] = "в",
        ["for"] = "для",
        ["of"] = "из",
        ["in"] = "в",
        ["on"] = "на",
        ["at"] = "в",
        ["by"] = "по",
        ["about"] = "о",
        ["as"] = "как",
        ["is"] = "является",
        ["are"] = "являются",
        ["was"] = "был",
        ["were"] = "были",
        ["be"] = "быть",
        ["been"] = "был",
        ["have"] = "иметь",
        ["has"] = "имеет",
        ["had"] = "имел",
        ["do"] = "делать",
        ["does"] = "делает",
        ["did"] = "сделал",
        ["not"] = "не",
        ["no"] = "нет",
        ["yes"] = "да",
        ["all"] = "все",
        ["any"] = "любой",
        ["some"] = "некоторые",
        ["each"] = "каждый",
        ["every"] = "каждый",
        ["other"] = "другой",
        ["another"] = "другой",
        ["more"] = "больше",
        ["most"] = "большинство",
        ["less"] = "меньше",
        ["only"] = "только",
        ["also"] = "также",
        ["even"] = "даже",
        ["very"] = "очень",
        ["too"] = "слишком",
        ["can"] = "может",
        ["could"] = "мог бы",
        ["will"] = "будет",
        ["would"] = "бы",
        ["should"] = "следует",
        ["must"] = "должен",
        ["may"] = "может",
        ["might"] = "может быть",
        ["you"] = "вы",
        ["your"] = "ваш",
        ["we"] = "мы",
        ["our"] = "наш",
        ["they"] = "они",
        ["their"] = "их",
        ["it"] = "это",
        ["its"] = "его",
        ["i"] = "я",
        ["my"] = "мой",
        ["he"] = "он",
        ["she"] = "она",
        ["who"] = "кто",
        ["what"] = "что",
        ["which"] = "который",
        ["how"] = "как",
        ["why"] = "почему",

        // Common & technical verbs
        ["show"] = "показать",
        ["shows"] = "показывает",
        ["showing"] = "показывая",
        ["print"] = "вывести",
        ["prints"] = "выводит",
        ["display"] = "отобразить",
        ["displays"] = "отображает",
        ["send"] = "отправить",
        ["sends"] = "отправляет",
        ["sent"] = "отправленные",
        ["receive"] = "получить",
        ["receives"] = "получает",
        ["received"] = "полученные",
        ["append"] = "дописать",
        ["clobber"] = "перезаписать",
        ["overwrite"] = "перезаписать",
        ["specify"] = "указать",
        ["specified"] = "указанные",
        ["use"] = "использовать",
        ["using"] = "используя",
        ["used"] = "используемый",
        ["assume"] = "предполагать",
        ["lack"] = "не иметь",
        ["lacks"] = "не имеет",
        ["run"] = "запустить",
        ["running"] = "запущенный",
        ["start"] = "начать",
        ["stop"] = "остановить",
        ["open"] = "открыть",
        ["close"] = "закрыть",
        ["save"] = "сохранить",
        ["load"] = "загрузить",
        ["download"] = "скачать",
        ["upload"] = "выгрузить",
        ["delete"] = "удалить",
        ["remove"] = "убрать",
        ["create"] = "создать",
        ["update"] = "обновить",
        ["check"] = "проверить",
        ["find"] = "найти",
        ["search"] = "искать",
        ["select"] = "выбрать",
        ["enable"] = "включить",
        ["enabled"] = "включено",
        ["disable"] = "отключить",
        ["disabled"] = "отключено",
        ["set"] = "установить",
        ["get"] = "получить",
        ["read"] = "читать",
        ["write"] = "записать",
        ["copy"] = "копировать",
        ["paste"] = "вставить",
        ["clear"] = "очистить",
        ["reset"] = "сбросить",
        ["change"] = "изменить",
        ["connect"] = "подключить",
        ["disconnect"] = "отключить",
        ["scan"] = "сканировать",
        ["probe"] = "опросить",
        ["detect"] = "определить",
        ["discover"] = "обнаружить",
        ["resolve"] = "разрешить",
        ["skip"] = "пропустить",
        ["treat"] = "рассматривать",
        ["try"] = "попробовать",
        ["allow"] = "разрешить",
        ["block"] = "блокировать",
        ["wait"] = "ждать",
        ["exit"] = "выйти",
        ["quit"] = "выйти",
        ["help"] = "справка",
        ["see"] = "см.",
        ["give"] = "дать",
        ["given"] = "заданный",
        ["take"] = "взять",
        ["make"] = "сделать",
        ["know"] = "знать",
        ["think"] = "думать",
        ["work"] = "работать",
        ["works"] = "работает",
        ["need"] = "нужно",
        ["want"] = "хотеть",
        ["play"] = "воспроизвести",
        ["pause"] = "пауза",
        ["translate"] = "перевести",
        ["translated"] = "переведено",

        // Technical & general nouns / adjectives
        ["packet"] = "пакет",
        ["packets"] = "пакеты",
        ["host"] = "хост",
        ["hosts"] = "хосты",
        ["interface"] = "интерфейс",
        ["interfaces"] = "интерфейсы",
        ["route"] = "маршрут",
        ["routes"] = "маршруты",
        ["debug"] = "отладка",
        ["debugging"] = "отладки",
        ["output"] = "вывод",
        ["input"] = "ввод",
        ["file"] = "файл",
        ["files"] = "файлы",
        ["folder"] = "папка",
        ["directory"] = "каталог",
        ["path"] = "путь",
        ["user"] = "пользователь",
        ["users"] = "пользователи",
        ["fully"] = "полностью",
        ["privileged"] = "привилегированный",
        ["unprivileged"] = "непривилегированный",
        ["privilege"] = "привилегия",
        ["privileges"] = "привилегии",
        ["raw"] = "сырой",
        ["socket"] = "сокет",
        ["sockets"] = "сокеты",
        ["frame"] = "кадр",
        ["frames"] = "кадры",
        ["version"] = "версия",
        ["number"] = "номер",
        ["summary"] = "сводка",
        ["page"] = "страница",
        ["pages"] = "страницы",
        ["example"] = "пример",
        ["examples"] = "примеры",
        ["option"] = "опция",
        ["options"] = "опции",
        ["target"] = "цель",
        ["targets"] = "цели",
        ["network"] = "сеть",
        ["networks"] = "сети",
        ["port"] = "порт",
        ["ports"] = "порты",
        ["service"] = "служба",
        ["services"] = "службы",
        ["system"] = "система",
        ["systems"] = "системы",
        ["server"] = "сервер",
        ["client"] = "клиент",
        ["address"] = "адрес",
        ["addresses"] = "адреса",
        ["name"] = "имя",
        ["names"] = "имена",
        ["list"] = "список",
        ["time"] = "время",
        ["timeout"] = "таймаут",
        ["delay"] = "задержка",
        ["rate"] = "скорость",
        ["speed"] = "скорость",
        ["fast"] = "быстрый",
        ["faster"] = "быстрее",
        ["slow"] = "медленный",
        ["default"] = "по умолчанию",
        ["custom"] = "пользовательский",
        ["error"] = "ошибка",
        ["errors"] = "ошибки",
        ["warning"] = "предупреждение",
        ["warnings"] = "предупреждения",
        ["status"] = "статус",
        ["state"] = "состояние",
        ["mode"] = "режим",
        ["window"] = "окно",
        ["screen"] = "экран",
        ["settings"] = "настройки",
        ["language"] = "язык",
        ["text"] = "текст",
        ["music"] = "музыка",
        ["audio"] = "аудио",
        ["sound"] = "звук",
        ["wave"] = "волна",
        ["waves"] = "волны",
        ["track"] = "трек",
        ["online"] = "онлайн",
        ["offline"] = "офлайн",
        ["local"] = "локальный",
        ["model"] = "модель",
        ["models"] = "модели",
        ["normal"] = "обычный",
        ["verbose"] = "подробный",
        ["verbosity"] = "подробность",
        ["level"] = "уровень",
        ["greater"] = "больший",
        ["effect"] = "эффект",
        ["never"] = "никогда",
        ["always"] = "всегда",
        ["random"] = "случайный",
        ["order"] = "порядок",
        ["source"] = "источник",
        ["destination"] = "назначение",
        ["data"] = "данные",
        ["length"] = "длина",
        ["size"] = "размер",
        ["format"] = "формат",
        ["type"] = "тип",
        ["value"] = "значение",
        ["values"] = "значения",
        ["key"] = "ключ",
        ["code"] = "код",
        ["line"] = "строка",
        ["lines"] = "строки",
        ["word"] = "слово",
        ["words"] = "слова",
        ["new"] = "новый",
        ["old"] = "старый",
        ["good"] = "хороший",
        ["bad"] = "плохой",
        ["first"] = "первый",
        ["last"] = "последний",
        ["next"] = "следующий",
        ["previous"] = "предыдущий",
        ["current"] = "текущий",
        ["available"] = "доступный",
        ["required"] = "требуемый",
        ["optional"] = "необязательный",
        ["supported"] = "поддерживаемый",
        ["valid"] = "допустимый",
        ["invalid"] = "недопустимый",
        ["true"] = "истина",
        ["false"] = "ложь"
    };

    private static readonly Dictionary<string, string> RuToEnWords = BuildReverseDictionary();

    private static Dictionary<string, string> BuildReverseDictionary()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (en, ru) in EnToRuWords)
        {
            if (!string.IsNullOrWhiteSpace(ru) && !dict.ContainsKey(ru))
            {
                dict[ru] = en;
            }
        }
        return dict;
    }

    public static TranslationResult Translate(string text, string sourceLangHint, string targetLang)
    {
        if (string.IsNullOrWhiteSpace(text))
            return new TranslationResult(false, string.Empty, sourceLangHint, targetLang, true, "Пустой текст.");

        string detected = DetectLanguage(text, sourceLangHint);
        string normTarget = NormalizeLang(targetLang);

        if (string.Equals(detected, normTarget, StringComparison.OrdinalIgnoreCase))
        {
            return new TranslationResult(true, text.Trim(), detected, normTarget, true);
        }

        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        var sb = new StringBuilder();

        for (int i = 0; i < lines.Length; i++)
        {
            if (i > 0) sb.AppendLine();
            string line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (normTarget == "ru")
                sb.Append(TranslateLineEnToRu(line));
            else if (normTarget == "en")
                sb.Append(TranslateLineRuToEn(line));
            else
                sb.Append(TranslateLineEnToRu(line));
        }

        string result = sb.ToString().Trim();
        return new TranslationResult(true, result, detected, normTarget, true);
    }

    private static string TranslateLineEnToRu(string line)
    {
        // Preserve leading CLI flags like "--packet-trace: " or "-V: "
        string prefix = string.Empty;
        string body = line;

        var flagMatch = Regex.Match(line, @"^(\s*(?:[-—–]{1,2}[A-Za-z0-9_/-]+(?:\s*,\s*[-—–]{1,2}[A-Za-z0-9_/-]+)*)\s*[:=]\s*)(.*)$");
        if (flagMatch.Success)
        {
            prefix = flagMatch.Groups[1].Value.Replace('—', '-').Replace('–', '-');
            body = flagMatch.Groups[2].Value;
        }

        string working = body;
        foreach (var (pattern, replacement) in EnToRuPhrases)
        {
            working = Regex.Replace(
                working,
                $@"\b{Regex.Escape(pattern)}\b",
                 m => MatchCase(m.Value, replacement),
                RegexOptions.IgnoreCase);
        }

        // Tokenize remaining English words while keeping punctuation & CLI arguments intact
        working = Regex.Replace(working, @"(?<![-—–/\\])\b([A-Za-z]{2,})\b", match =>
        {
            string word = match.Value;
            if (TryLookupEnWord(word, out string ruWord))
            {
                return MatchCase(word, ruWord);
            }
            return word;
        });

        // Clean up double spaces left by omitted articles ("the", "a", "an")
        working = Regex.Replace(working, @"\s{2,}", " ").Trim();
        return prefix + working;
    }

    private static string TranslateLineRuToEn(string line)
    {
        string working = Regex.Replace(line, @"\b([А-Яа-яЁё]{2,})\b", match =>
        {
            string word = match.Value;
            if (RuToEnWords.TryGetValue(word, out string? enWord) && !string.IsNullOrEmpty(enWord))
            {
                return MatchCase(word, enWord);
            }
            return word;
        });

        return Regex.Replace(working, @"\s{2,}", " ").Trim();
    }

    private static bool TryLookupEnWord(string word, out string translated)
    {
        if (EnToRuWords.TryGetValue(word, out string? direct))
        {
            translated = direct;
            return true;
        }

        string lower = word.ToLowerInvariant();
        if (lower.EndsWith("ing") && lower.Length > 5)
        {
            string stem = lower[..^3];
            if (EnToRuWords.TryGetValue(stem, out string? s1) && !string.IsNullOrEmpty(s1))
            {
                translated = s1;
                return true;
            }
            if (EnToRuWords.TryGetValue(stem + "e", out string? s2) && !string.IsNullOrEmpty(s2))
            {
                translated = s2;
                return true;
            }
        }

        if (lower.EndsWith("ed") && lower.Length > 4)
        {
            string stem = lower[..^2];
            if (EnToRuWords.TryGetValue(stem, out string? s1) && !string.IsNullOrEmpty(s1))
            {
                translated = s1;
                return true;
            }
            if (EnToRuWords.TryGetValue(lower[..^1], out string? s2) && !string.IsNullOrEmpty(s2))
            {
                translated = s2;
                return true;
            }
        }

        if (lower.EndsWith("es") && lower.Length > 4 && EnToRuWords.TryGetValue(lower[..^2], out string? esVal) && !string.IsNullOrEmpty(esVal))
        {
            translated = esVal;
            return true;
        }

        if (lower.EndsWith("s") && lower.Length > 3 && EnToRuWords.TryGetValue(lower[..^1], out string? sVal) && !string.IsNullOrEmpty(sVal))
        {
            translated = sVal;
            return true;
        }

        translated = string.Empty;
        return false;
    }

    private static string MatchCase(string original, string replacement)
    {
        if (string.IsNullOrEmpty(replacement) || string.IsNullOrEmpty(original))
            return replacement;

        if (original.Length > 1 && original.All(c => !char.IsLetter(c) || char.IsUpper(c)))
            return replacement.ToUpperInvariant();

        if (char.IsUpper(original[0]))
            return char.ToUpper(replacement[0]) + replacement[1..];

        return replacement;
    }

    private static string DetectLanguage(string text, string hint)
    {
        int cyrillic = 0;
        int latin = 0;
        foreach (char c in text)
        {
            if (c is >= 'А' and <= 'я' or 'Ё' or 'ё') cyrillic++;
            else if (c is >= 'A' and <= 'Z' or >= 'a' and <= 'z') latin++;
        }

        if (cyrillic > latin) return "ru";
        if (latin > 0) return "en";
        return NormalizeLang(hint);
    }

    private static string NormalizeLang(string lang)
    {
        if (string.IsNullOrWhiteSpace(lang) || string.Equals(lang, "auto", StringComparison.OrdinalIgnoreCase))
            return "en";
        int dash = lang.IndexOf('-');
        return (dash > 0 ? lang[..dash] : lang).ToLowerInvariant();
    }
}
