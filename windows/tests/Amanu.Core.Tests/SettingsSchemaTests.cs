using System.Text.Json.Nodes;
using Amanu.Core.Configuration;
using Amanu.Core.Localization;

namespace Amanu.Core.Tests;

public sealed class SettingsSchemaTests
{
    private static IEnumerable<string> Leaves(JsonObject node, string prefix = "")
    {
        foreach (var (key, value) in node)
        {
            var path = prefix.Length == 0 ? key : $"{prefix}.{key}";
            if (value is JsonObject nested && path != "on_stop")
                foreach (var leaf in Leaves(nested, path)) yield return leaf;
            else yield return path;
        }
    }

    [Fact]
    public void Every_setting_amanu_reads_is_described_in_the_schema()
    {
        var settings = AppSettings.CreateDefault("C:\\Docs");
        settings.OnStop = new CommandHook { Executable = "x" };
        settings.Transcription.Language = "ru";
        settings.Transcription.AssemblyAi.SpeechModel = "m";
        settings.Summary.Language = "ru";
        settings.Summary.Template = "t";
        settings.Summary.Model = "m";
        settings.Summary.OpenAiCompatible = true;
        settings.SpeakerNames.Model = "m";
        settings.SpeakerNames.PeopleFile = "p";
        settings.Transcription.DiarizationThreshold = 0.6;
        settings.UserName = "me";

        var described = SettingsSchema.Entries.Select(entry => entry.Path).ToHashSet();
        var missing = Leaves(SettingsDocument.ToNode(settings)).Where(path => !described.Contains(path)).ToArray();

        Assert.Empty(missing);
    }

    [Fact]
    public void Every_schema_path_is_a_real_setting()
    {
        var settings = AppSettings.CreateDefault("C:\\Docs");
        settings.OnStop = new CommandHook { Executable = "x" };
        settings.Transcription.Language = "ru";
        settings.Transcription.AssemblyAi.SpeechModel = "m";
        settings.Summary.Language = "ru";
        settings.Summary.Template = "t";
        settings.Summary.Model = "m";
        settings.Summary.OpenAiCompatible = true;
        settings.SpeakerNames.Model = "m";
        settings.SpeakerNames.PeopleFile = "p";
        settings.Transcription.DiarizationThreshold = 0.6;
        settings.UserName = "me";
        var node = SettingsDocument.ToNode(settings);

        foreach (var entry in SettingsSchema.Entries)
            Assert.True(SettingsDocument.Get(node, entry.Path) is not null, entry.Path);
    }

    [Fact]
    public void Every_choice_default_is_one_of_its_options()
    {
        foreach (var entry in SettingsSchema.Entries.Where(entry => entry.Kind == SettingKind.Choice))
        {
            var value = SettingsSchema.DefaultFor(entry.Path, "C:\\Docs")!.GetValue<string>();
            Assert.Contains(value, entry.Options!);
        }
    }

    [Fact]
    public void The_advanced_tab_leaves_out_what_setup_asks()
    {
        var advanced = SettingsSchema.AdvancedSections.SelectMany(section => section.Entries).ToArray();
        Assert.DoesNotContain(advanced, entry => entry.AskedInSetup);
        Assert.Contains(advanced, entry => entry.Path == "auto_record.stop_delay_seconds");
        Assert.DoesNotContain(SettingsSchema.AdvancedSections, section => section.Entries.Count == 0);
        // The echo settings are asked in setup, beside the engine they change the input of.
        Assert.DoesNotContain(advanced, entry => entry.Path is "echo_model" or "offline_echo_cancellation" or "transcript_echo_filter");
    }

    [Fact]
    public void Every_advanced_menu_says_what_each_of_its_options_does()
    {
        foreach (var language in new[] { InterfaceLanguage.English, InterfaceLanguage.Russian })
        {
            using var scope = Localized.Use(language);
            foreach (var entry in SettingsSchema.AdvancedSections.SelectMany(section => section.Entries)
                         .Where(entry => entry.Kind == SettingKind.Choice))
            {
                Assert.NotNull(entry.OptionHelp);
                Assert.Equal(entry.Options!.Order(), entry.OptionHelp!.Keys.Order());
                Assert.All(entry.OptionHelp.Values, words => Assert.False(string.IsNullOrWhiteSpace(words), entry.Path));
            }
        }
    }

    [Fact]
    public void The_setup_echo_menu_offers_every_model_with_the_default_first()
    {
        var fallback = SettingsSchema.DefaultFor("echo_model", "C:\\Docs")!.GetValue<string>();
        Assert.Equal(fallback, EchoChoices.Main[0]);
        Assert.All(EchoChoices.Main, model => Assert.Contains(model, AppSettings.EchoModels));
        Assert.EndsWith(" It is the default.", EchoChoices.Describe(fallback, fallback));
        Assert.DoesNotContain("default", EchoChoices.Describe("sonora", fallback));
        Assert.Contains("ECHO_REMOVAL.md", EchoChoices.Describe("sonora-long", fallback));
    }

    [Fact]
    public void Both_languages_say_something_for_every_entry()
    {
        foreach (var language in new[] { InterfaceLanguage.English, InterfaceLanguage.Russian })
        {
            using var scope = Localized.Use(language);
            foreach (var entry in SettingsSchema.Entries)
            {
                Assert.False(string.IsNullOrWhiteSpace(entry.Label), entry.Path);
                Assert.False(string.IsNullOrWhiteSpace(entry.Help), entry.Path);
            }
        }
    }

    [Theory]
    [InlineData(null, "ru-RU", InterfaceLanguage.Russian)]
    [InlineData("auto", "pl-PL", InterfaceLanguage.English)]
    [InlineData("en", "ru-RU", InterfaceLanguage.English)]
    [InlineData("ru", "en-US", InterfaceLanguage.Russian)]
    [InlineData("de", "ru", InterfaceLanguage.Russian)]
    public void Interface_language_follows_the_config_then_windows(string? configured, string system, InterfaceLanguage expected)
    {
        Assert.Equal(expected, Localized.Choose(configured, system));
    }
}
