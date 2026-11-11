using System.Text.Json.Nodes;
using Amanu.Core.Localization;
using static Amanu.Core.Localization.Localized;

namespace Amanu.Core.Configuration;

public enum SettingKind
{
    Toggle,
    Choice,
    Number,
    Text,
    MultilineText,
    /// <summary>A list of strings, edited one per line.</summary>
    List,
    /// <summary>An executable and its arguments — the Windows shape of <c>on_stop</c>.</summary>
    Command,
    /// <summary>The path of a text file the user edits, with a button that opens it.</summary>
    File,
}

/// <summary>One setting: where it lives in the file, what it does, and how it is edited.</summary>
/// <param name="Path">The keys from the file's root, e.g. <c>auto_record.start_delay_seconds</c>.</param>
/// <param name="DescribedAs">
/// For a setting whose absence is a behaviour rather than a value, the words for
/// that behaviour, shown in the grey of an empty field.
/// </param>
/// <param name="AskedInSetup">
/// True when the setup form already asks this fully. Those are left out of the
/// Advanced tab, which is the tab's premise: it is what setup does not ask. Asked
/// twice in one window, in two vocabularies, they were two controls a person had
/// to work out were the same control.
/// </param>
public sealed record SettingEntry(
    string Path,
    string Label,
    string Help,
    SettingKind Kind,
    IReadOnlyList<string>? Options = null,
    string? Unit = null,
    string? DescribedAs = null,
    bool AskedInSetup = false,
    bool NeedsRestart = false,
    bool Decimal = false)
{
    public string[] Keys => Path.Split('.');
}

public sealed record SettingSection(string Title, IReadOnlyList<SettingEntry> Entries);

/// <summary>
/// Every setting Amanu has, described once. The Advanced tab renders it, the
/// config reader checks values against it, and the README is written against it.
/// A setting that exists but appears in no window is a setting nobody finds.
/// </summary>
/// <remarks>
/// Built on each use rather than once: the words depend on the interface
/// language, which is settled at startup after anything static might have run.
/// Defaults are not written here — they are the initializers of
/// <see cref="AppSettings"/>, read through <see cref="DefaultFor"/>, so there is
/// one place a default lives.
/// </remarks>
public static class SettingsSchema
{
    public static IReadOnlyList<SettingSection> Sections =>
    [
        new(T("Recording by itself", "Запись сама по себе"),
        [
            new("auto_record.enabled",
                T("Record meetings automatically", "Записывать встречи автоматически"),
                T("Start and stop on their own when a call begins and ends.",
                  "Запись начинается и заканчивается сама, вместе со звонком."),
                SettingKind.Toggle, AskedInSetup: true),
            new("auto_record.mic_activity",
                T("Watch which app holds the microphone", "Следить, какое приложение держит микрофон"),
                T("How recording by itself notices a call: an app from the list below opening the mic — Zoom, Teams, Telegram, a meeting in a browser. Off, there is nothing for it to start from.",
                  "Так запись сама по себе замечает звонок: микрофон открывает приложение из списка ниже — Zoom, Teams, Telegram, встреча в браузере. Выключено — начинать ей не от чего."),
                SettingKind.Toggle),
            new("auto_record.start_delay_seconds",
                T("Wait before starting", "Ждать перед началом"),
                T("How long a call app must hold the microphone before this counts as a meeting.",
                  "Сколько приложение звонка должно держать микрофон, чтобы это считалось встречей."),
                SettingKind.Number, Unit: T("seconds", "с")),
            new("auto_record.stop_delay_seconds",
                T("Wait before stopping", "Ждать перед остановкой"),
                T("How long nobody may hold the mic before the meeting ends.",
                  "Сколько никто не держит микрофон, прежде чем встреча кончится."),
                SettingKind.Number, Unit: T("seconds", "с")),
            new("auto_record.min_duration_seconds",
                T("Discard meetings shorter than", "Выбрасывать встречи короче"),
                T("A mic that opened for a few seconds was never a meeting. The wait before stopping doesn't count. Automatic recordings only.",
                  "Микрофон, открытый на несколько секунд, встречей не был. Ожидание перед остановкой не считается. Только для автоматических записей."),
                SettingKind.Number, Unit: T("seconds", "с")),
            new("auto_record.silence_stop_minutes",
                T("Stop after silence on both tracks", "Останавливать после тишины на обеих дорожках"),
                T("The backstop against an app that never releases the microphone.",
                  "Страховка от приложения, которое так и не отпускает микрофон."),
                SettingKind.Number, Unit: T("minutes", "мин")),
            new("auto_record.max_duration_minutes",
                T("Hard duration ceiling", "Жёсткий предел длительности"),
                T("Applies to manual recordings too — whatever this is, it stopped being a meeting.",
                  "Касается и ручных записей: что бы это ни было, встречей оно быть перестало."),
                SettingKind.Number, Unit: T("minutes", "мин")),
            new("auto_record.apps",
                T("Call apps", "Приложения для звонков"),
                T("Process names that count as a call, one per line. Clearing the field brings back the built-in list of call apps and browsers.",
                  "Имена процессов, которые считаются звонком, по одному в строке. Очищенное поле возвращает встроенный список звонилок и браузеров."),
                SettingKind.List),
            new("auto_record.ignore_apps",
                T("Never count these", "Никогда не считать звонком"),
                T("Process names that never start a recording, whatever they do with the mic.",
                  "Имена процессов, которые не начинают запись, что бы они ни делали с микрофоном."),
                SettingKind.List, DescribedAs: T("empty", "пусто")),
        ]),

        new(T("Audio", "Звук"),
        [
            new("system_audio",
                T("Record system audio from", "Записывать звук системы"),
                T("app: only the call app and the processes it started. all: everything Windows plays, music and notifications included.",
                  "app — только приложение звонка и запущенные им процессы. all — всё, что играет Windows, вместе с музыкой и уведомлениями."),
                SettingKind.Choice, Options: ["app", "all"]),
            new("keep_audio",
                T("Keep the audio after transcribing", "Оставлять звук после расшифровки"),
                T("Saves one stereo M4A: your mic on the left, the other side on the right. A recording that never got a transcript is kept either way.",
                  "Сохраняет один стереофайл M4A: ваш микрофон слева, дальняя сторона справа. Запись, которая так и не получила расшифровку, остаётся в любом случае."),
                SettingKind.Toggle, AskedInSetup: true),
            new("recordings_dir",
                T("Recordings folder", "Папка записей"),
                T("Where sessions land.", "Куда складываются встречи."),
                SettingKind.Text, AskedInSetup: true),
        ]),

        new(T("Transcription", "Расшифровка"),
        [
            new("transcription.enabled",
                T("Transcribe recordings", "Расшифровывать записи"),
                T("Off means record only; the on_stop command still runs.",
                  "Выключено — только запись; команда on_stop всё равно запускается."),
                SettingKind.Toggle, AskedInSetup: true),
            new("live_transcription.enabled",
                T("Show a live transcript", "Показывать расшифровку по ходу встречи"),
                T("Transcribes the meeting in 20-second pieces while it records, with the same engine as the final transcript.",
                  "Пока идёт запись, расшифровывает встречу кусками по 20 секунд тем же движком, что и итоговую расшифровку."),
                SettingKind.Toggle, AskedInSetup: true),
            new("live_transcription.engine",
                T("Live engine", "Движок лайва"),
                T("local: a model on this PC, nothing leaves it. gemini: Gemini Live captions the meeting in the cloud with your Gemini key.",
                  "local — модель на этом ПК, звук никуда не уходит. gemini — Gemini Live расшифровывает встречу в облаке по вашему ключу Gemini."),
                SettingKind.Choice, Options: LiveTranscriptionSettings.Engines, AskedInSetup: true),
            new("live_transcription.gemini_model",
                T("Gemini Live model", "Модель Gemini Live"),
                T("The Gemini Live model that captions the meeting.", "Модель Gemini Live для расшифровки по ходу встречи."),
                SettingKind.Text),
            new("transcription.engine",
                T("Engine", "Движок"),
                T("auto: the cloud engine when there's a key and the network answers, the local model otherwise.",
                  "auto — облачный движок, когда есть ключ и отвечает сеть, иначе локальная модель."),
                SettingKind.Choice, Options: ["auto", .. TranscriptionSettings.CloudEngines, .. TranscriptionSettings.LocalEngines],
                AskedInSetup: true),
            new("transcription.cloud",
                T("Cloud engine", "Облачный движок"),
                T("Which service auto uploads to when a key is available.",
                  "В какой сервис auto отправляет запись, когда есть ключ."),
                SettingKind.Choice, Options: TranscriptionSettings.CloudEngines, AskedInSetup: true),
            new("transcription.local_engine",
                T("Local engine", "Локальный движок"),
                T("Which private model auto uses when the cloud is unavailable.",
                  "Какую локальную модель auto использует, когда облако недоступно."),
                SettingKind.Choice, Options: TranscriptionSettings.LocalEngines, AskedInSetup: true),
            new("transcription.language",
                T("Meeting language", "Язык встреч"),
                T("Two-letter code for what meetings are mostly in. English is expected alongside it; the engines still decide which they heard.",
                  "Двухбуквенный код языка, на котором в основном идут встречи. Вместе с ним ожидается английский, а какой язык прозвучал, решают сами движки."),
                SettingKind.Text, DescribedAs: T("detected from any language", "определяется, язык любой")),
            new("transcript_echo_filter",
                T("Drop echoed speech", "Убирать отражённую речь"),
                T("Removes mic segments duplicating system audio — the far end coming back through the speakers.",
                  "Убирает куски с микрофона, повторяющие звук системы, — дальнюю сторону, вернувшуюся через колонки."),
                SettingKind.Toggle),
            new("offline_echo_cancellation",
                T("Remove echo before transcribing", "Убирать эхо перед расшифровкой"),
                T("Cleans the call's sound out of a copy of the mic on this computer before transcription. The recording itself is not changed.",
                  "Перед расшифровкой убирает звук звонка из копии записи микрофона прямо на этом компьютере. Сама запись не меняется."),
                SettingKind.Toggle),
            new("echo_model",
                T("Echo removal model", "Модель удаления эха"),
                T("solve-floor (the default): finds the echo path over the whole recording, also when the clocks of the two devices drift apart, and turns down what is left of the echo. sonora: the classic WebRTC echo canceller, by far the fastest. fast: a small LocalVQE neural model, about 13 times slower. thorough: a larger LocalVQE model that also removes echo in rooms with strong reverb, slower again. solve: fits the echo path over the whole recording, also when the clocks of the two devices drift apart. solve-floor: the same, and turns down what is left of the echo. solve-gate: the same, and turns the mic down between your sentences, which helps most in loud noise. solve-gate-cut: the same, and the engine then hears only your sentences, each on its own.",
                  "solve-floor (по умолчанию): подбирает путь эха по всей записи, в том числе когда часы двух устройств расходятся, и приглушает остаток эха. sonora: классический эхоподавитель WebRTC, самый быстрый. fast: небольшая нейросетевая модель LocalVQE, примерно в 13 раз медленнее. thorough: модель LocalVQE побольше, убирает эхо и в гулких комнатах, ещё медленнее. solve: подбирает путь эха по всей записи, в том числе когда часы двух устройств расходятся. solve-floor: то же, и приглушает остаток эха. solve-gate: то же, и приглушает микрофон между вашими фразами, что больше всего помогает при сильном шуме. solve-gate-cut: то же, и движок слышит только ваши фразы, каждую отдельно."),
                SettingKind.Choice, Options: AppSettings.EchoModels),
            new("keep_echo_cleaned_audio",
                T("Keep echo-cleaned audio", "Хранить очищенный от эха звук"),
                T("Keeps the cleaned copy beside the recording, so transcribing again skips echo removal. Costs about 230 MB per hour of meeting.",
                  "Хранит очищенную копию рядом с записью, и повторная расшифровка не убирает эхо заново. Занимает около 230 МБ на час встречи."),
                SettingKind.Toggle),
            new("transcription.level_normalization",
                T("Even out speech levels before transcribing", "Выравнивать громкость речи перед расшифровкой"),
                T("auto: makes quiet or loud speech in a copy of each track closer to normal before transcription. Can help when quiet speakers lose words. The recording itself is not changed.",
                  "auto — перед расшифровкой делает тихую или громкую речь в копии каждой дорожки ближе к обычной. Может помочь, когда у тихих собеседников теряются слова. Сама запись не меняется."),
                SettingKind.Choice, Options: TranscriptionSettings.LevelNormalizations),
            new("transcription.diarization",
                T("Tell call voices apart", "Различать голоса в звонке"),
                T("auto: after a local engine, splits the call side by voice on this computer, so \"them\" becomes \"them A\", \"them B\". Downloads about 32 MB of models once. Cloud engines do this themselves. Off by default: it is not yet reliable on calls of several people, and it adds about as much time as transcription.",
                  "auto — после локального движка делит сторону звонка по голосам прямо на этом компьютере: «они» становится «они A», «они B». Один раз скачивает около 32 МБ моделей. Облачные движки делают это сами. По умолчанию выключено: на звонках с несколькими людьми пока ненадёжно и занимает примерно столько же времени, сколько расшифровка."),
                SettingKind.Choice, Options: TranscriptionSettings.Diarizations),
            new("transcription.diarization_threshold",
                T("Voice likeness threshold", "Порог сходства голосов"),
                T("How alike two voices must be to count as one person, from 0 to 1. Higher gives fewer people; lower splits one person into several.",
                  "Насколько похожими должны быть два голоса, чтобы считаться одним человеком, от 0 до 1. Выше — меньше людей; ниже — один человек делится на нескольких."),
                SettingKind.Number, DescribedAs: T($"{TranscriptionSettings.DefaultDiarizationThreshold}", $"{TranscriptionSettings.DefaultDiarizationThreshold}"), Decimal: true),
            new("transcription.assemblyai.speech_model",
                T("AssemblyAI speech model", "Модель речи AssemblyAI"),
                T("Empty sends nothing and lets the API pick its own default.",
                  "Пусто — ничего не отправляется, и API выбирает сам."),
                SettingKind.Text, DescribedAs: T("the API's own default", "выбор самого API")),
            new("transcription.openai.model",
                T("OpenAI model", "Модель OpenAI"),
                T("The default is the only OpenAI model that returns both timings and speakers.",
                  "По умолчанию стоит единственная модель OpenAI, которая возвращает и время, и говорящих."),
                SettingKind.Text),
            new("transcription.gemini.models",
                T("Gemini models", "Модели Gemini"),
                T("Asked in this order, at most three: when one is busy or out of quota, the next is asked.",
                  "Опрашиваются по порядку, не больше трёх: когда одна занята или без квоты, спрашивается следующая."),
                SettingKind.List, AskedInSetup: true),
        ]),

        new(T("Summaries", "Саммари"),
        [
            new("summary.enabled",
                T("Write a summary", "Писать саммари"),
                T("Topic, key points, decisions, action items, open questions.",
                  "Тема, главное, решения, задачи, открытые вопросы."),
                SettingKind.Toggle, AskedInSetup: true),
            new("summary.backend",
                T("Which model to ask", "У какой модели спрашивать"),
                T("auto walks the chain: claude CLI, Anthropic API, codex CLI, OpenAI API, ollama — subscriptions before metered keys. none skips summarizing without turning off the rest.",
                  "auto идёт по цепочке: claude CLI, Anthropic API, codex CLI, OpenAI API, ollama — сначала подписки, потом платные ключи. none пропускает саммари, не выключая всего остального."),
                SettingKind.Choice, Options: SummarySettings.Backends),
            new("summary.source",
                T("Summarize from", "Из чего писать саммари"),
                T("transcript: the final transcript made after the meeting. live: the live captions kept during it. both: sends the two together, so each fills the other's gaps. Without live captions the final transcript is used.",
                  "transcript — итоговая расшифровка после встречи. live — живые субтитры, сохранённые во время встречи. both — обе вместе, чтобы одна закрывала пробелы другой. Без живых субтитров берётся итоговая расшифровка."),
                SettingKind.Choice, Options: SummarySettings.Sources),
            new("summary.model",
                T("Anthropic model", "Модель Anthropic"),
                T("Used on the API path, and by the claude CLI once it is set here; left empty, the CLI uses whatever model Claude Code is set to.",
                  "Для пути через API, а если задана здесь, то и для claude CLI; пусто — CLI берёт ту модель, на которую настроен Claude Code."),
                SettingKind.Text, DescribedAs: T($"{SummarySettings.DefaultAnthropicModel} for the API; Claude Code’s own for the CLI",
                    $"{SummarySettings.DefaultAnthropicModel} для API; для CLI — модель Claude Code")),
            new("summary.openai_model",
                T("OpenAI model", "Модель OpenAI"),
                T("Used by the codex CLI and the OpenAI API.", "Для codex CLI и для OpenAI API."),
                SettingKind.Text),
            new("summary.openai_base_url",
                T("OpenAI-compatible Base URL", "Base URL OpenAI-compatible API"),
                T("The API root, including /v1. Leave the default for OpenAI itself. Anything not on this computer must be https.",
                  "Корень API вместе с /v1. Для самого OpenAI оставьте значение по умолчанию. Всё, что не на этом компьютере, — только https."),
                SettingKind.Text),
            new("summary.openai_compatible",
                T("OpenAI-compatible service", "OpenAI-compatible сервис"),
                T("Use the custom server URL and its own key.", "Использовать URL сервера и его собственный ключ."),
                SettingKind.Toggle, AskedInSetup: true),
            new("summary.ollama_model",
                T("Local model", "Местная модель"),
                T("The fully-offline fallback.", "Запасной вариант, целиком без сети."),
                SettingKind.Text),
            new("summary.ollama_base_url",
                T("Ollama Base URL", "Base URL Ollama"),
                T("Where Amanu reaches Ollama. Another machine sees the meeting, and must be reached over https — plain http is only accepted on this computer.",
                  "Где Amanu находит Ollama. Другая машина увидит содержимое встречи, и адрес у неё должен быть https — простой http принимается только на этом компьютере."),
                SettingKind.Text),
            new("summary.language",
                T("Summary language", "Язык саммари"),
                T("Leave empty to write in whichever language the meeting was held in.",
                  "Пусто — саммари пишется на языке самой встречи."),
                SettingKind.Text, DescribedAs: T("the language of the meeting", "язык встречи")),
            new("summary.template",
                T("Summary template", "Шаблон саммари"),
                T("Instructions sent to the model. Clear the field to restore the built-in template.",
                  "Инструкции для модели. Очистите поле, чтобы вернуть встроенный шаблон."),
                SettingKind.MultilineText, DescribedAs: T("the built-in template", "встроенный шаблон")),
            new("slack_summary.enabled",
                T("Offer the summary for Slack", "Предлагать саммари для Slack"),
                T("After a Slack huddle, a small card offers to copy the summary and open Slack. It never picks the channel and never sends.",
                  "После созвона в Slack маленькая карточка предлагает скопировать саммари и открыть Slack. Она не выбирает канал и ничего не отправляет."),
                SettingKind.Toggle),
            new("slack_summary.apps",
                T("Apps that count as Slack", "Приложения, которые считаются Slack"),
                T("Process names, one per line, as meta.json keeps them in process_family.",
                  "Имена процессов, по одному в строке, как их хранит process_family в meta.json."),
                SettingKind.List, DescribedAs: "slack.exe"),
            new("slack_summary.min_minutes",
                T("Shortest call that gets a card", "Самый короткий звонок с карточкой"),
                T("A call that rang and stopped gives no card.", "Звонок, который прозвенел и закончился, карточки не даёт."),
                SettingKind.Number, Unit: T("minutes", "мин")),
        ]),

        new(T("Naming", "Имена"),
        [
            new("speaker_names.enabled",
                T("Put names to speakers", "Подставлять имена говорящих"),
                T("Works out who \"them A\" was from people addressing each other, and applies it only when the transcript proves it.",
                  "Догадывается, кто такой «them A», по тому, как люди обращаются друг к другу, и подставляет имя, только когда расшифровка это подтверждает."),
                SettingKind.Toggle),
            new("user_name",
                T("Your name", "Ваше имя"),
                T("Used instead of \"me\". Empty falls back to the Windows account's name.",
                  "Ставится вместо «me». Пусто — берётся имя учётной записи Windows."),
                SettingKind.Text, DescribedAs: T("the account's name", "имя учётной записи")),
            new("speaker_names.backend",
                T("Which model to ask", "У какой модели спрашивать"),
                T("summary sends the transcript wherever summaries go, and nowhere when they are off — only your own name is filled in then. Anything else is a choice for naming alone; none asks no model.",
                  "summary отправляет расшифровку туда же, куда и саммари, а если саммари выключены — никуда, и подставляется только ваше имя. Любой другой вариант — выбор только для имён; none — не спрашивать никакую модель."),
                SettingKind.Choice, Options: SpeakerNameSettings.Backends),
            new("speaker_names.model",
                T("Anthropic model for naming", "Модель Anthropic для имён"),
                T("Naming is an easier job than summarizing; empty uses the summary's model.",
                  "Имена — работа проще саммари; пусто — та же модель, что у саммари."),
                SettingKind.Text, DescribedAs: T("the summary's model", "модель саммари")),
            new("speaker_names.people_file",
                T("People list", "Список людей"),
                T("Who may be on your calls: one person per line, full name first, then other names with commas (Joseph Smith, Joe, Jojo). Put (me) at the end of your own line. Names are given as full names, and the transcription spells them as written.",
                  "Кто бывает на ваших звонках: по человеку в строке, сначала полное имя, затем другие имена через запятую (Joseph Smith, Joe, Jojo). В конце своей строки поставьте (me). Имена пишутся полностью, и расшифровка пишет их так, как в списке."),
                SettingKind.File, DescribedAs: T(@"people.txt in %LOCALAPPDATA%\Amanu Data", @"people.txt в %LOCALAPPDATA%\Amanu Data")),
        ]),

        new(T("Interface", "Интерфейс"),
        [
            new("interface_language",
                T("Language of Amanu's own windows", "Язык окон Amanu"),
                T("auto follows Windows. Not the language of your meetings, and not the one summaries are written in — those are settings of their own.",
                  "auto — как в Windows. Это не язык встреч и не язык саммари: у тех есть собственные настройки."),
                SettingKind.Choice, Options: Localized.ConfiguredValues, NeedsRestart: true),
            new("start_at_login",
                T("Start when you sign in", "Запускать при входе в Windows"),
                T("So a meeting is never missed because nobody opened Amanu.",
                  "Чтобы встреча не пропала из-за того, что Amanu никто не открыл."),
                SettingKind.Toggle, AskedInSetup: true),
            new("tray_icon",
                T("Show in the notification area", "Показывать в области уведомлений"),
                T("Off with the taskbar button too leaves Amanu with no icon anywhere: open Amanu again to bring its window back.",
                  "Выключено вместе с кнопкой на панели задач — Amanu нигде не видно: чтобы вернуть окно, откройте Amanu ещё раз."),
                SettingKind.Toggle, AskedInSetup: true),
            new("taskbar_icon",
                T("Show on the taskbar", "Показывать на панели задач"),
                T("The window's taskbar button and Alt+Tab.", "Кнопка окна на панели задач и в Alt+Tab."),
                SettingKind.Toggle, AskedInSetup: true),
            new("on_stop",
                T("Run after each session", "Запускать после каждой встречи"),
                T("A program and its arguments, one per line; {session} is replaced by the session folder. Runs once, after the transcript and summary are written.",
                  "Программа и её аргументы, по одному в строке; {session} заменяется на папку встречи. Запускается один раз, после того как записаны расшифровка и саммари."),
                SettingKind.Command, DescribedAs: T("nothing", "ничего")),
        ]),

        new(T("Statistics", "Статистика"),
        [
            new("analytics",
                T("Send usage statistics", "Отправлять статистику об использовании"),
                T("Feature usage with a random installation identifier; no meeting content.",
                  "Использование функций со случайным идентификатором установки; без содержимого встреч."),
                SettingKind.Toggle, AskedInSetup: true),
        ]),
    ];

    /// <summary><see cref="Sections"/> minus what setup already asks; an emptied section goes with it.</summary>
    public static IReadOnlyList<SettingSection> AdvancedSections =>
        Sections.Select(section => section with { Entries = section.Entries.Where(entry => !entry.AskedInSetup).ToArray() })
            .Where(section => section.Entries.Count > 0)
            .ToArray();

    public static IEnumerable<SettingEntry> Entries => Sections.SelectMany(section => section.Entries);

    public static SettingEntry? Find(string path) => Entries.FirstOrDefault(entry => entry.Path == path);

    /// <summary>What a setting is when the file does not say.</summary>
    public static JsonNode? DefaultFor(string path, string homeDirectory) =>
        SettingsDocument.Get(SettingsDocument.ToNode(AppSettings.CreateDefault(homeDirectory)), path);

    /// <summary>The default in words, for the grey of an empty field.</summary>
    public static string DescribeDefault(SettingEntry entry, string homeDirectory)
    {
        if (entry.DescribedAs is { } words) return words;
        var value = DefaultFor(entry.Path, homeDirectory);
        return entry.Kind switch
        {
            SettingKind.Toggle => value?.GetValue<bool>() == true ? T("on", "включено") : T("off", "выключено"),
            SettingKind.Number => $"{value} {entry.Unit}",
            SettingKind.List => value is JsonArray array && array.Count > 0
                ? T("known call apps and browsers", "известные звонилки и браузеры")
                : T("empty", "пусто"),
            _ => value?.ToString() ?? "",
        };
    }
}
