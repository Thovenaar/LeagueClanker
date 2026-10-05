using LeagueClanker.Core;
using LeagueClanker.Core.Augments;
using Xunit;

namespace LeagueClanker.Core.Tests;

public class AugmentHudTests
{
    // A 2560x1440 screen that's black except for the HUD's card slots, cut from a real Mayhem screenshot: It's Killing
    // Time, Pinball and Leg Day taken, two slots empty, and an offer dimming the screen.
    private static GrayImage RealScreen()
    {
        var crop = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", "hud-1440p-640x1280-320x160.gray"));
        var pixels = new float[2560 * 1440];
        for (var y = 0; y < 160; y++)
            for (var x = 0; x < 320; x++)
                pixels[(1280 + y) * 2560 + 640 + x] = crop[y * 320 + x];
        return new GrayImage(2560, 1440, pixels);
    }

    private static CardIcons RealIcon(string name, string file) =>
        new(name, [new GrayImage(64, 64, File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "Data", $"icon-{file}-64.gray")).Select(b => (float)b).ToArray())]);

    private static readonly CardIcons LegDay = RealIcon("Leg Day", "leg-day");
    private static readonly CardIcons EscapePlan = RealIcon("Escape Plan", "escape-plan");
    private static readonly CardIcons Pinball = RealIcon("Pinball", "pinball");
    private static readonly CardIcons ItsKillingTime = RealIcon("It's Killing Time", "its-killing-time");

    [Fact]
    public void Read_FindsTheNewestCard_AndTellsItFromALookAlike()
    {
        var read = AugmentHud.Read(RealScreen(), [EscapePlan, Pinball, LegDay]);

        Assert.Equal("Leg Day", read.Card); // Escape Plan has the same running figure in another frame
        Assert.Equal(3, read.Filled);
        Assert.Equal(1.0, read.HudScale);
        Assert.InRange(read.Score - read.RunnerUp, AugmentHud.MinMargin, 1);
    }

    [Fact]
    public void Read_IgnoresACardThatWasThereBeforeTheOfferClosed()
    {
        var screen = RealScreen();

        var read = AugmentHud.Read(screen, [EscapePlan, Pinball, LegDay], before: screen); // hid the offer without picking

        Assert.Null(read.Card);
    }

    [Fact]
    public void Read_CountsANewSlotFromBeforeTheOfferClosed()
    {
        var screen = RealScreen();
        var before = new GrayImage(screen.Width, screen.Height, (float[])screen.Pixels.Clone());
        foreach (var slot in AugmentHud.Slots(2560, 1440).Skip(2).Take(1))
            for (var y = slot.Y - 2; y < slot.Y + slot.Size + 2; y++)
                Array.Fill(before.Pixels, 8f, y * 2560 + slot.X - 2, slot.Size + 4); // the third slot still empty

        Assert.Equal("Leg Day", AugmentHud.Read(screen, [EscapePlan, Pinball, LegDay], before).Card);
    }

    [Fact]
    public void Read_DoesNotGuess_WhenNoOfferedCardIsInTheNewestSlot()
    {
        var read = AugmentHud.Read(RealScreen(), [EscapePlan, Pinball, ItsKillingTime]); // Leg Day wasn't offered

        Assert.Null(read.Card);
    }

    [Fact]
    public void Read_DoesNotGuess_WhenACardThatWasntOfferedMatchesBetter()
    {
        // A look-alike of Leg Day was offered, but Leg Day is in the slot: the offer was misread.
        var lookAlike = new CardIcons("Flashy", [Blend(LegDay.Icons[0], EscapePlan.Icons[0], 0.5f)]);
        Assert.Equal("Flashy", AugmentHud.Read(RealScreen(), [lookAlike, Pinball, ItsKillingTime]).Card);

        var read = AugmentHud.Read(RealScreen(), [lookAlike, Pinball, ItsKillingTime], everyCard: [lookAlike, Pinball, ItsKillingTime, LegDay]);

        Assert.Null(read.Card);
        Assert.Contains("Leg Day", read.Why);
    }

    [Fact]
    public void Read_KeepsTheOfferedCard_WhenACardThatWasntOfferedSharesItsImage()
    {
        var killSecured = LegDay with { Name = "Kill Secured" }; // the same image as Leg Day

        var read = AugmentHud.Read(RealScreen(), [EscapePlan, Pinball, LegDay], everyCard: [EscapePlan, Pinball, LegDay, killSecured]);

        Assert.Equal("Leg Day", read.Card);
    }

    [Fact]
    public void Read_DoesNotGuess_WhenTwoOfferedCardsShareAnIcon()
    {
        // Riot gives It's Killing Time, Surge Field and Final Form one image.
        var surgeField = LegDay with { Name = "Surge Field" };

        var read = AugmentHud.Read(RealScreen(), [EscapePlan, surgeField, LegDay]);

        Assert.Null(read.Card);
        Assert.Contains("same icon", read.Why);
    }

    [Theory]
    [InlineData(1920, 1080, 1.0)]
    [InlineData(2560, 1440, 0.8)]
    [InlineData(3440, 1440, 1.0)] // ultrawide: the HUD stays in the middle
    public void Read_FindsTheSlots_AtOtherScreenSizesAndHudScales(int width, int height, double hudScale)
    {
        var screen = new GrayImage(width, height, Enumerable.Repeat(8f, width * height).ToArray());
        var slots = AugmentHud.Slots(width, height, hudScale);
        Paint(screen, slots[0], Pinball.Icons[0]);
        Paint(screen, slots[1], LegDay.Icons[0]);

        var read = AugmentHud.Read(screen, [EscapePlan, LegDay, ItsKillingTime]);

        Assert.Equal("Leg Day", read.Card);
        Assert.Equal(hudScale, read.HudScale);
    }

    [Fact]
    public void Read_SaysSo_WhenEverySlotIsEmpty()
    {
        var screen = new GrayImage(2560, 1440, Enumerable.Repeat(8f, 2560 * 1440).ToArray());

        var read = AugmentHud.Read(screen, [EscapePlan, LegDay, Pinball]);

        Assert.Null(read.Card);
        Assert.Equal(0, read.Filled);
    }

    [Fact]
    public void Slots_FillThreeOnTopThenTwoBelow()
    {
        var slots = AugmentHud.Slots(2560, 1440);

        Assert.Equal(new HudSlot(690, 1311, 53), slots[0]);
        Assert.Equal(slots[0].Y, slots[2].Y);
        Assert.Equal(slots[0].X, slots[3].X);
        Assert.True(slots[3].Y > slots[0].Y);
    }

    private static GrayImage Blend(GrayImage a, GrayImage b, float share) =>
        new(a.Width, a.Height, a.Pixels.Zip(b.Pixels, (x, y) => x * share + y * (1 - share)).ToArray());

    // Draws an icon into a slot the way the game does: tinted darker, on the slot's black.
    private static void Paint(GrayImage screen, HudSlot slot, GrayImage icon)
    {
        var scaled = icon.Square(slot.Size);
        for (var y = 0; y < slot.Size; y++)
            for (var x = 0; x < slot.Size; x++)
                screen.Pixels[(slot.Y + y) * screen.Width + slot.X + x] = 5 + scaled[x, y] * 0.6f;
    }
}

public class PickVotesTests
{
    [Fact]
    public void Best_IsTheCardTheHudSaw_OverTheClick()
    {
        var votes = new PickVotes();
        votes.Click("Recursion");
        votes.Read("Celestial Body"); // a read counts 1, the click a half

        Assert.Equal("Celestial Body", votes.Best);
        Assert.True(votes.Seen("Celestial Body"));
    }

    [Fact]
    public void Best_FallsBackOnTheClick_WhenTheHudCantTell()
    {
        var votes = new PickVotes();
        votes.Click("Recursion");

        Assert.Equal("Recursion", votes.Best);
        Assert.False(votes.Seen("Recursion"));
    }

    [Fact]
    public void Best_LetsTheClickBreakATie()
    {
        var votes = new PickVotes();
        votes.Read("Recursion");
        votes.Read("Celestial Body");
        votes.Click("Celestial Body");

        Assert.Equal("Celestial Body", votes.Best);
    }

    [Fact]
    public void Best_IsNull_OnATieWithoutAClick()
    {
        var votes = new PickVotes();
        votes.Read("Recursion");
        votes.Read("Celestial Body");

        Assert.Null(votes.Best);
    }

    [Fact]
    public void Sure_NeedsALeadOfTwo()
    {
        var votes = new PickVotes();
        votes.Read("Recursion");
        votes.Click("Recursion");
        Assert.Null(votes.Sure); // 1.5 votes

        votes.Read("Recursion");
        Assert.Equal("Recursion", votes.Sure);

        votes.Read("Celestial Body");
        Assert.Null(votes.Sure); // 2.5 against 1
    }
}

public class AugmentIconsTests
{
    // Trimmed from Community Dragon's cherry-augments.json: the same card in Arena (id 103) and Mayhem (1103), and a quest.
    private const string Json = """
        [
          { "id": 103, "nameTRA": "Bread And Butter", "augmentSmallIconPath": "/lol-game-data/assets/ASSETS/UX/Cherry/Augments/Icons/BreadAndButter_small.png" },
          { "id": 1103, "nameTRA": "Bread And Butter", "augmentSmallIconPath": "/lol-game-data/assets/ASSETS/UX/Kiwi/Augments/Icons/GenericAbilityAugmentIcon_Gold.png" },
          { "id": 1156, "nameTRA": "Wooglet's Witchcap", "augmentSmallIconPath": "/lol-game-data/assets/ASSETS/UX/Cherry/Augments/Icons/Quest_WoogletsWitchcap_small.png" },
          { "id": 1999, "nameTRA": "", "augmentSmallIconPath": "/lol-game-data/assets/ASSETS/UX/Kiwi/Augments/Icons/Nameless.png" }
        ]
        """;

    [Fact]
    public void Urls_TakeMayhemCardsFromId1000_AsLowercaseDownloadUrls()
    {
        var mayhem = AugmentIcons.Urls(Json, AugmentSet.Mayhem);

        Assert.Equal(
            ["https://raw.communitydragon.org/latest/plugins/rcp-be-lol-game-data/global/default/assets/ux/kiwi/augments/icons/genericabilityaugmenticon_gold.png"],
            AugmentIcons.For(mayhem, "Bread And Butter"));
        Assert.Equal(2, mayhem.Count); // the nameless card is left out
    }

    [Fact]
    public void Urls_TakeArenaCardsBelowId1000()
    {
        var arena = AugmentIcons.Urls(Json, AugmentSet.Arena);

        Assert.EndsWith("/breadandbutter_small.png", Assert.Single(AugmentIcons.For(arena, "Bread And Butter")));
    }

    [Fact]
    public void For_FindsQuestCardsWithoutTheirPrefix()
    {
        var mayhem = AugmentIcons.Urls(Json, AugmentSet.Mayhem);

        Assert.Single(AugmentIcons.For(mayhem, "Quest: Wooglet's Witchcap"));
        Assert.Empty(AugmentIcons.For(mayhem, "Not A Card"));
    }
}

public class GrayImageTests
{
    [Fact]
    public void FromBgra_PutsTransparentPixelsOnBlack()
    {
        var image = GrayImage.FromBgra([90, 90, 90, 255, 200, 200, 200, 0], 2, 1, useAlpha: true);

        Assert.Equal([90f, 0f], image.Pixels);
    }

    [Fact]
    public void Resize_KeepsAFlatImageFlat()
    {
        var image = new GrayImage(64, 64, Enumerable.Repeat(100f, 64 * 64).ToArray());

        Assert.All(image.Square(53).Pixels, p => Assert.Equal(100f, p, 0.01f));
        Assert.All(image.Resize(80, 40).Pixels, p => Assert.Equal(100f, p, 0.01f));
    }
}
