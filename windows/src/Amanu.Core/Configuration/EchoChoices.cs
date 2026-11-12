using static Amanu.Core.Localization.Localized;

namespace Amanu.Core.Configuration;

/// <summary>
/// How the setup form offers the echo_model setting: the models worth choosing
/// first, each with one sentence, and the rest after them. The rest are variants
/// kept so that echo removers can be compared against each other; see
/// windows/ECHO_REMOVAL.md for what each of them does.
/// </summary>
public static class EchoChoices
{
    /// <summary>In the order the menu lists them: the default first, the rest of its family, then the older removers.</summary>
    public static readonly IReadOnlyList<string> Main = ["solve-gate-cut", "solve-gate", "solve-floor", "solve", "sonora", "fast", "thorough"];

    /// <summary>One sentence on what the model does, with "(the default)" when it is the default.</summary>
    public static string Describe(string model, string? defaultModel)
    {
        var words = model switch
        {
            "solve-gate-cut" => T("solve-gate-cut finds the echo path over the whole recording, also when the clocks of the two devices drift apart, and the engine then hears only your sentences, each on its own.",
                "solve-gate-cut подбирает путь эха по всей записи, в том числе когда часы двух устройств расходятся, и движок слышит только ваши фразы, каждую отдельно."),
            "solve-gate" => T("solve-gate fits the echo path over the whole recording and turns the mic down between your sentences, which helps most in loud noise.",
                "solve-gate подбирает путь эха по всей записи и приглушает микрофон между вашими фразами, что больше всего помогает при сильном шуме."),
            "solve-floor" => T("solve-floor fits the echo path over the whole recording and turns down what is left of the echo.",
                "solve-floor подбирает путь эха по всей записи и приглушает остаток эха."),
            "solve" => T("solve fits the echo path over the whole recording, also when the clocks of the two devices drift apart.",
                "solve подбирает путь эха по всей записи, в том числе когда часы двух устройств расходятся."),
            "sonora" => T("sonora is the classic WebRTC echo canceller, by far the fastest.",
                "sonora — классический эхоподавитель WebRTC, самый быстрый."),
            "fast" => T("fast is a small LocalVQE neural model, about 13 times slower than sonora.",
                "fast — небольшая нейросетевая модель LocalVQE, примерно в 13 раз медленнее sonora."),
            "thorough" => T("thorough is a larger LocalVQE model that also removes echo in rooms with strong reverb, slower again.",
                "thorough — модель LocalVQE побольше, убирает эхо и в гулких комнатах, ещё медленнее."),
            _ => T($"{model} is a variant kept for comparing echo removers; windows/ECHO_REMOVAL.md says what it does.",
                $"{model} — вариант для сравнения эхоподавителей; что он делает, написано в windows/ECHO_REMOVAL.md."),
        };
        return model == defaultModel ? words + T(" It is the default.", " Это вариант по умолчанию.") : words;
    }
}
