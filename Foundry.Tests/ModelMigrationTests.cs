using Foundry.Core.Ai;
using Foundry.Core.Config;

namespace Foundry.Tests;

// The selected model is PERSISTED in config.json, so shipping a newer catalog does nothing on its own for
// anyone who has already run the app. A real install was found pinned to "claude-opus-4-7" — a model that
// is no longer in the catalog at all, so the picker showed a value it did not offer and every request kept
// asking for a retired model.
public class ModelMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foundry-cfg-" + Guid.NewGuid().ToString("N")[..8]);

    public ModelMigrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    // ---- the catalog itself ----

    [Fact]
    public void GenerationUsesOpus5() =>
        Assert.Equal("claude-opus-5", ModelCatalog.GenerationModelId);

    [Fact]
    public void ChatUsesSonnet5() =>
        Assert.Equal("claude-sonnet-5", ModelCatalog.DefaultModelId);

    [Fact]
    public void BothDefaultsAreActuallyOfferedInThePicker()
    {
        Assert.Contains(ModelCatalog.Fallback, m => m.Id == ModelCatalog.GenerationModelId);
        Assert.Contains(ModelCatalog.Fallback, m => m.Id == ModelCatalog.DefaultModelId);
    }

    [Fact]
    public void ANewConfigStartsOnOpus5() =>
        Assert.Equal("claude-opus-5", new AppConfig().ModelId);

    // ---- migration preserves the TIER the user chose ----

    [Theory]
    [InlineData("claude-opus-4-7", "claude-opus-5")]     // the id found on the real install
    [InlineData("claude-opus-4-8", "claude-opus-5")]
    [InlineData("claude-opus-4-6", "claude-opus-5")]
    [InlineData("claude-sonnet-4-6", "claude-sonnet-5")]
    [InlineData("claude-sonnet-4-5", "claude-sonnet-5")]
    public void ARetiredModelMovesToItsCurrentEquivalent(string old, string expected) =>
        Assert.Equal(expected, ModelCatalog.Migrate(old));

    // Someone who deliberately picked the cheap fast model must not be silently moved to the expensive one.
    [Fact]
    public void ADeliberateHaikuChoiceIsLeftAlone() =>
        Assert.Equal("claude-haiku-4-5-20251001", ModelCatalog.Migrate("claude-haiku-4-5-20251001"));

    [Fact]
    public void CurrentModelsAreUntouched()
    {
        foreach (var m in ModelCatalog.Fallback)
            Assert.Equal(m.Id, ModelCatalog.Migrate(m.Id));
    }

    // A model this build has never heard of may simply be NEWER than this build. Rewriting it would
    // downgrade someone who is ahead of us.
    [Fact]
    public void AnUnrecognisedModelIsLeftAlone() =>
        Assert.Equal("claude-opus-6-future", ModelCatalog.Migrate("claude-opus-6-future"));

    [Fact]
    public void NoChoiceMeansTheGenerationDefault()
    {
        Assert.Equal(ModelCatalog.GenerationModelId, ModelCatalog.Migrate(null));
        Assert.Equal(ModelCatalog.GenerationModelId, ModelCatalog.Migrate("   "));
    }

    // ---- through the real load path ----

    [Fact]
    public void LoadingAConfigPinnedToARetiredModel_MovesItToOpus5()
    {
        var path = Path.Combine(_dir, "config.json");
        // Byte-for-byte the shape found on the real install.
        File.WriteAllText(path, """
        {
          "ModelId": "claude-opus-4-7",
          "ChatModelId": null,
          "MaxOutputTokens": 8192,
          "Temperature": 1,
          "FirmwarePlatform": "MicroPython",
          "OutputFolder": "C:\\Users\\x\\Documents\\Foundry",
          "EnclosureFormat": "STL",
          "Units": "mm"
        }
        """);

        var cfg = ConfigStore.Load(path);

        Assert.Equal("claude-opus-5", cfg.ModelId);
        // Everything else the user CHOSE must survive; the token cap is itself migrated (see below).
        Assert.Equal(16384, cfg.MaxOutputTokens);
        Assert.Equal("MicroPython", cfg.FirmwarePlatform);
        Assert.Equal("STL", cfg.EnclosureFormat);
    }

    // A null ChatModelId means "use the fast default"; migrating it would promote every chat edit to the
    // expensive generation model.
    [Fact]
    public void ANullChatModelIsNotPromotedToTheGenerationModel()
    {
        var path = Path.Combine(_dir, "chat-null.json");
        File.WriteAllText(path, """{ "ModelId": "claude-opus-4-7", "ChatModelId": null }""");

        var cfg = ConfigStore.Load(path);

        Assert.NotEqual(ModelCatalog.GenerationModelId, cfg.ChatModelId);
        Assert.True(string.IsNullOrWhiteSpace(cfg.ChatModelId), $"expected no chat override, got '{cfg.ChatModelId}'");
    }

    [Fact]
    public void AnExplicitRetiredChatModelIsMigrated()
    {
        var path = Path.Combine(_dir, "chat-set.json");
        File.WriteAllText(path, """{ "ModelId": "claude-opus-4-8", "ChatModelId": "claude-sonnet-4-6" }""");

        var cfg = ConfigStore.Load(path);

        Assert.Equal("claude-opus-5", cfg.ModelId);
        Assert.Equal("claude-sonnet-5", cfg.ChatModelId);
    }
}

// Two settings were persisted, then superseded by a later default — and the saved value won, so installs
// never received fixes that were shipped as default changes. Found by reading the app log.
public class SettingsMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "foundry-set-" + Guid.NewGuid().ToString("N")[..8]);

    public SettingsMigrationTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }

    private AppConfig LoadJson(string json)
    {
        var path = Path.Combine(_dir, Guid.NewGuid().ToString("N")[..6] + ".json");
        File.WriteAllText(path, json);
        return ConfigStore.Load(path);
    }

    // 69f343a raised the default 8192 -> 16384, titled "fix generation truncation on complex designs".
    // A config saved before that kept 8192 and kept truncating: the app log shows 43 truncation retries and
    // 21 "firmware pass failed ... using deterministic fallback" in one day.
    [Fact]
    public void TheSupersededTokenCapIsRaised() =>
        Assert.Equal(16384, LoadJson("""{ "MaxOutputTokens": 8192 }""").MaxOutputTokens);

    // Narrow on purpose: anything that is not the old default is a real choice.
    [Theory]
    [InlineData(4096)]
    [InlineData(12000)]
    [InlineData(32768)]
    [InlineData(64000)]
    public void ADeliberateTokenCapIsLeftAlone(int chosen) =>
        Assert.Equal(chosen, LoadJson($$"""{ "MaxOutputTokens": {{chosen}} }""").MaxOutputTokens);

    [Fact]
    public void ANewConfigAlreadyHasTheRaisedCap() =>
        Assert.Equal(16384, new AppConfig().MaxOutputTokens);

    // The real file from this machine: retired model AND superseded cap, everything else untouched.
    [Fact]
    public void TheRealWorldConfigIsFullyMigrated()
    {
        var cfg = LoadJson("""
        {
          "ModelId": "claude-opus-4-7",
          "ChatModelId": null,
          "MaxOutputTokens": 8192,
          "Temperature": 1,
          "FirmwarePlatform": "MicroPython",
          "OutputFolder": "D:/Foundry",
          "EnclosureFormat": "STL",
          "Units": "mm"
        }
        """);

        Assert.Equal("claude-opus-5", cfg.ModelId);
        Assert.Equal(16384, cfg.MaxOutputTokens);
        Assert.Equal("MicroPython", cfg.FirmwarePlatform);
        Assert.Equal("STL", cfg.EnclosureFormat);
        Assert.Equal(1.0, cfg.Temperature);
    }
}
