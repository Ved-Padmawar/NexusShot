using NexusShot.Core;

namespace NexusShot.Tests;

/// <summary>What Find sensitive text covers, and what it leaves alone.</summary>
public class SensitiveTextTests
{
    /// <summary>Words laid out left to right, 10 pixels per character plus a space, on one line.</summary>
    private static List<TextWord> Line(string text, double y = 100)
    {
        var words = new List<TextWord>();
        var x = 0.0;
        foreach (var word in text.Split(' '))
        {
            words.Add(new TextWord(word, new Rect(x, y, word.Length * 10, 20)));
            x += (word.Length + 1) * 10;
        }
        return words;
    }

    private static List<Rect> Find(params string[] lines) =>
        SensitiveText.Find([.. lines.Select((line, i) => (IReadOnlyList<TextWord>)Line(line, i * 40))], padding: 0);

    [Fact]
    public void AnEmailIsCoveredAndItsNeighboursAreNot()
    {
        var area = Assert.Single(Find("contact jane.doe+work@example.co.uk today"));
        Assert.Equal(new Rect(80, 0, 270, 20), area);
    }

    [Fact]
    public void ACardNumberInGroupsIsCoveredAsOneBlock()
    {
        var area = Assert.Single(Find("Card 4111 1111 1111 1111 exp"));
        Assert.Equal(new Rect(50, 0, 190, 20), area);
    }

    [Theory]
    [InlineData("Order 1234 5678 9012 3456")]      // sixteen digits that fail the card checksum
    [InlineData("Ticket 202609251234")]            // a bare run of digits
    [InlineData("Version 1.2.3 released")]
    public void NumbersThatAreNotSecretsAreLeftAlone(string text) => Assert.Empty(Find(text));

    [Theory]
    [InlineData("Call +44 20 7946 0958 now")]
    [InlineData("Call (555) 123-4567 now")]
    [InlineData("Host 192.168.10.24 up")]
    [InlineData("Key Zq51HxAbCdEfGh1234567890XyZw")]
    public void PhoneNumbersAddressesAndKeysAreCovered(string text) => Assert.NotEmpty(Find(text));

    [Fact]
    public void EveryKeyInAnEnvFileIsCoveredHoweverRecognitionSplitIt()
    {
        var areas = Find(
            "# OPENAI_API_KEY=sk-proj-Ab12Cd34Ef56Gh78",
            "OPENAI_API_KEY=sk-proj-Ab12 Cd34Ef 56Gh78Ij90",
            "AZURE_OPENAI_API_KEY= 3f9a 1c0b7e 2d4f",
            "AZURE_OPENAI_ENDPOINT=https://example.openai.azure.com");

        Assert.Equal([0.0, 40.0, 80.0], areas.Select(area => area.Y));
    }

    [Fact]
    public void AnAssignmentKeepsItsNameVisible()
    {
        // "API_KEY=" is 8 of the word's 20 characters, 10 pixels each.
        var area = Assert.Single(Find("API_KEY=abcdefghijkl"));
        Assert.Equal(new Rect(80, 0, 120, 20), area);
    }

    [Theory]
    [InlineData("\"apiKey\": \"a1b2c3d4\"")]
    [InlineData("password: hunter22")]
    [InlineData("clientSecret = x7Y8z9W0")]
    public void ConfigValuesNamedAsSecretsAreCovered(string text) => Assert.NotEmpty(Find(text));

    [Theory]
    [InlineData("Keyboard: US International")]
    [InlineData("Press the key: Enter")]
    public void ProseThatMentionsAKeyIsLeftAlone(string text) => Assert.Empty(Find(text));

    [Fact]
    public void EachLineIsSearchedOnItsOwn()
    {
        var areas = Find("mail a@b.com", "nothing here", "ip 10.0.0.1");
        Assert.Equal([0.0, 80.0], areas.Select(area => area.Y));
    }
}
